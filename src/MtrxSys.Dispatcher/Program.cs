using MtrxSys.Dispatcher;
using MtrxSys.Infrastructure;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((sp, lc) => lc
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<DispatchEngine>();
builder.Services.AddHostedService<DispatchWorker>();

var host = builder.Build();
host.Run();
