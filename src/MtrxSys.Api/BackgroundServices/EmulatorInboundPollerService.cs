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
    IClock clock,
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

        // Marcado no PRIMEIRO ciclo em modo Emulador, não no construtor: em WahaOnly o serviço nem chega
        // aqui, e um piso cravado no boot ficaria velho até alguém trocar o modo em runtime.
        _watchSince ??= clock.UtcNow - WatchSinceSkewGrace;

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

        // Piso de tempo que vale SÓ com marco 0 — a única situação em que lemos "desde o começo".
        // Ver o campo `_watchSince`: sem ele, um histórico que APAREÇA depois de termos visto a caixa
        // vazia entraria inteiro como recente.
        //
        // Comparação em MILISSEGUNDOS CRUS, sem construir DateTimeOffset por mensagem, por dois motivos
        // que não são de estilo:
        // 1. `FromUnixTimeMilliseconds` LANÇA fora da faixa (~±253402300799999). Um timestamp corrompido
        //    no banco do aparelho estouraria dentro do laço, o catch de fora pegaria, e o MESMO lote
        //    voltaria a cada 20s — poller travado pra sempre, sem nunca alcançar as mensagens novas.
        // 2. Evita alocação por item num laço que roda a cada 20s.
        var floorMs = state.InboundLastRowId == 0 ? _watchSince?.ToUnixTimeMilliseconds() : null;

        // AVANÇA SÓ ATÉ O ÚLTIMO QUE DEU CERTO. Se a ingestão falhar no meio do lote, o marco para no
        // anterior e o restante volta no próximo ciclo — PULAR não seria seguro: uma resposta perdida é um
        // opt-out ignorado, e disparar pra quem pediu pra sair é justamente o que queima chip.
        //
        // Reprocessar é barato e correto porque a ingestão deduplica ANTES de escrever: ela busca o
        // `waMessageId` (aqui, o `emu:{rowId}` estável) e retorna em silêncio se já existe. Verificado —
        // não é dedupe por exceção de constraint, que faria o "tentar de novo" virar erro em cascata.
        var lastOk = state.InboundLastRowId;
        var skippedAsHistory = 0;
        var poisoned = 0;
        var ingested = 0;
        foreach (var m in messages)
        {
            ct.ThrowIfCancellationRequested();
            // `m.Timestamp > 0` É A PARTE QUE IMPORTA: a consulta faz `coalesce(m.timestamp, 0)`, então
            // hora AUSENTE chega como 0 — e 0 lido como data é 1970, ou seja, "histórico" por acidente.
            // Mensagem sem hora conhecida é INGERIDA: tempo desconhecido não é tempo antigo, e o custo de
            // errar pra cada lado é assimétrico (ingerir a mais mostra um chat velho; ingerir a menos
            // engole um "SAIR" e a pessoa segue recebendo). Mesma doutrina do resto do arquivo.
            if (floorMs is { } since && m.Timestamp > 0 && m.Timestamp < since)
            {
                // Histórico: NÃO ingere, mas AVANÇA o marco por cima. Deixar o marco parado faria o
                // mesmo lote voltar pra sempre, e o poller nunca chegaria às mensagens novas.
                skippedAsHistory++;
                lastOk = m.RowId;
                continue;
            }

            try
            {
                await ingestion.IngestAsync(ToWebhookEvent(m, session), ct);
                ingested++;
                _poisonRowId = 0;
                _poisonAttempts = 0;
            }
            catch (OperationCanceledException)
            {
                // Shutdown: sai SEM salvar o marco (o `SaveChanges` do fim não roda). O que já foi
                // ingerido está commitado — cada mensagem grava por si dentro do IngestAsync — então o
                // próximo start relê estas linhas e a dedupe as descarta em silêncio. Perde-se um
                // punhado de leituras repetidas, não informação.
                throw;
            }
#pragma warning disable CA1031 // decidir entre transitório e permanente exige pegar tudo
            catch (Exception ex)
            {
                if (!GiveUpOnPoisonMessage(m.RowId, ex))
                {
                    // TRANSITÓRIO: para o lote aqui. O marco fica no último que deu certo e esta volta
                    // no próximo ciclo — a política de "não pular" continua valendo pra falha passageira.
                    break;
                }
                poisoned++;
            }
#pragma warning restore CA1031

            lastOk = m.RowId;
        }

        // `state` é a MESMA instância que a ingestão enxergou: o repositório usa FindAsync, que devolve a
        // entidade já rastreada no contexto do escopo em vez de reconsultar. Reler aqui seria uma chamada
        // a mais devolvendo o mesmo objeto.
        state.AdvanceInboundMarker(lastOk);
        await stateRepo.UpdateAsync(state, ct);
        await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(ct);

        if (skippedAsHistory > 0)
        {
            log.LogWarning(
                "Poller: {Skipped} mensagem(ns) anteriores ao início da escuta foram PULADAS (histórico do "
                + "aparelho, não conversa nova); marco em {RowId}.", skippedAsHistory, lastOk);
        }
        if (ingested > 0)
        {
            // Contador PRÓPRIO, não `messages.Count`: o lote pode ter parado no meio (falha transitória)
            // ou pulado linhas, e reportar o tamanho do lote como "ingeridas" inflaria o número justo no
            // log que alguém vai usar pra conferir se uma resposta entrou.
            log.LogInformation("Poller: {Count} mensagem(ns) do emulador ingerida(s); marco em {RowId}.",
                ingested, lastOk);
        }
        if (poisoned > 0)
        {
            log.LogError("Poller: {Poisoned} mensagem(ns) PULADAS por falha persistente neste ciclo.", poisoned);
        }
    }

    // Ciclos seguidos sem nada. Serve pra decidir QUANDO vale gastar uma consulta extra investigando se
    // o silêncio é normal (ninguém escreveu) ou patológico (marco órfão).
    private int _emptyCycles;

    // Se já posicionamos o marco no fim nesta instância. A auto-cura rearma isto: quando o banco do
    // aparelho é recriado, o marco volta a 0 e a escuta precisa se reposicionar no fim do banco NOVO —
    // senão o histórico que veio com o aparelho recriado seria ingerido como se fosse recente.
    private bool _seekedToEnd;

    /// <summary>Momento em que esta instância começou a escutar; piso de tempo para o caso de marco 0.</summary>
    /// <remarks>
    /// Fecha o furo que o "posicionar no fim" NÃO cobre: se na primeira olhada a caixa estava VAZIA, não
    /// há id pra marcar, o marco fica legitimamente em 0 e a leitura passa a ser "desde o começo". Se
    /// depois disso um HISTÓRICO aparecer — aparelho limpo e re-pareado com restauração de backup, ou
    /// imagem-ouro trocada — ele entra INTEIRO como se fosse recente, marcando gente como "respondeu"
    /// por mensagem de meses atrás e jogando essa gente na fila quente. É exatamente o dano que o
    /// posicionamento no fim existe pra evitar, chegando pelo caminho de trás.
    /// <para>Com marco 0, então, só ingere o que é mais NOVO que este piso; o resto é pulado (com o marco
    /// avançando por cima). A tolerância de <see cref="WatchSinceSkewGrace"/> existe porque o timestamp é
    /// do APARELHO e o piso é do servidor: sem folga, um relógio adiantado no guest descartaria mensagem
    /// legítima. Vazar até 5 min de histórico é incomparavelmente mais barato que vazar a caixa inteira.</para>
    /// </remarks>
    private DateTimeOffset? _watchSince;

    private static readonly TimeSpan WatchSinceSkewGrace = TimeSpan.FromMinutes(5);

    // Mensagem-VENENO: a mesma linha derrubando o ciclo repetidamente.
    private long _poisonRowId;
    private int _poisonAttempts;

    // 3 tentativas ≈ 1 min de insistência. Suficiente pra um blip (WAHA reiniciando, banco ocupado)
    // passar; curto o bastante pra não deixar a escuta parada muito tempo se a falha for permanente.
    private const int MaxIngestAttempts = 3;

    /// <summary>Decide se DESISTE desta mensagem (true) ou se para o lote pra tentar de novo (false).</summary>
    /// <remarks>
    /// A política deste arquivo é "nunca pular" — reprocessar é seguro, pular é que perde opt-out. Só que
    /// aplicada sem limite ela cria um modo de falha PIOR que o que evita: uma única mensagem que falha
    /// SEMPRE prende o marco, o mesmo lote volta a cada 20s e **nenhuma mensagem posterior é ingerida**.
    /// Uma resposta perdida vira TODAS as respostas perdidas, indefinidamente e em silêncio.
    /// <para>O caminho real que produz isso hoje: `@lid` que o `jid_map` do aparelho NÃO resolve (medido —
    /// 1 de 3 participantes) chega à ingestão como <c>…@lid</c>, que tenta resolver o lid pelo <b>WAHA</b>
    /// — e no modo Emulador o WAHA é justamente o que não está lá. Inalcançável, o HttpClient (Polly,
    /// breaker) LANÇA em vez de devolver null, e a exceção sobe pelo <c>IngestAsync</c>.</para>
    /// Então: insiste <see cref="MaxIngestAttempts"/> vezes (cobre o transitório) e, persistindo, desiste
    /// DESTA e segue — em LogError, porque desistir de uma mensagem recebida é perda real de informação e
    /// não pode passar como rotina.
    /// </remarks>
    private bool GiveUpOnPoisonMessage(long rowId, Exception ex)
    {
        if (rowId != _poisonRowId)
        {
            _poisonRowId = rowId;
            _poisonAttempts = 0;
        }
        if (++_poisonAttempts < MaxIngestAttempts)
        {
            log.LogWarning(ex,
                "Poller: falha ao ingerir a mensagem {RowId} (tentativa {Attempt}/{Max}); o lote para aqui "
                + "e ela volta no próximo ciclo.", rowId, _poisonAttempts, MaxIngestAttempts);
            return false;
        }
        log.LogError(ex,
            "Poller: DESISTINDO da mensagem {RowId} após {Max} tentativas — ela será PULADA pra não travar "
            + "a escuta. Se era um opt-out, ele NÃO foi aplicado; confira esta conversa à mão.",
            rowId, MaxIngestAttempts);
        _poisonRowId = 0;
        _poisonAttempts = 0;
        return true;
    }

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

        // Compara o marco com o ÚLTIMO id do aparelho: só um marco ACIMA do topo do banco é órfão.
        // Se o topo alcança o marco, o silêncio é legítimo; se o banco está vazio (0), o marco alto é
        // órfão — que é o caso que esta sonda existe pra resolver.
        //
        // ⚠️ NÃO comparar com a mensagem mais ANTIGA (o que esta sonda fazia até 2026-07-26): num
        // aparelho saudável a mais antiga está SEMPRE abaixo do marco, porque o marco vive no fim do
        // banco. O teste dava "órfão" em toda conta com histórico, a cada ~5 min de silêncio, pra
        // sempre — visto na prod do stack A: 8 avisos de órfão em 40 min, marco zerado e reposicionado
        // no fim em cada um. O efeito não era só log: entre o reset e o reposicionamento há um ciclo, e
        // mensagem que chegasse nessa janela era pulada em silêncio (opt-out perdido). Além disso o
        // aviso gritava lobo a cada 5 min e teria escondido um órfão de verdade.
        var newest = await phone.GetLastInboundRowIdAsync(ct);
        if (newest >= state.InboundLastRowId)
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
