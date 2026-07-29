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
        // 🔴 SEM SERIAL NÃO RODA. O código antigo omitia o `-s` quando o serial vinha vazio, e isso é
        // pior que falhar: com UM aparelho plugado o adb escolhe sozinho e tudo FUNCIONA — em teste, em
        // homologação, na demonstração. O erro só aparece quando alguém pluga o segundo celular, e aí a
        // mensagem sai pelo chip errado, em silêncio. `Phone__AdbSerial` tem default vazio, então esse
        // caminho era alcançável só esquecendo uma variável de ambiente.
        //
        // Devolver código -1 é o mesmo contrato de "CLI indisponível" que o RunAsync já usa, então os
        // chamadores tratam como aparelho fora sem nenhuma mudança — e o motivo vai junto na mensagem.
        if (string.IsNullOrWhiteSpace(_serial))
        {
            return Task.FromResult((-1, string.Empty,
                "Phone__AdbSerial não configurado. O engine físico exige o serial do aparelho "
                + "(veja em `adb devices`) — sem ele, o adb escolheria sozinho qual celular receber o "
                + "comando, e com mais de um plugado o envio sairia pelo chip errado."));
        }

        string[] full = [.. new[] { "-s", _serial }, .. args];
        return DockerCli.RunAsync(_exe, ct, full);
    }
}
