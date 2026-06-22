using System.Data.Common;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MtrxSys.Api.Endpoints;
using MtrxSys.Api.Startup;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure;
using MtrxSys.Infrastructure.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<MtrxSys.Core.Application.Abstractions.ICurrentUserAccessor, MtrxSys.Api.Startup.HttpCurrentUserAccessor>();
builder.Services.AddSingleton<MtrxSys.Api.Services.PresenceTracker>();
builder.Services.AddMemoryCache(); // cache curto do status do chip (/api/presence/chip)
builder.Services.AddOptions<MtrxSys.Api.Options.WebhookOptions>()
    .Bind(builder.Configuration.GetSection(MtrxSys.Api.Options.WebhookOptions.SectionName));
builder.Services.AddOptions<MtrxSys.Core.Application.Options.SyncOptions>()
    .Bind(builder.Configuration.GetSection(MtrxSys.Core.Application.Options.SyncOptions.SectionName));
builder.Services.AddOptions<MtrxSys.Api.Options.PresenceOptions>()
    .Bind(builder.Configuration.GetSection(MtrxSys.Api.Options.PresenceOptions.SectionName));
builder.Services.AddHostedService<MtrxSys.Api.BackgroundServices.WhatsAppAutoSyncService>();
builder.Services.AddHostedService<MtrxSys.Api.BackgroundServices.CollectorWorker>();
// Origens permitidas: por padrão libera as portas web usadas no setup localhost — a landing
// de múltiplos ambientes (5175) e os webs dos Stacks 1..10 (5173, 5174, 5176..5183; a 5175 é
// da landing). Pode ser sobrescrito por env var Web__Origins__0, Web__Origins__1, etc.
var corsOrigins = builder.Configuration.GetSection("Web:Origins").Get<string[]>()
    ?? new[]
    {
        "http://localhost:5173", "http://localhost:5174", "http://localhost:5175", "http://localhost:5176",
        "http://localhost:5177", "http://localhost:5178", "http://localhost:5179", "http://localhost:5180",
        "http://localhost:5181", "http://localhost:5182", "http://localhost:5183",
    };
builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
    p.AllowAnyHeader().AllowAnyMethod().WithOrigins(corsOrigins)));
// Exceção não tratada → ProblemDetails (JSON) em vez de 500 cru. Importa pro CORS: numa resposta de
// erro sem o middleware de exceção, o header Access-Control-Allow-Origin some e o browser reporta um
// falso "erro de CORS" mascarando o crash real. Com o handler (rodando DEPOIS do UseCors) a resposta
// volta pelo CORS e mantém o header.
builder.Services.AddProblemDetails();

var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
var jwtOptions = jwtSection.Get<JwtOptions>() ?? new JwtOptions();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApiDocument(cfg =>
{
    cfg.Title = "MtrxSys API";
    cfg.Version = "v1";
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var migrationLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Migrations");
    var db = scope.ServiceProvider.GetRequiredService<MtrxDbContext>();
    // Boot resiliente: se o Postgres ainda não está aceitando conexões (PC lento, container
    // recém-subido), a 1ª tentativa de migração falha. Em vez de crashar — o que vira crash-loop
    // visível no browser como ERR_EMPTY_RESPONSE — tentamos de novo com backoff por ~2min antes
    // de desistir. O depends_on:service_healthy cobre o caso normal; isto é o cinto extra pra
    // instalações em máquinas novas/lentas.
    const int maxMigrationAttempts = 30;
    var migrationRetryDelay = TimeSpan.FromSeconds(4);
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            var pendingList = (await db.Database.GetPendingMigrationsAsync()).ToList();
            if (pendingList.Count > 0)
            {
                migrationLogger.LogInformation("Aplicando {Count} migrations: {Names}",
                    pendingList.Count, string.Join(", ", pendingList));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await db.Database.MigrateAsync();
                migrationLogger.LogInformation("Migrations aplicadas em {ElapsedMs} ms", sw.ElapsedMilliseconds);
            }
            else
            {
                migrationLogger.LogInformation("Banco já está na última migration.");
            }
            break;
        }
        // Só erros de banco (DbException → NpgsqlException): conexão recusada/timeout enquanto o
        // Postgres sobe. Não engole exceção genérica — se for outra coisa, falha rápido em vez de
        // mascarar por 2min. CA1031 não dispara porque o tipo é específico.
        catch (DbException ex) when (attempt < maxMigrationAttempts)
        {
            migrationLogger.LogWarning(ex,
                "Banco indisponível (tentativa {Attempt}/{Max}); aguardando {Delay}s e tentando de novo.",
                attempt, maxMigrationAttempts, migrationRetryDelay.TotalSeconds);
            await Task.Delay(migrationRetryDelay);
        }
    }

    var seedLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Seed");
    await AdminSeeder.SeedAdminIfEmptyAsync(scope.ServiceProvider, seedLogger, default);

    var wahaLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("WahaSetup");
    await WahaWebhookEnsurer.EnsureAsync(scope.ServiceProvider, wahaLogger, default);
}

app.UseCors();
app.UseExceptionHandler();
app.UseOpenApi();
app.UseSwaggerUi();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapPresenceEndpoints();
app.MapAuthEndpoints();
app.MapWebhookEndpoints();
app.MapConversationsEndpoints();
app.MapContactsEndpoints();
app.MapTagsEndpoints();
app.MapWahaEndpoints();
app.MapGroupsEndpoints();
app.MapCollectorEndpoints();
app.MapCampaignsEndpoints();
app.MapOptOutPublicEndpoints();
app.MapPhoneEndpoints();

await app.RunAsync();
