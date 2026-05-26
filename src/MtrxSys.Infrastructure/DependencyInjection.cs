using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Contacts;
using MtrxSys.Core.Application.UseCases.Webhooks;
using MtrxSys.Core.Messaging;
using MtrxSys.Core.Safety;
using MtrxSys.Core.Validation;
using MtrxSys.Infrastructure.Auth;
using MtrxSys.Infrastructure.Metrics;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;
using MtrxSys.Infrastructure.Randomness;
using MtrxSys.Infrastructure.Time;
using MtrxSys.Infrastructure.Waha;

namespace MtrxSys.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<DispatchOptions>().Bind(config.GetSection(DispatchOptions.SectionName));
        services.AddOptions<CircuitBreakerOptions>().Bind(config.GetSection(CircuitBreakerOptions.SectionName));
        services.AddOptions<WarmupOptions>().Bind(config.GetSection(WarmupOptions.SectionName));
        services.AddOptions<WahaOptions>()
            .Bind(config.GetSection(WahaOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<JwtOptions>()
            .Bind(config.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContext<MtrxDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Postgres")));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IRandomSource, CryptoRandomSource>();
        services.AddSingleton<IDispatchMetrics, NullDispatchMetrics>();

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IContactRepository, ContactRepository>();
        services.AddScoped<IDispatchJobRepository, DispatchJobRepository>();
        services.AddScoped<ISystemStateRepository, SystemStateRepository>();
        services.AddScoped<IDailySendCountsRepository, DailySendCountsRepository>();
        services.AddScoped<IMessageTemplateRepository, MessageTemplateRepository>();
        services.AddScoped<ISendAuditRepository, SendAuditRepository>();
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IChatMessageRepository, ChatMessageRepository>();
        services.AddScoped<IContactNoteRepository, ContactNoteRepository>();
        services.AddScoped<IContactTagRepository, ContactTagRepository>();
        services.AddScoped<IContactStageChangeRepository, ContactStageChangeRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();

        services.AddScoped<ImportGroupMembersUseCase>();
        services.AddScoped<MtrxSys.Core.Application.UseCases.Conversations.RelinkOrphanConversationsUseCase>();
        services.AddScoped<IWebhookIngestionService, WebhookIngestionService>();
        services.AddScoped<WhatsAppSyncService>();

        services.AddScoped<BrazilPhoneValidator>();
        services.AddScoped<SpintaxExpander>();
        services.AddScoped<MessageComposer>();
        services.AddScoped<DelayPolicy>();
        services.AddScoped<TypingSimulator>();
        services.AddScoped<CircuitBreaker>();
        services.AddScoped<WarmupManager>();

        services.AddHttpClient<IWahaClient, WahaClient>((sp, client) =>
        {
            var wahaOpts = sp.GetRequiredService<IOptions<WahaOptions>>().Value;
            var baseUrl = wahaOpts.BaseUrl.EndsWith('/') ? wahaOpts.BaseUrl : wahaOpts.BaseUrl + "/";
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(wahaOpts.TimeoutSeconds);
        })
        .AddStandardResilienceHandler();

        services.AddLogging(b => b.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning));

        return services;
    }
}
