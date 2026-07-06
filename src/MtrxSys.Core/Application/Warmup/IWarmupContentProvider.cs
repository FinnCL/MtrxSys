namespace MtrxSys.Core.Application.Warmup;

/// <summary>Um turno de uma conversa de aquecimento. <paramref name="SenderSlot"/> alterna 0/1 entre
/// os dois membros do par (0 = quem abriu; 1 = quem responde).</summary>
public sealed record WarmupTurn(int SenderSlot, string Text);

/// <summary>Gera o CONTEÚDO das conversas de aquecimento. Pluggável: o MVP usa banco de frases
/// variadas (grátis); um <c>AiWarmupContentProvider</c> (LLM) pode substituir depois sem tocar no
/// motor. Conteúdo robótico/repetido é PIOR que nada — a variação é o ponto.</summary>
public interface IWarmupContentProvider
{
    /// <summary>Monta uma conversa curta (2-4 turnos), mão dupla, casual e variada.</summary>
    IReadOnlyList<WarmupTurn> BuildConversation();
}
