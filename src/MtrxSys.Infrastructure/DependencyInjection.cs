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
using MtrxSys.Core.Application.Warmup;
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
        // Fase Humana (dias 1-3 do chip novo). EffectiveFrom ausente = desligado — é o que mantém os
        // stacks já em produção intactos.
        services.AddOptions<HumanPhaseOptions>().Bind(config.GetSection(HumanPhaseOptions.SectionName));
        // Motor de aquecimento de CONVERSA (pool) — distinto do WarmupOptions (teto diário de envio).
        services.AddOptions<WarmupEngineOptions>().Bind(config.GetSection(WarmupEngineOptions.SectionName));
        services.AddOptions<OptOutOptions>().Bind(config.GetSection(OptOutOptions.SectionName));
        services.AddOptions<WahaOptions>()
            .Bind(config.GetSection(WahaOptions.SectionName))
            // Fonte única do token: reusa Webhooks:WahaToken (o mesmo que a API valida no endpoint),
            // pra não duplicar o segredo numa Waha:WebhookToken separada. Assim o WahaClient grava o
            // customHeader na sessão do WAHA. IMPORTANTE: usa IsNullOrWhiteSpace, não `??=` — o Bind
            // pode materializar "" (env unset lido como vazio / appsettings ""), e "" NÃO é null, então
            // o `??=` antigo NÃO sobrepunha → a sessão nascia com token VAZIO → o WAHA mandava vazio →
            // a api rejeitava o webhook com 401 (acks/inbound/SAIR não chegavam). IsNullOrWhiteSpace
            // cobre os dois casos (null e vazio).
            .PostConfigure(o =>
            {
                if (string.IsNullOrWhiteSpace(o.WebhookToken))
                {
                    o.WebhookToken = config["Webhooks:WahaToken"];
                }
            })
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
        // Há banco compartilhado disponível? (só a connection string, independente do Mode.) O contador
        // de busca compartilhado usa ESTE banco mesmo com o phone-ledger desligado (Mode=Off).
        var hasSharedDb = !string.IsNullOrWhiteSpace(ledgerConn);
        var ledgerActive = hasSharedDb
            && !string.IsNullOrWhiteSpace(ledgerMode)
            && !string.Equals(ledgerMode, nameof(SharedLedgerMode.Off), StringComparison.OrdinalIgnoreCase);
        // A fonte do banco compartilhado é registrada sempre que há connection string (a usam o
        // phone-ledger E o contador de busca). O phone-ledger em si só liga com Mode != Off.
        if (hasSharedDb)
        {
            services.AddSingleton(_ => new SharedLedgerDataSource(ledgerConn!));
        }
        if (ledgerActive)
        {
            services.AddScoped<ISharedPhoneLedger, NpgsqlSharedPhoneLedger>();
        }
        else
        {
            services.AddSingleton<ISharedPhoneLedger, NoOpSharedPhoneLedger>();
        }

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IRandomSource, CryptoRandomSource>();
        services.AddSingleton<IDispatchMetrics, NullDispatchMetrics>();
        // Aparelho virtual da aba "Celular": a API provisiona/liga/instala/registra um Android em
        // container via docker CLI sobre o socket — tudo DENTRO da aba, sem prompt. O Android vira o
        // dispositivo PRINCIPAL do número e o WAHA fica como companion (disparo). Fail-safe onde não há
        // docker (aba mostra "indisponível"). Dois engines (Phone:Engine), mesmo contrato:
        //   • docker-android (default): budtmo, noVNC embutido, exige /dev/kvm.
        //   • redroid: sem KVM (binder/ashmem do host), leve pros 10; tela via ws-scrcpy.
        services.AddOptions<PhoneOptions>().Bind(config.GetSection(PhoneOptions.SectionName));
        var phoneEngine = config[$"{PhoneOptions.SectionName}:Engine"];
        if (string.Equals(phoneEngine, "redroid", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IPhoneOrchestrator, MtrxSys.Infrastructure.Phone.RedroidPhoneOrchestrator>();
        }
        else
        {
            services.AddSingleton<IPhoneOrchestrator, MtrxSys.Infrastructure.Phone.DockerCliPhoneOrchestrator>();
        }

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
        services.AddScoped<IOwnedGroupRepository, OwnedGroupRepository>();
        services.AddScoped<IWarmupCircleRepository, WarmupCircleRepository>();
        services.AddScoped<IHumanPhaseProgressRepository, HumanPhaseProgressRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<OptOutLinkSigner>();

        services.AddScoped<ImportGroupMembersUseCase>();
        services.AddScoped<AddManualContactsUseCase>();
        services.AddScoped<MarkContactOptOutUseCase>();
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
        services.AddScoped<HumanPhaseGate>();

        // Aquecimento de conversa (pool): estado em memória (singleton), banco de frases, fábrica de
        // clientes WAHA por membro (named HttpClient com handler pooled) e o motor (scoped — usa o
        // ISystemStateRepository pra ler o toggle). O WarmupWorker (host da API) roda o ciclo.
        // Timeout explícito: sem ele, um peer lento/morto seguraria o worker por até 100s (default do
        // HttpClient) por chamada — um par com um WAHA fora travaria a conversa inteira. 25s ≈ o teto
        // do WAHA local. As chamadas são best-effort (falha é logada e a conversa segue).
        services.AddHttpClient("warmup-pool", c => c.Timeout = TimeSpan.FromSeconds(25));
        services.AddSingleton<WarmupState>();
        services.AddSingleton<IWarmupContentProvider, TemplateWarmupContentProvider>();
        services.AddSingleton<IPoolWahaClientFactory, MtrxSys.Infrastructure.Warmup.PoolWahaClientFactory>();
        services.AddScoped<WarmupEngine>();
        // Envio automático da Fase Humana. Distinto do WarmupEngine acima: aquele é o POOL (chip
        // conversando com os SEUS outros chips, mão dupla); este manda pro círculo de pessoas reais
        // pela via do Chat, e só existe enquanto a fase de um chip novo não fechou.
        services.AddScoped<HumanPhaseAutoSender>();

        // Alerta operacional (chip offline etc.) por webhook configurável (Slack/Discord/relay). No-op
        // sem Alert:WebhookUrl. Timeout curto: um alerta lento não pode segurar o watch de sessão.
        services.AddHttpClient<IAlertNotifier, MtrxSys.Infrastructure.Alerting.WebhookAlertNotifier>(
            c => c.Timeout = TimeSpan.FromSeconds(10));

        // Coletor de grupos: fila em memória (endpoint → worker), trava anti-ban da entrada, motivo
        // da última falha de busca (pro painel) e medidor de uso da busca. O medidor é COMPARTILHADO
        // (agrega os 10 ambientes, persistente) quando há banco compartilhado; senão, local em memória.
        services.AddSingleton<IGroupCollectorChannel, InMemoryGroupCollectorChannel>();
        services.AddSingleton<JoinThrottle>();
        services.AddSingleton<ISearchStatus, InMemorySearchStatus>();
        if (hasSharedDb)
        {
            services.AddSingleton<ISearchUsageMeter, SharedSearchUsageMeter>();
        }
        else
        {
            services.AddSingleton<ISearchUsageMeter, InMemorySearchUsageMeter>();
        }

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

        // Fonte de BUSCA por nicho. Se há chave do Serper (índice do Google, faixa grátis), usa o
        // Serper; senão, o SearXNG auto-hospedado. Ambos baixam o CORPO das páginas de resultado pra
        // extrair os convites — por isso o teto de 5 MB (uma página gigante/maliciosa estouraria a
        // memória) e o User-Agent. Sem BaseAddress nem retry/breaker: usam URLs absolutas e toleram
        // falha por página (o retry/circuit-breaker padrão atrapalharia ao varrer muitas páginas).
        void ConfigureSearchClient(System.Net.Http.HttpClient client)
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.MaxResponseContentBufferSize = 5_000_000;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; MtrxSysCollector/1.0)");
        }
        // Ambas as fontes ficam disponíveis (cada uma com seu HttpClient). O CompositeSearchSource
        // tenta o Serper (quando há chave) e CAI no SearXNG quando o Serper esgota/erra — a busca por
        // nicho não morre quando a cota grátis acaba. Sem chave do Serper, usa direto o SearXNG.
        services.AddHttpClient<SerperSearchSource>(ConfigureSearchClient);
        services.AddHttpClient<SearxngSearchSource>(ConfigureSearchClient);
        services.AddTransient<IGroupLinkSearchSource>(sp => new CompositeSearchSource(
            primary: sp.GetRequiredService<SerperSearchSource>(),
            secondary: sp.GetRequiredService<SearxngSearchSource>(),
            sp.GetRequiredService<ISearchStatus>()));

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
