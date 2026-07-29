namespace MtrxSys.Infrastructure.Phone;

/// <summary>Fala com o Android que roda DENTRO de um container: `docker exec &lt;container&gt; adb …`.</summary>
/// <remarks>
/// É o transporte que o <see cref="DockerCliPhoneOrchestrator"/> usa hoje inline. Esta classe existe
/// para o driver de UI poder ser compartilhado pelos dois mundos, e para os testes poderem trocar o
/// transporte por um falso.
/// <para>⚠️ Enquanto a Fase 1 durar, o <see cref="DockerCliPhoneOrchestrator"/> NÃO usa esta classe:
/// ele segue chamando <c>DockerCli.DockerAsync</c> direto. A troca é passo separado, feito só depois
/// que o caminho físico se provar — mexer nas ~600 linhas que estão em produção nos 10 stacks hoje
/// seria risco sem ganho. Ver docs/engine-physical.md.</para>
/// </remarks>
internal sealed class DockerAdbRunner(string containerName) : IAdbRunner
{
    private readonly string _container = containerName;

    // Imagem do emulador é userdebug e tem `su 0` — é o que permite ler msgstore.db/wa.db.
    public bool SupportsRoot => true;

    public string Target => _container;

    public Task<(int Code, string Out, string Err)> ShellAsync(string command, CancellationToken ct) =>
        DockerCli.DockerAsync(ct, "exec", _container, "adb", "shell", command);

    public Task<(int Code, string Out, string Err)> RawAsync(CancellationToken ct, params string[] args) =>
        DockerCli.DockerAsync(ct, [.. new[] { "exec", _container, "adb" }, .. args]);
}
