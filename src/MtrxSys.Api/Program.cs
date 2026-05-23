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
builder.Services.AddOptions<MtrxSys.Api.Options.WebhookOptions>()
    .Bind(builder.Configuration.GetSection(MtrxSys.Api.Options.WebhookOptions.SectionName));
builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
    p.AllowAnyHeader().AllowAnyMethod().WithOrigins("http://localhost:5173")));

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
    var pending = await db.Database.GetPendingMigrationsAsync();
    var pendingList = pending.ToList();
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

    var seedLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Seed");
    await AdminSeeder.SeedAdminIfEmptyAsync(scope.ServiceProvider, seedLogger, default);

    var wahaLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("WahaSetup");
    await WahaWebhookEnsurer.EnsureAsync(scope.ServiceProvider, wahaLogger, default);
}

app.UseCors();
app.UseOpenApi();
app.UseSwaggerUi();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapWebhookEndpoints();
app.MapConversationsEndpoints();
app.MapContactsEndpoints();
app.MapTagsEndpoints();
app.MapWahaEndpoints();
app.MapGroupsEndpoints();
app.MapCampaignsEndpoints();

await app.RunAsync();
