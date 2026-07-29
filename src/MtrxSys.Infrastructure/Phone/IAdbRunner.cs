namespace MtrxSys.Infrastructure.Phone;

/// <summary>COMO se fala com um aparelho Android. Separa o transporte (container x cabo) da lógica
/// (abrir conversa, digitar, ler a agenda), que é idêntica nos dois casos porque sempre foi Android,
/// nunca foi Docker.</summary>
/// <remarks>
/// A fronteira existe porque o orquestrador atual tem ~1700 linhas e quase nenhuma é sobre Docker: o
/// envio pela UI, a agenda e a leitura de banco são `adb shell` puro. Trocar o prefixo do comando é o
/// que basta pra o mesmo código dirigir um emulador ou um celular real.
/// </remarks>
internal interface IAdbRunner
{
    /// <summary>Roda um comando no shell do aparelho. A string vai INTEIRA pro `adb shell`, então quem
    /// monta precisa citar o que tiver espaço/metacaractere (ver ShellQuote no driver).</summary>
    Task<(int Code, string Out, string Err)> ShellAsync(string command, CancellationToken ct);

    /// <summary>Roda `adb` com argumentos separados (install, pull, get-state…). Cada argumento vai como
    /// um item de ArgumentList, então NÃO passa pelo shell e não precisa de citação.</summary>
    Task<(int Code, string Out, string Err)> RawAsync(CancellationToken ct, params string[] args);

    /// <summary>O aparelho tem `su 0`? Emulador userdebug tem; celular de varejo NÃO.
    /// <para>Quem depende disso são os leitores dos bancos privados do WhatsApp (`msgstore.db`, `wa.db`).
    /// Existir como propriedade é o que permite falhar EXPLICITAMENTE em vez de devolver vazio — e vazio,
    /// nesse caminho, é indistinguível de "esse número não tem WhatsApp", que é um veredito terminal.
    /// Ver docs/engine-physical.md.</para></summary>
    bool SupportsRoot { get; }

    /// <summary>Identificação curta do alvo, pra mensagem de erro e log ("mtrx-dandroid" ou o serial).</summary>
    string Target { get; }
}
