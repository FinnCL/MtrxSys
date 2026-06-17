using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
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
using MtrxSys.Infrastructure.Collector;
using MtrxSys.Infrastructure.Metrics;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;
using MtrxSys.Infrastructure.Randomness;
using MtrxSys.Infrastructure.SharedLedger;
using MtrxSys.Infrastructure.Time;
using MtrxSys.Infrastructure.Waha;

namespace MtrxSys.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<DispatchOptions>().Bind(config.GetSection(DispatchOptions.SectionName));
        services.AddOptions<CollectorOptions>().Bind(config.GetSection(CollectorOptions.SectionName));
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

        // Registro compartilhado entre os 10 ambientes (dedup de disparo cross-chip + opt-out global).
        // DESLIGADO por padrão: só ativa com SharedLedger:Mode != Off E uma connection string própria
        // (ConnectionStrings:SharedLedger), apontando pro banco compartilhado. Sem isso → no-op, e o
        // comportamento atual fica intacto. A implementação é fail-open (nunca trava o disparo).
        services.AddOptions<SharedLedgerOptions>().Bind(config.GetSection(SharedLedgerOptions.SectionName));
        var ledgerMode = config.GetSection(SharedLedgerOptions.SectionName)["Mode"];
        var ledgerConn = config.GetConnectionString("SharedLedger");
        var ledgerActive = !string.IsNullOrWhiteSpace(ledgerConn)
            && !string.IsNullOrWhiteSpace(ledgerMode)
            && !string.Equals(ledgerMode, nameof(SharedLedgerMode.Off), StringComparison.OrdinalIgnoreCase);
        if (ledgerActive)
        {
            services.AddSingleton(_ => new SharedLedgerDataSource(ledgerConn!));
            services.AddScoped<ISharedPhoneLedger, NpgsqlSharedPhoneLedger>();
        }
        else
        {
            services.AddSingleton<ISharedPhoneLedger, NoOpSharedPhoneLedger>();
        }

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
        services.AddScoped<IGroupLinkRepository, GroupLinkRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();

        services.AddScoped<ImportGroupMembersUseCase>();
        services.AddScoped<AddManualContactsUseCase>();
        services.AddScoped<MtrxSys.Core.Application.UseCases.Groups.CollectGroupLinksUseCase>();
        services.AddScoped<MtrxSys.Core.Application.UseCases.Conversations.RelinkOrphanConversationsUseCase>();
        services.AddScoped<IWebhookIngestionService, WebhookIngestionService>();
        services.AddScoped<WhatsAppSyncService>();
        services.AddScoped<MtrxSys.Core.Application.UseCases.Webhooks.OptOutReconciler>();

        services.AddScoped<BrazilPhoneValidator>();
        services.AddScoped<SpintaxExpander>();
        services.AddScoped<MessageComposer>();
        services.AddScoped<DelayPolicy>();
        services.AddScoped<TypingSimulator>();
        services.AddScoped<CircuitBreaker>();
        services.AddScoped<WarmupManager>();

        // Coletor de grupos: fila em memória (endpoint → worker) e trava anti-ban da entrada.
        services.AddSingleton<IGroupCollectorChannel, InMemoryGroupCollectorChannel>();
        services.AddSingleton<JoinThrottle>();

        // Timeout efetivo das chamadas ao WAHA. O default do handler de resiliência é 30s e
        // IGNORA o Waha:TimeoutSeconds — por isso lemos a config aqui e configuramos o Polly.
        var wahaTimeoutSeconds = config.GetValue<int?>($"{WahaOptions.SectionName}:TimeoutSeconds") ?? 30;
        var wahaTimeout = TimeSpan.FromSeconds(wahaTimeoutSeconds);

        services.AddHttpClient<IWahaClient, WahaClient>((sp, client) =>
        {
            var wahaOpts = sp.GetRequiredService<IOptions<WahaOptions>>().Value;
            var baseUrl = wahaOpts.BaseUrl.EndsWith('/') ? wahaOpts.BaseUrl : wahaOpts.BaseUrl + "/";
            client.BaseAddress = new Uri(baseUrl);
            // Quem governa o tempo é o handler de resiliência (Polly). O HttpClient não impõe teto
            // próprio — senão cortaria antes (em 30s) e brigaria com o TotalRequestTimeout abaixo.
            client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        })
        .AddStandardResilienceHandler().Configure(options =>
        {
            // AttemptTimeout é o teto REAL por chamada (honra o Waha:TimeoutSeconds). Como não há
            // retry de transporte (MaxRetryAttempts=0), é ele que governa o tempo de cada sendText.
            options.AttemptTimeout.Timeout = wahaTimeout;
            // Validação do Polly: TotalRequestTimeout precisa ser ESTRITAMENTE maior que o
            // AttemptTimeout — deixamos o dobro (limite externo que, sem retries, não dispara).
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(wahaTimeoutSeconds * 2);
            // Validação do Polly: SamplingDuration do breaker interno >= 2 × AttemptTimeout.
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(wahaTimeoutSeconds * 2);
            // Não re-tenta métodos não idempotentes (POST do sendText → evita duplicar a mensagem);
            // GETs (status/grupos/qr) continuam com retry. O reenvio do disparo é feito no nível do
            // app (DispatchEngine), de forma controlada. NB: MaxRetryAttempts=0 é REJEITADO pela
            // validação do handler (precisa ser >= 1) — por isso desligamos via ShouldHandle.
            options.Retry.DisableForUnsafeHttpMethods();
            // 1 retry (em vez de 3): o join-info de um convite MORTO devolve 500 que NÃO se recupera
            // re-tentando — 3 retries só desperdiçavam ~10s por link morto e entupiam o enriquecimento.
            options.Retry.MaxRetryAttempts = 1;
        });

        // Fonte do Coletor: lê a prévia web de canais públicos de Telegram (HTML, sem login/JS).
        // Sai pelo IP da máquina (o proxy é só do container WAHA) — ok pra localhost.
        services.AddHttpClient<IGroupLinkSource, TelegramChannelSource>(client =>
        {
            client.BaseAddress = new Uri("https://t.me/");
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; MtrxSysCollector/1.0)");
        })
        .AddStandardResilienceHandler();

        // Fonte de BUSCA por nicho (SearXNG auto-hospedado — web inteira, sem chave). A URL do
        // SearXNG vem das opções (Collector:SearxngBaseUrl); vazia = fonte não-configurada → cai no
        // Telegram. Sem BaseAddress nem retry/breaker: a fonte usa URLs absolutas (o SearXNG E as
        // páginas de resultado que ela visita pra varrer o corpo) e tolera falha por página — o
        // retry/circuit-breaker padrão atrapalharia ao varrer muitas páginas externas.
        services.AddHttpClient<IGroupLinkSearchSource, SearxngSearchSource>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            // Teto de 5 MB por resposta: a fonte baixa páginas arbitrárias da web; sem isso, uma
            // página gigante/maliciosa poderia estourar a memória (ReadAsStringAsync lança ao exceder).
            client.MaxResponseContentBufferSize = 5_000_000;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; MtrxSysCollector/1.0)");
        });

        // Validação de convite pela página pública (chat.whatsapp.com) — sem WAHA, rápida.
        services.AddHttpClient<IWhatsAppInviteValidator, WhatsAppInviteValidator>(client =>
        {
            client.BaseAddress = new Uri("https://chat.whatsapp.com/");
            client.Timeout = TimeSpan.FromSeconds(10);
            client.MaxResponseContentBufferSize = 2_000_000;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; MtrxSysCollector/1.0)");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR");
        });

        services.AddLogging(b => b.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning));

        return services;
    }
}
