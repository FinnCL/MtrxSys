using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Api.BackgroundServices;

/// <summary>
/// "Ouvir" SEM WAHA no modo Emulador: lê as mensagens recebidas direto do banco do aparelho e as entrega
/// à MESMA ingestão que o webhook usa (Chat, opt-out, marcação de quem respondeu).
///
/// <para>POR QUE EXISTE: o emulador é o dono da conta, mas quem escutava era o WAHA — um aparelho
/// conectado (companion) vinculado ao chip só pra receber. Isso obrigava a parear um companion a cada
/// troca de chip e deixava o "ouvir" morto sempre que a sessão do WAHA caía (que foi o estado real em
/// 2026-07-25: sessão FAILED, `me: null`, nada chegando). Lendo do aparelho, receber passa a depender só
/// do que já é indispensável — o próprio emulador.</para>
///
/// <para>NÃO substitui o webhook nos 9 stacks WahaOnly: lá o WAHA é quem envia e recebe, e este serviço
/// simplesmente não faz nada (checa o modo a cada ciclo, não no boot — o modo é trocável em runtime).</para>
/// </summary>
public sealed class EmulatorInboundPollerService(
    IServiceScopeFactory scopes,
    IOptions<DispatchOptions> dispatchOpts,
    ILogger<EmulatorInboundPollerService> log) : BackgroundService
{
    // 20s: o "ouvir" não é tempo real (opt-out e respostas toleram bem esse atraso) e cada ciclo custa
    // duas chamadas adb — o MESMO canal que o disparo usa pra enviar pela UI. Poll agressivo aqui vira
    // envio lento lá. O `stat` do snapshot já evita copiar o banco quando nada mudou.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    // Tipos de `message_type` tratados como MÍDIA. Só `1` (imagem) foi observado no aparelho — junto com
    // `0` (texto) e `7` (sistema). Os demais seguem a convenção do app; se algum estiver errado, o efeito
    // é uma mensagem de mídia entrar como "evento vazio" e ser ignorada, que é a falha barata. O caminho
    // caro (evento de sistema virar conversa) está fechado porque o desconhecido NÃO entra nesta lista.
    private static readonly HashSet<int> MediaMessageTypes = [1, 2, 3, 9, 13, 15, 20];

    // Lote pequeno de propósito: um lote gigante depois de um período parado seguraria o ciclo por
    // minutos com o adb ocupado. O que sobrar vem no ciclo seguinte, 20s depois.
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // um ciclo que falha não pode derrubar o serviço: tenta de novo em 20s
            catch (Exception ex)
            {
                log.LogWarning(ex, "Poller de entrada do emulador falhou neste ciclo; tenta de novo.");
            }
#pragma warning restore CA1031

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var stateRepo = sp.GetRequiredService<ISystemStateRepository>();

        var state = await stateRepo.GetAsync(ct);
        if (state.DispatchMode != PhoneDispatchMode.Emulator)
        {
            return; // WahaOnly: quem ouve é o webhook do WAHA
        }

        var phone = sp.GetRequiredService<IPhoneOrchestrator>();

        // PRIMEIRA VEZ: posiciona o marco no fim, sem ingerir. Começar do zero num aparelho COM histórico
        // (imagem-ouro feita de um device já usado, chip reaproveitado) trataria conversas antigas como
        // recém-chegadas: chats velhos no Chat e — o que de fato machuca — gente marcada como "respondeu"
        // por causa de mensagem de meses atrás, indo parar na fila quente. Disparar pra quem não fala com
        // você há meses é perfil de queima de chip. Perde-se a janela de até 20s antes do 1º ciclo; é um
        // preço muito menor que reprocessar histórico.
        // Uma vez por processo (e de novo após a auto-cura zerar o marco): com a caixa vazia o marco fica
        // legitimamente em 0, e sondar a cada ciclo seria uma consulta adb por 20s sem nada a mostrar.
        if (!_seekedToEnd && state.InboundLastRowId == 0)
        {
            _seekedToEnd = true;
            var last = await phone.GetLastInboundRowIdAsync(ct);
            if (last > 0)
            {
                state.AdvanceInboundMarker(last);
                await stateRepo.UpdateAsync(state, ct);
                await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(ct);
                log.LogInformation(
                    "Poller: escuta iniciada a partir do id {RowId} (histórico anterior do aparelho ignorado).",
                    last);
                return;
            }
            // Caixa vazia: não há histórico pra pular, então a leitura incremental normal já é segura.
        }

        var messages = await phone.ReadInboundMessagesAsync(state.InboundLastRowId, BatchSize, ct);
        if (messages.Count == 0)
        {
            await SelfHealMarkerIfOrphanedAsync(phone, stateRepo, sp, state, ct);
            return;
        }
        _emptyCycles = 0;

        var ingestion = sp.GetRequiredService<IWebhookIngestionService>();
        var session = dispatchOpts.Value.SessionId;

        // AVANÇA SÓ ATÉ O ÚLTIMO QUE DEU CERTO. Se a ingestão estourar no meio do lote, o marco fica no
        // anterior e o restante volta no próximo ciclo — reprocessar é seguro (a ingestão deduplica por
        // id de mensagem), PULAR não seria: uma resposta perdida é um opt-out ignorado, e disparar pra
        // quem pediu pra sair é justamente o que queima chip.
        var lastOk = state.InboundLastRowId;
        foreach (var m in messages)
        {
            ct.ThrowIfCancellationRequested();
            await ingestion.IngestAsync(ToWebhookEvent(m, session), ct);
            lastOk = m.RowId;
        }

        // `state` é a MESMA instância que a ingestão enxergou: o repositório usa FindAsync, que devolve a
        // entidade já rastreada no contexto do escopo em vez de reconsultar. Reler aqui seria uma chamada
        // a mais devolvendo o mesmo objeto.
        state.AdvanceInboundMarker(lastOk);
        await stateRepo.UpdateAsync(state, ct);
        await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(ct);

        log.LogInformation("Poller: {Count} mensagem(ns) do emulador ingerida(s); marco em {RowId}.",
            messages.Count, lastOk);
    }

    // Ciclos seguidos sem nada. Serve pra decidir QUANDO vale gastar uma consulta extra investigando se
    // o silêncio é normal (ninguém escreveu) ou patológico (marco órfão).
    private int _emptyCycles;

    // Se já posicionamos o marco no fim nesta instância. A auto-cura rearma isto: quando o banco do
    // aparelho é recriado, o marco volta a 0 e a escuta precisa se reposicionar no fim do banco NOVO —
    // senão o histórico que veio com o aparelho recriado seria ingerido como se fosse recente.
    private bool _seekedToEnd;

    // 15 ciclos ≈ 5 min de silêncio. Curto o bastante pra recuperar rápido, longo o bastante pra não
    // sondar a cada 20s numa conta que simplesmente não recebe mensagem.
    private const int ProbeAfterEmptyCycles = 15;

    /// <summary>Recupera de um marco ÓRFÃO — o modo de falha que mata o "ouvir" sem dar erro.</summary>
    /// <remarks>
    /// O marco é um `message._id` do banco do APARELHO. Se esse banco for recriado (troca de chip,
    /// limpeza pela imagem-ouro, `pm clear`), a numeração recomeça do 1 — e um marco antigo em, digamos,
    /// 5000 faz o poller pedir "o que veio depois do 5000" num banco cujo maior id é 3. Resultado: lote
    /// sempre vazio, para sempre, sem erro e sem log. Nenhuma mensagem chega e ninguém percebe.
    /// <para>O reconcile do dispatcher já zera o marco quando detecta troca de chip, mas ele roda em OUTRO
    /// processo: se o dispatcher estiver parado quando o aparelho for trocado, o reset não acontece. Esta
    /// sonda torna o poller capaz de se curar sozinho, sem depender de quem não controla.</para>
    /// Só roda após ~5 min de silêncio: em operação normal o custo é zero.
    /// </remarks>
    private async Task SelfHealMarkerIfOrphanedAsync(
        IPhoneOrchestrator phone, ISystemStateRepository stateRepo, IServiceProvider sp,
        SystemStateAggregate state, CancellationToken ct)
    {
        _emptyCycles++;
        if (state.InboundLastRowId == 0 || _emptyCycles < ProbeAfterEmptyCycles)
        {
            return;
        }
        _emptyCycles = 0;

        // Lê do começo: se a mensagem mais antiga do aparelho tem id MENOR que o marco, o banco foi
        // recriado sob os nossos pés. Se não vier nada, o banco está vazio — e um marco alto sobre um
        // banco vazio também é órfão.
        var oldest = await phone.ReadInboundMessagesAsync(0, 1, ct);
        if (oldest.Count > 0 && oldest[0].RowId >= state.InboundLastRowId)
        {
            return; // silêncio legítimo
        }

        log.LogWarning(
            "Marco de entrada órfão ({Marco}): o banco do aparelho foi recriado. Zerando pra voltar a receber.",
            state.InboundLastRowId);
        state.ResetInboundMarker();
        _seekedToEnd = false; // reposiciona no fim do banco NOVO em vez de ingerir o histórico dele
        await stateRepo.UpdateAsync(state, ct);
        await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(ct);
    }

    /// <summary>Traduz a mensagem do aparelho para a MESMA forma que o webhook do WAHA entrega.</summary>
    /// <remarks>
    /// Reaproveitar o contrato (em vez de criar um caminho paralelo de ingestão) faz as duas fontes
    /// passarem pelas mesmas regras de opt-out, conversa e marcação de respondente. Um segundo caminho
    /// divergiria com o tempo, e a divergência apareceria como "opt-out funciona no stack B mas não no A".
    /// <para>Três conversões que NÃO são óbvias:</para>
    /// <list type="bullet">
    /// <item>Id estável <c>emu:&lt;rowId&gt;</c> — a deduplicação da ingestão é por id, então reprocessar
    /// o mesmo lote não duplica no Chat.</item>
    /// <item>Timestamp em SEGUNDOS — o aparelho guarda em ms e a ingestão faz
    /// <c>FromUnixTimeSeconds</c>. Sem dividir, toda mensagem entraria com data no ano 58500.</item>
    /// <item><c>Participant</c> só em GRUPO — é a convenção do WAHA que o <c>ResolveAuthorPhone</c>
    /// espera: em 1:1 o autor é a própria conversa; em grupo, é o participante.</item>
    /// </list>
    /// </remarks>
    private static WahaWebhookEvent ToWebhookEvent(PhoneInboundMessage m, string session)
    {
        var isGroup = m.ChatJid.EndsWith("@g.us", StringComparison.Ordinal);
        return new WahaWebhookEvent(
            WahaEvents.Message,
            session,
            new WahaMessagePayload(
                Id: $"emu:{m.RowId}",
                Timestamp: m.Timestamp / 1000,
                From: m.ChatJid,
                To: null,
                FromMe: false,
                Body: m.Text,
                // Mídia por TIPO CONHECIDO, não por ausência de texto. A inferência "sem texto ⇒ mídia"
                // marcaria mensagem de SISTEMA (type 7, visto no aparelho) como mídia, criando conversa
                // a partir de um evento que nem é mensagem. Com o allowlist, o desconhecido cai em
                // `Body: null` + `HasMedia: false`, e a ingestão já descarta evento vazio — o padrão
                // seguro é ignorar, não adivinhar.
                HasMedia: MediaMessageTypes.Contains(m.MessageType),
                Media: null,
                Participant: isGroup ? m.SenderJid : null));
    }
}
