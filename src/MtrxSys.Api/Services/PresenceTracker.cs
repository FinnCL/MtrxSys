namespace MtrxSys.Api.Services;

// Presença por CONEXÃO, não por heartbeat. O dashboard abre um stream SSE
// (/api/presence/connect) e o mantém aberto enquanto a aba existir. Quem segura a
// conexão viva é o NAVEGADOR, não um timer de JS — então minimizar, mandar pra segundo
// plano ou congelar a aba (Page Lifecycle/freeze) NÃO derruba a conexão: continua "em uso".
// Quando a aba fecha, navega ou o processo morre (crash/kill), o socket cai e o endpoint
// decrementa via RequestAborted → destrave automático, sem heartbeat e sem ação manual.
public sealed class PresenceTracker
{
    private int _connections;

    // Marca uma conexão ativa. Descarte o IDisposable (ou deixe o using) quando a conexão
    // cair pra liberar — idempotente, então chamar Dispose mais de uma vez é seguro.
    public IDisposable Connect()
    {
        Interlocked.Increment(ref _connections);
        return new Connection(this);
    }

    public PresenceStatus GetStatus()
    {
        var count = Volatile.Read(ref _connections);
        return new PresenceStatus(count > 0, count);
    }

    private void Drop() => Interlocked.Decrement(ref _connections);

    private sealed class Connection(PresenceTracker tracker) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                tracker.Drop();
            }
        }
    }
}

public record PresenceStatus(bool Active, int Connections);
