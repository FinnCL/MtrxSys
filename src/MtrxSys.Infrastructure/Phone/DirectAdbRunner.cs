namespace MtrxSys.Infrastructure.Phone;

/// <summary>Fala com um aparelho FÍSICO pelo `adb` do host: `adb -s &lt;serial&gt; …`.</summary>
/// <remarks>
/// <para>O serial vem de `adb devices` e é estável por aparelho (ex.: RQ8WB048RFW). Fixá-lo em vez de
/// aceitar o "aparelho único" evita o modo de falha silencioso de mandar comando pro device errado
/// quando alguém pluga um segundo celular na mesma máquina.</para>
/// <para>⚠️ SEM ROOT. Celular de varejo não tem `su 0`, então os leitores dos bancos privados do
/// WhatsApp não funcionam por aqui. Ver docs/engine-physical.md.</para>
/// </remarks>
internal sealed class DirectAdbRunner(string serial, string adbPath) : IAdbRunner
{
    private readonly string _serial = serial;
    private readonly string _exe = string.IsNullOrWhiteSpace(adbPath) ? "adb" : adbPath;

    public bool SupportsRoot => false;

    public string Target => _serial;

    public Task<(int Code, string Out, string Err)> ShellAsync(string command, CancellationToken ct) =>
        RawAsync(ct, "shell", command);

    public Task<(int Code, string Out, string Err)> RawAsync(CancellationToken ct, params string[] args)
    {
        // -s SEMPRE, mesmo com um aparelho só: sem ele o adb escolhe sozinho e, com dois plugados,
        // falha com "more than one device" — ou pior, num cenário de duas máquinas compartilhando
        // servidor adb, acerta o aparelho de outro chip.
        string[] full = string.IsNullOrWhiteSpace(_serial)
            ? args
            : [.. new[] { "-s", _serial }, .. args];
        return DockerCli.RunAsync(_exe, ct, full);
    }
}
