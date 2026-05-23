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
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("WahaWebhook");
            var configured = opts.Value.WahaToken;
            if (!string.IsNullOrWhiteSpace(configured))
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
