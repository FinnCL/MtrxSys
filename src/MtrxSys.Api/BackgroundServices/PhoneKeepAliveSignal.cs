namespace MtrxSys.Api.BackgroundServices;

/// <summary>Sinal leve pro botão "Acordar / Keep-alive agora": acordar o primário leva minutos e não
/// pode bloquear o request HTTP. O endpoint só marca aqui; o <see cref="PhoneKeepAliveService"/>
/// consome no próximo tick e roda o ciclo. Singleton.</summary>
public sealed class PhoneKeepAliveSignal
{
    private volatile bool _requested;

    /// <summary>Pede um keep-alive imediato (não-bloqueante).</summary>
    public void RequestNow() => _requested = true;

    /// <summary>Consome o pedido pendente — retorna true uma única vez por pedido.</summary>
    public bool Consume()
    {
        var requested = _requested;
        _requested = false;
        return requested;
    }
}
