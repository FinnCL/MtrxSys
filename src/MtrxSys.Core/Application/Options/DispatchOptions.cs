namespace MtrxSys.Core.Application.Options;

public sealed class DispatchOptions
{
    public const string SectionName = "Dispatch";

    public string SessionId { get; set; } = "default";
    // Espaçamento entre envios (sorteado no intervalo). 90-240s é o default conservador anti-ban:
    // ritmo humano, longe do padrão-de-bot que dispara o anti-abuso do WhatsApp em conta nova.
    // Vale nos 10 stacks. Pode ser sobrescrito por stack via Dispatch__DelayMin/MaxSeconds no .env.
    public int DelayMinSeconds { get; set; } = 90;
    public int DelayMaxSeconds { get; set; } = 240;
    public int TypingMinSeconds { get; set; } = 2;
    public int TypingMaxSeconds { get; set; } = 5;
    public double TypingJitter { get; set; } = 0.15;

    // Rodapé de opt-out anexado SÓ na 1ª mensagem a cada contato (quando LastSentAt == null).
    // Dá uma saída explícita ("responda SAIR") em vez de a pessoa ir direto no denunciar/bloquear.
    // String vazia desliga o recurso.
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
}
