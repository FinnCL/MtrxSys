using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Api.Options;
using MtrxSys.Core.Application.Abstractions;
using Npgsql;

namespace MtrxSys.Api.Endpoints;

public static class WebhookEndpoints
{
    private const string UniqueViolationSqlState = "23505";

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/webhooks").AllowAnonymous();

        group.MapPost("/waha", async (
            WahaWebhookEvent payload,
            HttpContext http,
            IWebhookIngestionService ingestion,
            IOptions<WebhookOptions> opts,
            IHostEnvironment env,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("WahaWebhook");
            var configured = opts.Value.WahaToken;
            if (string.IsNullOrWhiteSpace(configured))
            {
                // FAIL-CLOSED fora de Development: sem token, o endpoint ficaria aberto — qualquer um
                // que adivinhe o formato forjaria um "SAIR" e daria opt-out global/irreversível de uma
                // vítima. Recusa em QUALQUER ambiente que não seja Development (Production, Staging ou
                // um nome custom) até configurar Webhooks:WahaToken (WAHA_HOOK_TOKEN). Só o dev/emulador
                // local segue aberto pra facilitar o loop.
                if (!env.IsDevelopment())
                {
                    logger.LogError(
                        "Webhook SEM token fora de Development (Webhooks:WahaToken vazio) — inbound rejeitado. "
                        + "Defina WAHA_HOOK_TOKEN pra o WAHA assinar os callbacks.");
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            }
            else
            {
                var received = http.Request.Headers["X-Webhook-Token"].ToString();
                if (!IsTokenValid(received, configured))
                {
                    logger.LogWarning("Webhook with missing/invalid token from {Ip}", http.Connection.RemoteIpAddress);
                    return Results.Unauthorized();
                }
            }

            try
            {
                await ingestion.IngestAsync(payload, ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                logger.LogDebug(ex, "Idempotent duplicate from concurrent webhook (waMessageId={WaId})", payload.Payload?.Id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to ingest WAHA webhook (event={Event}, session={Session})", payload.Event, payload.Session);
                // 5xx faz o WAHA reenfileirar (retries configurados em BuildWebhooks). Responder 200 aqui
                // descartava silenciosamente a mensagem — inclusive pedidos de SAIR — numa falha transitória
                // (DB/rede indisponível). O reprocesso é seguro: a ingestão é idempotente por waMessageId único.
                return Results.StatusCode(500);
            }
#pragma warning restore CA1031

            return Results.Ok();
        });

        return app;
    }

    private static bool IsTokenValid(string received, string configured)
    {
        var receivedBytes = Encoding.UTF8.GetBytes(received);
        var configuredBytes = Encoding.UTF8.GetBytes(configured);
        if (receivedBytes.Length != configuredBytes.Length)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(receivedBytes, configuredBytes);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg && pg.SqlState == UniqueViolationSqlState;
}
