using System.Runtime.InteropServices;

namespace MtrxSys.Cli.Infrastructure;

/// <summary>Impede o Windows de SUSPENDER enquanto o lote roda. Solte com <see cref="Dispose"/>.</summary>
/// <remarks>
/// 🔴 O CASO QUE MOTIVOU: a pausa entre blocos é de ~30 min SEM nenhuma entrada de teclado ou mouse,
/// que é exatamente o gatilho do standby do Windows. O lote morria no meio da noite e o sintoma era
/// indistinguível do celular dormindo: some e ninguém sabe por quê. É o mesmo defeito do aparelho, do
/// outro lado do cabo, e ninguém tinha ligado os dois.
///
/// <para>O <c>docs/aparelho-fisico-passo-a-passo.md</c> já mandava rodar <c>powercfg</c> antes de
/// operar. Passo manual e documentado é passo esquecido: quem troca de PC (existe até um
/// <c>migrar-para-outro-pc.md</c>) recomeça sem ele, e nada confere. Segurar aqui resolve para todo
/// mundo, inclusive para quem nunca leu o doc.</para>
///
/// <para>⚠️ NÃO segura a TELA acesa, de propósito. O doc já registra a distinção que importa: apagar a
/// tela não atrapalha, porque o Windows continua rodando e o adb continua conectado. O que mata é a
/// SUSPENSÃO. Pedir <c>ES_DISPLAY_REQUIRED</c> junto deixaria um monitor aceso a noite toda sem ganho
/// nenhum.</para>
///
/// <para>⚠️ TAMBÉM NÃO cobre FECHAR A TAMPA. Isso é política de energia do Windows, e nenhuma API de
/// processo a sobrepõe: continua valendo o passo manual de "Escolher o que a tampa faz" → "Não fazer
/// nada". A mensagem no início do lote diz isso em voz alta, em vez de deixar a proteção parcial passar
/// por completa.</para>
///
/// <para>Uma THREAD dedicada, e não uma chamada solta: o estado do
/// <c>SetThreadExecutionState</c> é POR THREAD e morre junto com ela. Num método assíncrono a
/// continuação pode voltar em outra thread do pool, e a que registrou o pedido pode simplesmente
/// deixar de existir — a proteção sumiria em silêncio, que é o pior jeito de uma proteção falhar. A
/// thread aqui vive exatamente o tempo do lote.</para>
/// </remarks>
internal sealed class PcAcordado : IDisposable
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    private readonly ManualResetEventSlim _solta = new(false);
    private readonly Thread? _thread;

    private PcAcordado(bool ligar)
    {
        if (!ligar)
        {
            return;
        }
        // Background: se algo escapar do Dispose, a thread não segura o encerramento do processo.
        _thread = new Thread(Segurar) { IsBackground = true, Name = "mtrx-pc-acordado" };
        _thread.Start();
    }

    /// <summary>Segura o PC acordado até o Dispose. Fora do Windows devolve um objeto inerte.</summary>
    /// <remarks>Inerte em vez de erro: o CLI é de operação, e quem roda noutro sistema tem que ver o
    /// lote andar, não uma falha de plataforma vinda de um detalhe de energia.</remarks>
    public static PcAcordado Ligar() => new(OperatingSystem.IsWindows());

    /// <summary>true quando a proteção está de fato ativa (só no Windows).</summary>
    public bool Ativo => _thread is not null;

    private void Segurar()
    {
        // A guarda fica AQUI, e não só em quem constrói: o analisador de plataforma segue o fluxo do
        // método, não um bool que veio de longe. Conferir de novo custa nada e mantém a build limpa.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // ES_CONTINUOUS = "vale até eu mudar", em vez de um cutucão que zera o contador uma vez só.
        // Somado a ES_SYSTEM_REQUIRED, diz ao Windows que o sistema está em uso mesmo sem teclado.
        _ = SetThreadExecutionState(EsContinuous | EsSystemRequired);
        _solta.Wait();
        // Devolve o comportamento normal ANTES de a thread morrer. Sem isto o PC até voltaria ao normal
        // (o estado morre com a thread), mas por acidente, e num horário que ninguém controla.
        _ = SetThreadExecutionState(EsContinuous);
    }

    public void Dispose()
    {
        _solta.Set();
        // 🔴 SÓ DESCARTA O EVENTO SE A THREAD DE FATO SAIU. Descartar com ela ainda dentro do `Wait`
        // joga ObjectDisposedException numa thread de fundo, e exceção não tratada em QUALQUER thread
        // derruba o processo — ou seja, o lote inteiro morreria por causa da limpeza de um detalhe de
        // energia. Se o Join estourar (só se o P/Invoke pendurar), vazar um handle é o desfecho barato:
        // o processo do console é de vida curta e o SO recolhe no fim.
        if (_thread is null || _thread.Join(TimeSpan.FromSeconds(2)))
        {
            _solta.Dispose();
        }
    }

    // DllImport e não LibraryImport: o gerador do LibraryImport exige AllowUnsafeBlocks no PROJETO
    // inteiro, e abrir `unsafe` no CLI por causa de uma assinatura `uint -> uint` é preço alto demais
    // pelo ganho (marshalling gerado que aqui não tem o que marshalar).
#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
#pragma warning restore SYSLIB1054
}
