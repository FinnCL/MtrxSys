namespace MtrxSys.Core.Application.Options;

public sealed class DispatchOptions
{
    public const string SectionName = "Dispatch";

    public string SessionId { get; set; } = "default";
    // Espaçamento entre envios (sorteado no intervalo). Ritmo humano, longe do padrão-de-bot que dispara
    // o anti-abuso do WhatsApp em conta nova. Sobrescrevível por stack via Dispatch__DelayMin/MaxSeconds.
    //
    // ⚠️ 150-360s É O VALOR ENDURECIDO (18133e5, junto com a rampa 400→200), e o default TEM que ser ele.
    // Até 2026-07-26 o default aqui era 90-240 enquanto o appsettings do Dispatcher mandava 150-360 — a
    // config vencia, então a produção rodava certo, mas a divergência era um alçapão: sumindo a seção
    // `Dispatch` do appsettings, o intervalo caía 40% NUM PARÂMETRO ANTI-BAN, em silêncio e sem erro.
    // Default tem que falhar pro lado SEGURO (mais lento), nunca pro rápido.
    //
    // Medido em 59 envios reais: até 23/07 aparecem delays de 93, 102, 103, 121s (config antiga);
    // de 24/07 em diante, 169s e 221s — dentro da faixa nova. Confirma que a config está valendo.
    public int DelayMinSeconds { get; set; } = 150;
    public int DelayMaxSeconds { get; set; } = 360;
    public int TypingMinSeconds { get; set; } = 2;
    public int TypingMaxSeconds { get; set; } = 5;
    public double TypingJitter { get; set; } = 0.15;

    // Texto de opt-out (fallback) anexado à 1ª mensagem SÓ quando o link de "sair" está DESLIGADO
    // (OptOut:PublicBaseUrl vazio — ex.: localhost). Com o link LIGADO (prod, o caso normal) a cópia é
    // FIXA no MessageComposer (LinkOnlyOptOut, por ser sensível a ban) e este valor NÃO é usado.
    // Ele pede pra RESPONDER "SAIR", o que só chega até nós com o companion WAHA vinculado (inbound) —
    // por isso é fallback de ambiente sem link, e não uma alternativa a ele. Dá uma saída explícita
    // em vez de a pessoa ir direto no denunciar/bloquear. String vazia desliga o texto.
    public string OptOutFooter { get; set; } = "Se não quiser mais receber mensagens, responda SAIR.";

    // Para o ciclo de disparo se a sessão WAHA do chip não estiver "Working" (caiu/deslogou),
    // antes de queimar tentativas e abrir o circuit breaker por falhas.
    public bool PauseWhenSessionDown { get; set; } = true;

    // "Reassentar após reconectar": quando o chip volta a WORKING (ou após um restart do dispatcher), o
    // disparo espera esta janela antes de voltar a enviar — evita reconectar-e-metralhar (anti-ban).
    // 0 desliga. Só tem efeito com PauseWhenSessionDown = true (é onde o status é checado).
    public int SettleAfterReconnectSeconds { get; set; } = 120;

    // Quantas vezes, no total, um disparo é tentado antes de virar falha definitiva.
    // 2 = a tentativa original + 1 reenvio automático (o contato volta pro fim da fila).
    // Falha transitória abaixo desse teto reenvia sem contar pro circuit breaker; ao atingir
    // o teto, vira Failed e aí sim conta (chip genuinamente quebrado acaba pausando).
    public int MaxSendAttempts { get; set; } = 2;

    // Teto de ADIAMENTOS de um mesmo disparo antes de virar Skipped. Adiar não consome tentativa (é
    // de propósito: adiar não é falhar), mas sem teto o job fica na fila para sempre — e cada volta
    // gasta um intervalo de envio, o mesmo que uma mensagem real gastaria.
    //
    // 180 ≈ 24h no ritmo de re-checagem de 8 min (EmulatorSyncGraceSeconds). A referência é o sync de
    // contatos do WhatsApp, que roda DE HORA EM HORA: 24h dá 24 ciclos dele. É outra ordem de grandeza
    // da carência de 20 min que, em 2026-07-27, descartou 10 contatos bons por julgá-los antes de o
    // app ter olhado pra eles uma única vez. 0 = sem teto (adia para sempre — o comportamento antigo).
    public int MaxDeferrals { get; set; } = 180;

    // GUARD DE SAÚDE-DE-ENTREGA (anti-shadow-restriction): o 463/shadow-ban não vem como erro do
    // envio — o WhatsApp aceita (ack 1) mas NÃO entrega (ack nunca chega a 2). O breaker não pega isso.
    // Este guard PARA o ciclo quando, numa janela recente com amostra mínima, a taxa entregue/enviado
    // cai abaixo do limiar — o freio automático que faltava pra não queimar o chip num número morto.
    // Auto-corrige: acks atrasados (destinatário offline) que chegam depois recuperam a taxa e o ciclo
    // volta sozinho; se a entrega não recupera, segue parado (é o que queremos). false = desliga.
    public bool DeliveryHealthGuardEnabled { get; set; } = true;
    public int DeliveryHealthWindowHours { get; set; } = 24;
    // Amostra mínima de envios na janela pra avaliar (evita pausar por 1-2 offline no começo).
    public int DeliveryHealthMinSample { get; set; } = 20;
    // Abaixo desta taxa entregue/enviado, pausa. 0.5 = metade não entregou = claramente anormal.
    public double DeliveryHealthMinRate { get; set; } = 0.5;

    // Na importação de participantes de grupo, só cadastra números VÁLIDOS para o Brasil (+55,
    // DDD/9º dígito corretos via libphonenumber). Números estrangeiros são ignorados. É a garantia
    // real de "contatos para brasileiros" — independe de o grupo parecer ou não brasileiro.
    public bool OnlyBrazilianContacts { get; set; } = true;

    // GATE POR CHIP (anti-463): só dispara pros contatos que o chip CONECTADO agora importou (co-membros
    // dele). Contato de outro chip — ou legado sem marca — é frio pra este chip → daria 463, então é
    // PULADO (não envia). É a regra "a co-membria é por chip": trocar de chip não pode disparar frio a
    // lista de outro. Re-importar o grupo com o chip atual "move" os contatos pra ele. false = desliga
    // (volta a disparar pra qualquer contato do público, arriscando 463 em quem for frio pro chip).
    public bool OnlyCurrentChipContacts { get; set; } = true;

    // FASE DE AQUECIMENTO POR RESPONDEDORES: nos primeiros N dias ATIVOS de um chip (dias COM envio, não
    // de calendário — chip parado não amadurece), o disparo SÓ aceita o público "Respondeu" (EngagedOnly).
    // Quem já te escreveu neste chip é seguro (sem 463); mandar pra frio recém-pareado é o gatilho nº1 de
    // ban. Depois de N dias ativos, abre pra todas as audiências. Conta a partir do marco do chip
    // (WarmupStartedOn), então re-parear reinicia a fase. 0 desliga a trava. Ver WarmingDailyResetService,
    // que à meia-noite de Brasília libera os respondedores pra novo disparo enquanto a fase durar.
    //
    // DESLIGADO (0, o default do int — deixado SEM inicializador explícito por CA1805) por decisão
    // operacional 2026-07-23: o chip dispara pra FRIO "Na fila" desde o dia 0, sem a janela de
    // só-respondedores. O gatilho de 463 que a trava evitava é endereçado agora por OUTRO caminho —
    // envio pela UI do app oficial + contato salvo/sincronizado na agenda + IP residencial (ver
    // DockerCliPhoneOrchestrator). A trava era defesa do caminho WAHA, que não é mais o de envio. Pra
    // religar num stack específico, sete Dispatch:WarmingResponderOnlyDays > 0 no .env dele.
    public int WarmingResponderOnlyDays { get; set; }

    // FASE HÍBRIDA: após os N dias de "só respondeu", até o platô da curva (200/dia), o disparo mistura
    // o Círculo de Aquecimento (contatos SEUS, re-enviáveis) com frios novos, intercalado. true = ligado
    // (default). false = após os N dias abre direto pra todas as audiências (comportamento anterior).
    public bool HybridWarmingEnabled { get; set; } = true;

    // NOTA (Caminho A anti-463): NÃO há flag de "enviar pelo emulador" aqui. O disparo pela UI do
    // emulador é comandado pelo TOGGLE "Com emulador" (PhoneDispatchMode.Emulator, persistido no banco
    // e visível na aba Celular), não por config de deploy — fonte única. Ver DispatchEngine.emulatorMode.

    // Caminho do flag de saúde do EGRESSO do emulador (escrito pelo watchdog do host: "ok"/"leak").
    // VAZIO (default) = gate DESLIGADO, nenhum bloqueio. Preenchido = FAIL-CLOSED no modo emulador: o
    // ciclo só envia se o flag disser "ok" (proxy residencial de pé); "leak" ou flag ausente/ilegível
    // PARA o ciclo, pra a mensagem não sair pelo IP do datacenter (gatilho de ban). O flag precisa
    // estar montado no container do dispatcher (ver docker-compose). Ligar só depois de confirmar o
    // mount + o watchdog escrevendo, senão o disparo bloqueia (falha SEGURA, mas para de enviar).
    public string EmulatorEgressHealthPath { get; set; } = string.Empty;
}
