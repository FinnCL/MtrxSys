namespace MtrxSys.Core.Application.Options;

public sealed class DispatchOptions
{
    public const string SectionName = "Dispatch";

    public string SessionId { get; set; } = "default";
    public int DelayMinSeconds { get; set; } = 45;
    public int DelayMaxSeconds { get; set; } = 75;
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

    // Quantas vezes, no total, um disparo é tentado antes de virar falha definitiva.
    // 2 = a tentativa original + 1 reenvio automático (o contato volta pro fim da fila).
    // Falha transitória abaixo desse teto reenvia sem contar pro circuit breaker; ao atingir
    // o teto, vira Failed e aí sim conta (chip genuinamente quebrado acaba pausando).
    public int MaxSendAttempts { get; set; } = 2;
}
