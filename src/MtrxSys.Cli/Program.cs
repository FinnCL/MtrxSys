using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MtrxSys.Cli.Commands;
using MtrxSys.Cli.Infrastructure;
using MtrxSys.Infrastructure;
using Spectre.Console.Cli;

Console.OutputEncoding = Encoding.UTF8;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(configuration);
services.AddSingleton(new CancellationTokenProvider(cts.Token));
services.AddInfrastructure(configuration);

var registrar = new TypeRegistrar(services);
var app = new CommandApp<ChatCommand>(registrar);

app.Configure(cfg =>
{
    cfg.SetApplicationName("mtrx");
    cfg.AddBranch("auth", auth =>
    {
        auth.SetDescription("Sessão WAHA (status + QR pra parear)");
        auth.AddCommand<AuthStatusCommand>("status").WithDescription("Mostra o estado da sessão WAHA.");
        auth.AddCommand<AuthQrCommand>("qr").WithDescription("Baixa o QR code da sessão para um arquivo PNG.");
    });
    cfg.AddBranch("phone", p =>
    {
        p.SetDescription("Aparelho (emulador ou físico) — bancada do piloto, sem banco e sem fila");
        p.AddCommand<PhoneSendCommand>("send")
            .WithDescription("Envia pela UI do aparelho. Respeita Phone__Engine (physical | docker-android).");
    });
    cfg.AddBranch("groups", g =>
    {
        g.SetDescription("Grupos do WhatsApp");
        g.AddCommand<GroupsListCommand>("list").WithDescription("Lista os grupos da sessão.");
        g.AddCommand<GroupsMembersCommand>("members").WithDescription("Importa membros de um grupo para o banco como Contacts.");
        g.AddCommand<GroupsJoinCommand>("join").WithDescription("Entra num grupo via link de convite.");
    });
});

return await app.RunAsync(args);
