using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Api.BackgroundServices;

/// <summary>"Validar lista" (pré-voo anti-463): passa os contatos <see cref="ContactStage.Lead"/> pelo
/// <see cref="IWahaClient.CheckNumberExistsAsync"/> do WhatsApp e DESCARTA (soft-delete via
/// <see cref="Contact.Discard"/>) os que NÃO têm conta — tirando os inexistentes da fila ANTES do disparo.
/// Reusa a MESMA checagem do envio (que também resolve o 9º dígito BR). Contatos raspados de grupo TÊM
/// conta; o "inexistente" costuma ser forma errada do número — a checagem confirma.
///
/// Roda ON-DEMAND (endpoint), em background, PACED (8-20s entre checagens: validar em rajada = "validação
/// em massa" = sinal de bot + rate-limit). Progresso em memória (perde no restart; é sob demanda). NÃO
/// persiste "já validado": re-rodar re-checa os válidos — o uso previsto é rodar ao importar lista nova.
/// Singleton (não HostedService): o endpoint dispara e lê o mesmo estado.</summary>
public sealed class NumberValidationRunner(
    IServiceScopeFactory scopes,
    IClock clock,
    IHostApplicationLifetime lifetime,
    IOptions<DispatchOptions> dispatchOpts,
    ILogger<NumberValidationRunner> log)
{
    // Se as PRIMEIRAS checagens vierem TODAS indeterminadas, a sessão WhatsApp está fora — não adianta
    // varrer a lista inteira (horas) batendo num WAHA morto. Aborta cedo com aviso.
    private const int SessionDownProbe = 5;

    private int _running;
    private volatile ValidationStatus _status = new(false, 0, 0, 0, 0, 0, null);

    public ValidationStatus Status => _status;

    /// <summary>Dispara a validação se não houver uma em andamento. Retorna false se já está rodando.</summary>
    public bool Start()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return false;
        }
        _status = new ValidationStatus(true, 0, 0, 0, 0, 0, "iniciando");
        _ = Task.Run(RunAsync);
        return true;
    }

    private async Task RunAsync()
    {
        // Token do ciclo de vida do app: no shutdown, para entre itens (não deixa Task órfã girando).
        var ct = lifetime.ApplicationStopping;
        var sessionId = dispatchOpts.Value.SessionId;
        int valid = 0, invalid = 0, uncertain = 0, done = 0;
        try
        {
            // Snapshot leve (id + telefone) do público a validar: Lead, não opt-out. Fecha o escopo antes
            // do loop lento — não segura o DbContext aberto por horas.
            List<(Guid Id, string Phone)> work;
            using (var scope = scopes.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IContactRepository>();
                var list = await repo.ListByFilterAsync(
                    new ContactFilter(Stage: ContactStage.Lead, ExcludeOptedOut: true), ct);
                work = list.Select(c => (c.Id, c.Phone.E164)).ToList();
            }
            _status = new ValidationStatus(true, work.Count, 0, 0, 0, 0, "validando");

            foreach (var (id, phone) in work)
            {
                try
                {
                    // Um escopo por item: a parte lenta é o check-exists (WAHA), não o DB. Mantém cada
                    // escopo minúsculo (sem acumular tracking por horas).
                    using var scope = scopes.CreateScope();
                    var waha = scope.ServiceProvider.GetRequiredService<IWahaClient>();
                    var check = await waha.CheckNumberExistsAsync(sessionId, phone, ct);
                    if (check?.Exists == false)
                    {
                        // Confirmado SEM conta → descarta (soft-delete). O enfileirador já exclui DeletedAt,
                        // então ele some da fila. Reversível (Restore) se for engano. Só descarta no "false"
                        // definitivo — null/indeterminado NÃO descarta (não perde contato bom por hiccup).
                        var repo = scope.ServiceProvider.GetRequiredService<IContactRepository>();
                        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                        var c = await repo.GetByIdAsync(id, ct);
                        if (c is not null && c.Discard(clock.UtcNow))
                        {
                            await uow.SaveChangesAsync(ct);
                        }
                        invalid++;
                    }
                    else if (check?.Exists == true)
                    {
                        valid++;
                    }
                    else
                    {
                        uncertain++; // checagem indisponível/indeterminada — mantém o contato (não arrisca)
                    }
                }
                catch (OperationCanceledException)
                {
                    throw; // shutdown: propaga pro catch de fora (encerra limpo)
                }
                catch (Exception ex)
                {
                    uncertain++;
                    log.LogWarning(ex, "Validação: erro ao checar {Phone}; mantido (indeterminado).", phone);
                }

                done++;
                _status = new ValidationStatus(true, work.Count, done, valid, invalid, uncertain, "validando");

                // Sessão fora: as primeiras N checagens TODAS indeterminadas → aborta (não varre horas à toa).
                if (done >= SessionDownProbe && valid == 0 && invalid == 0)
                {
                    _status = new ValidationStatus(false, work.Count, done, valid, invalid, uncertain,
                        "sessão WhatsApp indisponível — valide com a sessão de pé");
                    log.LogWarning(
                        "Validação abortada: {N} checagens iniciais todas indeterminadas (sessão WhatsApp fora?).", done);
                    return;
                }

                // Pacing anti-probing: 8-20s entre checagens. Validar rápido demais = sinal de robô + estoura
                // a cota do check-exists. Mesma faixa do cooldown de pulados do disparo.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(8_000, 20_001)), ct);
            }

            _status = new ValidationStatus(false, work.Count, done, valid, invalid, uncertain, "concluído");
            log.LogInformation(
                "Validação de números concluída: {Valid} com WhatsApp, {Invalid} descartados (sem conta), "
                + "{Uncertain} indeterminados, de {Total}.", valid, invalid, uncertain, work.Count);
        }
        catch (OperationCanceledException)
        {
            _status = _status with { Running = false, Message = "interrompido (encerrando)" };
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Validação de números falhou.");
            _status = _status with { Running = false, Message = "erro" };
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}

/// <summary>Progresso da validação (em memória), exposto pra a UI.</summary>
public sealed record ValidationStatus(
    bool Running, int Total, int Done, int Valid, int Invalid, int Uncertain, string? Message);
