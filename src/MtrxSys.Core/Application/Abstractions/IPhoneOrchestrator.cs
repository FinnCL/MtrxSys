namespace MtrxSys.Core.Application.Abstractions;

/// <summary>Estado do aparelho visto pela aba "Celular".
/// State: "unavailable" (sem docker/adb, ou aparelho fora) · "not_created" (container não existe) ·
/// "exited"/"created"/"running" (estado do container) · "unauthorized" (SÓ no engine físico: o adb vê
/// o celular mas o diálogo RSA não foi aceito — conserto é na tela do aparelho, não no cabo, e por isso
/// é estado próprio e não "unavailable"). ViewUrl = noVNC (emulador) ou scrcpy (físico) quando rodando.</summary>
public sealed record PhoneStatus(string State, bool Running, string? ViewUrl);

/// <summary>Orquestra o "aparelho virtual" (Android em container, docker-android) a partir do app,
/// pra TUDO ficar dentro da aba "Celular" — provisionar, ligar, instalar o WhatsApp, aplicar proxy,
/// ver a tela e os logs — sem janela/prompt/script externo. Implementação: docker CLI sobre o socket
/// montado (deploy Linux com /dev/kvm). Fail-safe: erros viram PhoneStatus("unavailable", ...), então
/// em ambientes sem docker a aba degrada sem quebrar.</summary>
public interface IPhoneOrchestrator
{
    /// <summary>Estado atual do aparelho virtual.</summary>
    Task<PhoneStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>Android terminou de bootar? (adb getprop sys.boot_completed == 1). O container ficar
    /// "running" não basta — o Android ainda leva ~1-2 min pra subir. O botão "Provisionar número"
    /// espera isto antes de instalar o WhatsApp.</summary>
    Task<bool> IsBootedAsync(CancellationToken ct);

    /// <summary>Proxy in-guest do emulador de pé? (gost escutando na :12345 + regra REDIRECT no nat OUTPUT,
    /// DENTRO do Android — a MESMA pós-condição do watchdog). É o que garante que o egresso sai pelo
    /// residencial e não pelo IP do datacenter: o SINAL de que é seguro registrar o chip. false quando não
    /// dá pra confirmar (container ausente, adb mudo, sem root, proxy fora) — fail-safe: a UI segura o
    /// registro. Default: false (engines sem esse conceito nunca liberam).</summary>
    Task<bool> IsEgressProxyUpAsync(CancellationToken ct) => Task.FromResult(false);

    /// <summary>Provisiona o aparelho: cria o container (se ainda não existe) e o liga. Idempotente.</summary>
    Task<PhoneStatus> ProvisionAsync(CancellationToken ct);

    /// <summary>Reset FORTE ("aparelho novo"): remove o container E o volume de dados (agenda, conta
    /// Google, WhatsApp e a identidade do device) e provisiona do ZERO. Diferente de
    /// <see cref="ClearWhatsAppAsync"/> (pm clear), que só zera o WhatsApp e MANTÉM agenda/Google/device.
    /// Segunda linha: use quando um número novo morre rápido no MESMO aparelho (suspeita de correlação por
    /// device) — não é o passo padrão após um ban (esse é chip novo). Default: no-op (status atual).</summary>
    Task<PhoneStatus> ResetEmulatorAsync(CancellationToken ct) => GetStatusAsync(ct);

    /// <summary>O container do emulador é gerenciado por docker compose (tem label
    /// <c>com.docker.compose.project</c>)? Se SIM, o <see cref="ResetEmulatorAsync"/> por docker-run
    /// recriaria um container ERRADO — perde a config do compose (self-healing do X-lock, porta 6090,
    /// mount do emulator.py, sem-volume/commit-to-image — ver docker-compose.emulator-a.yml do A). Usado
    /// pra BLOQUEAR o "Resetar emulador" nesses casos (a recuperação certa é "Trocar chip" ou recriar pelo
    /// deploy/compose). Default false (engines sem esse conceito).</summary>
    Task<bool> IsComposeManagedAsync(CancellationToken ct) => Task.FromResult(false);

    /// <summary>Liga o aparelho já provisionado.</summary>
    Task<PhoneStatus> StartAsync(CancellationToken ct);

    /// <summary>Desliga o aparelho.</summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>Logs do container (boot do Android etc.), exibidos na aba.</summary>
    Task<string> GetLogsAsync(int tail, CancellationToken ct);

    /// <summary>Instala o WhatsApp no Android (sideload do APK via adb). Retorna a saída do comando.</summary>
    Task<string> InstallWhatsAppAsync(CancellationToken ct);

    /// <summary>Aplica (ou limpa, com hostPort vazio) o http_proxy global do Android — o mesmo IP do
    /// chip/WAHA. Retorna a saída do comando.</summary>
    Task<string> SetProxyAsync(string? hostPort, CancellationToken ct);

    /// <summary>Envia uma tecla de navegação do Android (back/home/recents) via adb keyevent — pros
    /// botões ◁ ○ □ da aba (o emulador em modo gestos não mostra a barra). Default: não suportado.</summary>
    Task<string> SendKeyAsync(string key, CancellationToken ct) =>
        Task.FromResult("navegação não suportada neste engine.");

    /// <summary>Digita um texto no campo focado do Android (adb input text) — pra colar de fora do
    /// emulador (ex.: código de pareamento do WAHA). Default: não suportado.</summary>
    Task<string> SendTextAsync(string text, CancellationToken ct) =>
        Task.FromResult("digitação não suportada neste engine.");

    /// <summary>Lê o número do WhatsApp registrado no emulador (registration_jid) — pra auto-preencher
    /// o Passo 2 e evitar digitar o número errado. Vazio se não achar. Default: não suportado.</summary>
    Task<string> GetWhatsAppNumberAsync(CancellationToken ct) => Task.FromResult("");

    /// <summary>Estado da CONTA do WhatsApp dentro do emulador — distingue três situações que a tela
    /// mostrava iguais (e cuja confusão custou um chip em 2026-07-25): conta viva, conta DERRUBADA pelo
    /// servidor, e aparelho que nunca registrou. É o que decide qual botão de recuperação faz sentido:
    /// "Trocar chip" (pm clear, mantém o device) resolve troca POR ESCOLHA, mas é insuficiente depois de
    /// uma restrição — ali o `android_id`/GSF do device queimado sobrevivem ao pm clear e o chip novo
    /// herda a ficha. Ver <see cref="RequestCleanDeviceAsync"/>. Default: "unknown".</summary>
    Task<WhatsAppAccountState> GetWhatsAppAccountStateAsync(CancellationToken ct) =>
        Task.FromResult(WhatsAppAccountState.Unknown);

    /// <summary>Grupos do WhatsApp lidos DIRETO do aparelho, sem WAHA. O emulador é o dono da conta —
    /// ele já tem os grupos no próprio banco, então listar por aqui elimina a dependência de ter um
    /// aparelho conectado (companion) vinculado ao chip só pra conseguir importar. Vazio quando não dá
    /// pra ler (adb mudo, sem root, app sem dados) — nunca lança. Default: não suportado.</summary>
    Task<IReadOnlyList<PhoneGroup>> ListGroupsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PhoneGroup>>([]);

    /// <summary>Participantes de um grupo, lidos do aparelho, com o telefone REAL já resolvido.
    /// <para>⚠️ O participante costuma vir como <c>@lid</c> (identificador opaco), não como telefone. A
    /// implementação resolve pelo mapa que o próprio app mantém — sem isso, importar devolveria "números"
    /// que não existem, que é o padrão de lista suja associado a ban neste projeto. Ver [[br-phone-traps]].</para>
    /// Vazio quando o jid é inválido ou não dá pra ler. Default: não suportado.</summary>
    Task<IReadOnlyList<PhoneGroupMember>> ListGroupParticipantsAsync(string groupJid, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PhoneGroupMember>>([]);

    /// <summary>Mensagens RECEBIDAS (from_me=0) lidas do banco do aparelho, para consumo incremental.
    /// Substitui o webhook do WAHA como fonte do "ouvir" (Chat, opt-out, marcação de quem respondeu).
    /// <para><paramref name="afterRowId"/> é o `message._id` do último já processado — o chamador guarda
    /// esse marco e pede só o que veio depois. É crescente e estável, ao contrário de timestamp (que
    /// empata) e de key_id (que é opaco).</para></summary>
    Task<IReadOnlyList<PhoneInboundMessage>> ReadInboundMessagesAsync(long afterRowId, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PhoneInboundMessage>>([]);

    /// <summary>Maior `message._id` de entrada que já existe no aparelho (0 se não há nenhum).
    /// <para>Serve pra POSICIONAR o marco na primeira vez, sem ingerir. Um leitor de fila que começa do
    /// zero num banco com histórico reprocessaria conversas antigas como se fossem novas — criando chats
    /// velhos e, pior, marcando gente como "respondeu" por causa de mensagem de meses atrás, o que a
    /// jogaria na fila quente. Disparar pra quem não fala com você há meses é perfil de queima de chip.</para>
    /// Default: 0 (engines sem esse conceito começam do início, que é o comportamento anterior).</summary>
    Task<long> GetLastInboundRowIdAsync(CancellationToken ct) => Task.FromResult(0L);

    /// <summary>A imagem-ouro existe e está pronta pra ser usada? Sem ela o watchdog DESCARTA o pedido de
    /// limpeza (com aviso no log dele), e do lado do usuário a operação vira falha muda: a tela confirma,
    /// a fila fica pausada e o aparelho não muda. Por isso é checado ANTES de pausar qualquer coisa, e
    /// governa se o botão sequer aparece. Default: false (engines sem esse conceito nunca oferecem).</summary>
    Task<bool> IsGoldenImageReadyAsync(CancellationToken ct) => Task.FromResult(false);

    /// <summary>Pede um APARELHO NOVO a partir da imagem-ouro (molde que nunca registrou número).
    /// NÃO recria nada aqui de propósito: grava um flag-volume (`&lt;container&gt;-clean-request`) e quem
    /// executa é o emulator-watchdog, no HOST. Motivo: recriar por `docker run` a partir do app produz um
    /// container ERRADO (porta 6080 em vez de 6090, rede bridge que não alcança o gost, sem o
    /// self-healing do X-lock, sem o mount do emulator.py) — foi o que aposentou o botão "Resetar
    /// emulador". O watchdog tem o compose e recria certo. Mesmo padrão do flag `-off` do StopAsync.
    /// Default: não suportado.</summary>
    Task<string> RequestCleanDeviceAsync(CancellationToken ct) =>
        Task.FromResult("limpeza por imagem-ouro não suportada neste engine.");

    /// <summary>Abre uma URL no WhatsApp do emulador via intent VIEW (adb am start) — o deep link de
    /// vínculo por QR (a URL do QR do WAHA), que abre "Deseja conectar um dispositivo?" SEM câmera nem
    /// rate limit; o usuário toca "Continuar". Default: não suportado.</summary>
    Task<string> OpenUrlAsync(string url, CancellationToken ct) =>
        Task.FromResult("abertura de URL não suportada neste engine.");

    /// <summary>"Trocar chip": zera o WhatsApp do emulador (pm clear) pra registrar OUTRO número —
    /// volta pra tela de boas-vindas. A conta velha sai do app (não do servidor). Default: não
    /// suportado.</summary>
    Task<string> ClearWhatsAppAsync(CancellationToken ct) =>
        Task.FromResult("troca de chip não suportada neste engine.");

    /// <summary>Grava um número na AGENDA do Android do emulador (contacts provider) — pra o disparo
    /// sair pra um "contato salvo" (perfil menos-robô, ajuda anti-ban). Chamado pelo DispatchEngine
    /// antes de cada envio: IDEMPOTENTE (não duplica) e best-effort. Default: não suportado.</summary>
    /// <summary>O número está no WhatsApp, SEGUNDO O PRÓPRIO APARELHO? Equivalente LOCAL do
    /// check-exists do WAHA, sem API nenhuma: quando um contato da agenda é usuário da plataforma, o
    /// WhatsApp cria por conta própria um raw contact na conta <c>com.whatsapp</c> (com as linhas
    /// <c>vnd.com.whatsapp.profile</c>/<c>.voip.call</c>), agregado ao contato original. A ausência
    /// desse espelho, num contato JÁ sincronizado, é o sinal de que o número não tem WhatsApp.
    /// Medido em produção (2026-07-23): 115 contatos salvos → 113 espelhados → 2 não-usuários.
    /// PRÉ-REQUISITO: o contato precisa estar salvo E ter dado tempo de sincronizar (o espelho leva
    /// de ~2,5 a ~7 min pra aparecer). Por isso <c>null</c> ("não sei") quando ele nem está na agenda
    /// — quem chama deve salvar, esperar o grace e perguntar de novo, nunca tratar null como "não".
    /// </summary>
    /// <returns>true = é usuário; false = está na agenda há tempo e NÃO é usuário; null = não deu pra saber.</returns>
    Task<bool?> IsOnWhatsAppAsync(string phoneE164, CancellationToken ct) =>
        Task.FromResult<bool?>(null);

    Task<string> SaveContactAsync(string phoneE164, string? name, CancellationToken ct) =>
        Task.FromResult("gravação de contato não suportada neste engine.");

    /// <summary>Envia uma mensagem de WhatsApp DIRETO pela UI do emulador (o PRIMÁRIO), NÃO pelo WAHA.
    /// É o "Caminho A" anti-463: o companion NOWEB dá 463 em frio; o primário (dono da conta) manda
    /// normal. Abre o chat com a mensagem já preenchida via intent click-to-chat
    /// (whatsapp://send?phone=X&amp;text=Y — funciona pra número salvo OU não), acha o botão "enviar" por
    /// resource-id (uiautomator, robusto) e toca. Retorna resultado ESTRUTURADO (enviou? entrega? erro?).
    /// Default: não suportado.</summary>
    Task<WhatsAppSendResult> SendWhatsAppMessageAsync(string phoneE164, string text, CancellationToken ct) =>
        Task.FromResult(WhatsAppSendResult.Fail("envio pela UI não suportado neste engine."));

    /// <summary>O aparelho consegue digitar ESTE texto? null = sim (ou o engine não opina); string =
    /// motivo pra mostrar ao operador ANTES de começar o lote.</summary>
    /// <remarks>
    /// 🔴 Existe por um modo de falha medido em 2026-07-29: com <c>HumanTyping</c> ligado e sem o IME
    /// Unicode instalado, o `input text` do Android NÃO digita acento nem emoji (devolve
    /// NullPointerException). "oi" passa; o template real, que começa com 🔥 e tem acento em toda
    /// linha, falha em TODOS os envios. O erro só aparecia na primeira mensagem do lote, depois de
    /// abrir conversa e tocar na tela, e se repetia contato a contato.
    /// <para>Checar antes transforma 200 falhas idênticas num aviso na largada. Default null: engines
    /// que não digitam (ou não sabem responder) não bloqueiam ninguém.</para>
    /// </remarks>
    Task<string?> CheckTypingCapabilityAsync(string sampleText, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}

/// <summary>Grupo lido do banco do WhatsApp do emulador.</summary>
/// <param name="Jid">Identificador canônico (`&lt;id&gt;@g.us`).</param>
/// <param name="Subject">Nome do grupo; null se o aparelho ainda não sincronizou o assunto.</param>
/// <param name="CreatedTimestamp">Criação, em ms desde a época (0 quando desconhecido).</param>
/// <param name="ParticipantsCount">Quantos membros o aparelho conhece. É a contagem BRUTA do grupo — o
/// import costuma trazer menos, porque descarta quem não tem telefone resolvível (ver
/// <see cref="PhoneGroupMember"/>). Divergência entre este número e o importado é esperada, não bug.</param>
public sealed record PhoneGroup(string Jid, string? Subject, long CreatedTimestamp, int ParticipantsCount);

/// <summary>Participante de grupo lido do aparelho, com o telefone já resolvido a partir do @lid.</summary>
/// <param name="Phone">Dígitos do número (sem "+"), como o app o conhece.</param>
/// <param name="IsAdmin">Administrador do grupo (`rank` &gt; 0 no banco do app).</param>
public sealed record PhoneGroupMember(string Phone, bool IsAdmin);

/// <summary>Mensagem recebida, lida do banco do WhatsApp do emulador.</summary>
/// <param name="RowId">`message._id` — o marco incremental que o consumidor guarda pra pedir "o que veio
/// depois". Crescente e estável.</param>
/// <param name="ChatJid">Conversa de origem (`&lt;numero&gt;@s.whatsapp.net` ou `&lt;id&gt;@g.us`).</param>
/// <param name="SenderJid">QUEM escreveu. Em conversa 1:1 é igual ao <paramref name="ChatJid"/>; em GRUPO
/// é o participante, e é ele que importa — atribuir um "SAIR" ao jid do grupo suprimiria o grupo inteiro
/// em vez da pessoa. Por isso os dois campos existem separados desde o começo.</param>
/// <param name="Timestamp">Quando chegou, em ms desde a época.</param>
/// <param name="Text">Texto; null em mídia/sistema (o consumidor decide se ignora).</param>
/// <param name="MessageType">`message_type` do banco do app. MEDIDOS com mensagem real em 2026-07-25:
/// <c>0</c> = texto, <c>1</c> = imagem; <c>7</c> apareceu numa mensagem de SISTEMA. Os demais seguem a
/// convenção do app e NÃO foram observados aqui — por isso quem consome deve tratar desconhecido como
/// "ignorar", nunca como "provavelmente mídia".</param>
public sealed record PhoneInboundMessage(
    long RowId, string ChatJid, string SenderJid, long Timestamp, string? Text, int MessageType);

/// <summary>Estado da conta do WhatsApp DENTRO do emulador, com o motivo quando não há conta.</summary>
/// <param name="State">
/// "registered" = conta viva (registration_jid preenchido) ·
/// "revoked" = registration_jid VAZIO mas o app guarda marcas de logout do primário
/// (`previously_logged_out_from_primary` / `pref_phone_number_of_logged_out_user`) → o SERVIDOR derrubou,
/// ninguém limpou o app aqui · "none" = nunca registrou (aparelho novo ou pm clear recente) ·
/// "unknown" = não deu pra saber (adb mudo, container fora) — a UI não deve alarmar nesse caso.
/// </param>
/// <param name="Phone">Número em dígitos: o registrado (registered) ou o que foi derrubado (revoked).</param>
public sealed record WhatsAppAccountState(string State, string? Phone)
{
    public static readonly WhatsAppAccountState Unknown = new("unknown", null);

    /// <summary>Marca que o engine imprime ANTES do dump pra provar que o adb respondeu. Sem ela, um adb
    /// mudo (container fora, boot em andamento, adb ocupado no disparo) daria saída vazia — indistinguível
    /// de "nunca registrou" — e a UI acusaria problema onde não há.</summary>
    public const string AdbSentinel = "__ADB_OK__";

    /// <summary>Foi o servidor que tirou a conta do ar — caso em que "Trocar chip" (pm clear) NÃO basta,
    /// porque a identidade do device (android_id/GSF) sobrevive a ele.</summary>
    public bool RevokedByServer => State == "revoked";

    // Só o que interessa das shared_prefs. Compilados uma vez (static readonly) em vez dos helpers
    // estáticos de Regex: isto roda a cada poll da aba Celular e a cada ciclo do dispatcher.
    private static readonly System.Text.RegularExpressions.Regex JidRx =
        new(@"registration_jid[^0-9]*([0-9]{12,13})", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex BurnedRx =
        new(@"saved_user_before_logout[^0-9]*([0-9]{12,13})", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex LoggedOutFromPrimaryRx =
        new(@"previously_logged_out_from_primary""\s+value=""true""", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Interpreta o dump das shared_prefs do WhatsApp. FUNÇÃO PURA de propósito: a decisão de
    /// qual botão de recuperação a UI oferece mora aqui, e testá-la não pode depender de Docker/adb.
    /// <para>Ordem importa: <c>registration_jid</c> vence (conta viva); só na ausência dele as marcas de
    /// logout distinguem "o SERVIDOR derrubou" de "app zerado". Um <c>pm clear</c> apaga o diretório
    /// inteiro, então nenhuma marca sobreviveria a ele — é isso que torna a distinção confiável.</para>
    /// </summary>
    public static WhatsAppAccountState Parse(string? prefsDump)
    {
        var text = prefsDump ?? "";
        if (!text.Contains(AdbSentinel, StringComparison.Ordinal))
        {
            return Unknown;
        }

        var jid = JidRx.Match(text);
        if (jid.Success)
        {
            return new WhatsAppAccountState("registered", jid.Groups[1].Value);
        }

        var burned = BurnedRx.Match(text);
        return burned.Success || LoggedOutFromPrimaryRx.IsMatch(text)
            ? new WhatsAppAccountState("revoked", burned.Success ? burned.Groups[1].Value : null)
            : new WhatsAppAccountState("none", null);
    }
}

/// <summary>Resultado do envio pela UI do WhatsApp (Caminho A). Contrato claro em vez de uma string
/// que misturava status/ok/erro (que invertia o "ok" no sucesso).</summary>
/// <param name="Sent">A mensagem SAIU (botão tocado + campo esvaziou). false = não enviou.</param>
/// <param name="DeliveryStatus">Entrega NORMALIZADA (locale-independente): "sent" | "delivered" |
/// "read" | null (não lido ainda). Mata o "ack cego" do WAHA.</param>
/// <param name="Error">Motivo da falha quando <paramref name="Sent"/> = false; null no sucesso.</param>
public sealed record WhatsAppSendResult(bool Sent, string? DeliveryStatus, string? Error)
{
    /// <summary>A falha é CONCLUSIVA ("nada saiu") ou apenas NÃO CONFIRMADA ("pode ter saído")?</summary>
    /// <remarks>
    /// 🔴 Existe porque <see cref="Sent"/> é bool e engolia três estados em dois. "o WhatsApp respondeu
    /// que este número não tem conta" e "toquei enviar mas não consegui confirmar" chegavam idênticos a
    /// quem chama, e a diferença entre eles decide se é seguro TENTAR DE NOVO.
    ///
    /// <para>O console reage a uma falha reabrindo a conversa na outra forma do número. Fazer isso num
    /// envio que talvez tenha saído entrega a campanha DUAS VEZES para a mesma pessoa, que é o pior
    /// resultado possível com contato frio. Por isso a retentativa só pode rodar quando a falha é
    /// conclusiva.</para>
    ///
    /// <para>Mesma doutrina do <c>IsOnWhatsAppAsync</c>, que nunca devolve false: quando a consequência
    /// de errar é cara, "não sei" precisa caber no tipo, senão vira uma afirmação inventada.</para>
    /// </remarks>
    public bool Uncertain { get; init; }

    /// <summary>A falha é DESTE NÚMERO (o WhatsApp disse que não há conta) ou do APARELHO?</summary>
    /// <remarks>
    /// 🔴 Mesma razão do <see cref="Uncertain"/>, aplicada a outro par de estados que estava sendo
    /// engolido: "este número não tem conta" e "o botão enviar não apareceu" chegavam a quem chama
    /// como a mesma coisa, uma falha.
    ///
    /// <para>Só que a diferença decide se vale CONTINUAR o lote. Falha do aparelho (tela bloqueada,
    /// WhatsApp fechado, cabo solto) se repete no próximo contato e no seguinte, então parar rápido
    /// economiza a lista inteira. Falha do número não diz nada sobre o próximo contato: o disjuntor
    /// derrubava o lote por três números ruins seguidos com o aparelho perfeito.</para>
    ///
    /// <para>O driver já sabia a diferença desde que passou a ler o diálogo do app; o que faltava era
    /// carregá-la no tipo. Quem consome não deve ter que reconhecer a causa por substring da mensagem
    /// de erro, que é texto para humano e muda quando alguém melhora a redação.</para>
    /// </remarks>
    public bool NoWhatsAppAccount { get; init; }

    /// <summary>A conversa foi aberta pelo REGISTRO do contato na agenda, e não pelo número.</summary>
    /// <remarks>
    /// Existe pra medir, não pra decidir. Abrir pelo registro não passa pela resolução de número, que é
    /// onde o app nega gente que tem WhatsApp — mas se esse caminho de fato funciona neste aparelho e
    /// nesta versão do app é hipótese até rodar. Carregar o dado no resultado faz o PRIMEIRO lote real
    /// responder isso, sem teste separado: basta cruzar, no CSV, quem abriu por registro com quem
    /// entregou.
    /// </remarks>
    public bool AbertoPeloRegistro { get; init; }

    /// <summary>O PRÓPRIO APP declarou na tela que a conta está restringida.</summary>
    /// <remarks>
    /// 🔴 MEDIDO em 2026-08-10, aparelho RQ8M908C0ZX, no meio de um lote: depois de 30 entregas normais,
    /// a tela da conversa passou a trazer "Sua conta foi restringida. Você não pode…", e mais adiante o
    /// app foi inteiro para a tela de recurso, com o botão "PEDIR ANÁLISE".
    ///
    /// <para>Isto encerra uma investigação inteira que partiu da premissa ERRADA de que o aparelho não
    /// mostra restrição — premissa que veio de olhar o app à mão e não achar aviso. Ele mostra, dentro
    /// da conversa, e a captura automática de tela é que trouxe a prova.</para>
    ///
    /// <para>É a única condição de CERTEZA que o driver consegue emitir sobre a conta: não é inferência
    /// por sequência de falhas, é o app declarando. Por isso, diferente de tudo mais, ela autoriza PARAR
    /// o lote: com a conta restrita nenhuma mensagem sai, e insistir é o que vira banimento.</para>
    /// </remarks>
    public bool ContaRestringida { get; init; }

    /// <summary>O app declarou restrição na tela. Falha do CHIP, não do número nem do aparelho.</summary>
    public static WhatsAppSendResult Restringida(string error) =>
        new(false, null, error) { ContaRestringida = true };

    public static WhatsAppSendResult Ok(string? deliveryStatus) => new(true, deliveryStatus, null);

    /// <summary>Falha CONCLUSIVA: nada saiu, e tentar de novo é seguro.</summary>
    public static WhatsAppSendResult Fail(string error) => new(false, null, error);

    /// <summary>Falha CONCLUSIVA e do NÚMERO: o app afirmou que não existe conta para ele.</summary>
    public static WhatsAppSendResult NoAccount(string error) =>
        new(false, null, error) { NoWhatsAppAccount = true };

    /// <summary>NÃO DEU PRA CONFIRMAR: a mensagem pode ter saído. Não tentar de novo sozinho.</summary>
    public static WhatsAppSendResult Unconfirmed(string error) => new(false, null, error) { Uncertain = true };
}
