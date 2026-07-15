using MtrxSys.Dispatcher;
using MtrxSys.Infrastructure;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((sp, lc) => lc
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.AddInfrastructure(builder.Configuration);
// Singleton: guarda o estado do "reassentar após reconectar" ENTRE ciclos (o engine é Scoped).
builder.Services.AddSingleton<DispatchSettleTracker>();
// Singleton pelo mesmo motivo: latch da Fase Humana entre ciclos (evita recomputar o progresso
// depois que ela fecha). É só cache — perder num restart apenas recomputa.
builder.Services.AddSingleton<HumanPhaseTracker>();
builder.Services.AddScoped<DispatchEngine>();
builder.Services.AddHostedService<DispatchWorker>();

var host = builder.Build();
host.Run();
