using FluentAssertions;
using MtrxSys.Infrastructure.Phone;

namespace MtrxSys.Infrastructure.IntegrationTests.Phone;

/// <summary>Prova como o <see cref="WhatsAppContactsReader"/> identifica o contato que acabou de criar.</summary>
/// <remarks>
/// <para>O `content insert` do Android não devolve o id do que criou, então o método deduz lendo a agenda
/// de novo. A dedução tem uma corrida que NÃO dá pra reproduzir no aparelho sob demanda: o WhatsApp e a
/// conta Google escrevem em raw_contacts quando querem, e quem provoca essa escrita é justamente a nossa
/// gravação. Um adb falso é o único jeito de colocar a corrida no lugar exato e conferir o que o código
/// faz com ela.</para>
/// <para>O que se guarda aqui não é "grava o contato" — isso o aparelho já mostrou. É a REGRA DE PARADA:
/// diante de um id ambíguo o método tem que recusar, porque gravar é idempotente e barato de repetir,
/// enquanto escrever no contato de outra pessoa sobe pra conta Google e não tem desfazer.</para>
/// </remarks>
public sealed class WhatsAppContactsReaderTests
{
    private const string Numero = "+5584998420730";
    private static string Digitos => new([.. Numero.Where(char.IsDigit)]);

    /// <summary>Adb de mentira roteado por TRECHO do comando, na ordem em que o método os emite.</summary>
    /// <remarks>
    /// Guarda todos os comandos para o teste poder afirmar o que NÃO foi executado — que é o ponto dos
    /// casos de corrida: o valor está em nenhuma escrita ter saído, não na string devolvida.
    /// </remarks>
    private sealed class AdbFalso(Func<string, (int, string, string)> responder) : IAdbRunner
    {
        public List<string> Comandos { get; } = [];

        public Task<(int Code, string Out, string Err)> ShellAsync(string command, CancellationToken ct)
        {
            Comandos.Add(command);
            return Task.FromResult(responder(command));
        }

        public Task<(int Code, string Out, string Err)> RawAsync(CancellationToken ct, params string[] args) =>
            throw new NotSupportedException("a agenda só usa `adb shell`");

        public bool SupportsRoot => false;

        public string Target => "falso";
    }

    /// <summary>Linhas no formato do `content query --projection _id`.</summary>
    private static string Linhas(params int[] ids) =>
        string.Join('\n', ids.Select((id, i) => $"Row: {i} _id={id}"));

    /// <summary>Adb que representa uma agenda VAZIA do número alvo, com os ids que forem passados a cada
    /// leitura de raw_contacts (a primeira é o "antes", a segunda o "depois").</summary>
    private static AdbFalso AgendaCom(string[] leiturasDeIds, bool encontraNoFinal)
    {
        var leitura = 0;
        var telefoneGravado = false;
        return new AdbFalso(cmd =>
        {
            if (cmd.Contains("phone_lookup", StringComparison.Ordinal))
            {
                // Antes de gravar o número não está lá; depois, só se o teste disser que está. É assim
                // que a pós-condição é exercida nos dois sentidos.
                return telefoneGravado && encontraNoFinal
                    ? (0, "Row: 0 contact_id=42", "")
                    : (0, "No result found.", "");
            }
            if (cmd.Contains("query", StringComparison.Ordinal) && cmd.Contains("raw_contacts", StringComparison.Ordinal))
            {
                var r = leiturasDeIds[Math.Min(leitura, leiturasDeIds.Length - 1)];
                leitura++;
                return (0, r, "");
            }
            if (cmd.Contains("phone_v2", StringComparison.Ordinal))
            {
                telefoneGravado = true;
            }
            return (0, "", ""); // inserts
        });
    }

    [Fact]
    public async Task Sem_concorrencia_grava_e_confirma()
    {
        // O id salta de 10 pra 11: exatamente a UMA linha que nós inserimos.
        var adb = AgendaCom([Linhas(9, 10), Linhas(9, 10, 11)], encontraNoFinal: true);

        var r = await new WhatsAppContactsReader(adb).SaveContactAsync(Numero, "Fulano", CancellationToken.None);

        r.Should().Be("ok");
        adb.Comandos.Should().Contain(c => c.Contains($"data1:s:+{Digitos}", StringComparison.Ordinal),
            "o telefone tem que ir em E.164 COM o +, que é a única forma que o WhatsApp resolve");
    }

    [Fact]
    public async Task Id_pulando_mais_de_um_recusa_sem_gravar_nada()
    {
        // 🔴 O caso que motivou tudo: o sync do WhatsApp (ou da conta Google) inseriu uma linha na
        // janela entre o nosso insert e a leitura. O maior id passa a ser de OUTRA PESSOA, e a regra
        // antiga ("o maior é o nosso") gravaria nome e telefone do alvo em cima do contato dela.
        var adb = AgendaCom([Linhas(9, 10), Linhas(9, 10, 11, 12)], encontraNoFinal: true);

        var r = await new WhatsAppContactsReader(adb).SaveContactAsync(Numero, "Fulano", CancellationToken.None);

        r.Should().Contain("mudou embaixo de mim");
        adb.Comandos.Should().NotContain(c => c.Contains("mimetype", StringComparison.Ordinal),
            "com o id ambíguo NENHUMA escrita de nome ou telefone pode sair: é o contato de outra "
            + "pessoa que estaria em jogo, e isso sobe pra conta Google sem desfazer");
    }

    [Fact]
    public async Task Nenhum_id_novo_recusa_sem_gravar_nada()
    {
        // O insert devolveu 0 e a agenda não mostrou nada novo. Seguir daqui gravaria em cima de um
        // contato que já existia antes de chegarmos.
        var adb = AgendaCom([Linhas(9, 10), Linhas(9, 10)], encontraNoFinal: true);

        var r = await new WhatsAppContactsReader(adb).SaveContactAsync(Numero, "Fulano", CancellationToken.None);

        r.Should().Contain("nenhum registro novo");
        adb.Comandos.Should().NotContain(c => c.Contains("mimetype", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Escrita_que_nao_aparece_na_agenda_nao_devolve_ok()
    {
        // Todos os comandos deram rc==0, mas a agenda continua sem resolver o número. É o buraco que a
        // pós-condição fecha: sem ela isto devolvia "ok" e o erro só reaparecia lá na frente,
        // disfarçado de "este número não tem WhatsApp" — a mensagem que acusa o contato quando a culpa
        // é da gravação.
        var adb = AgendaCom([Linhas(10), Linhas(10, 11)], encontraNoFinal: false);

        var r = await new WhatsAppContactsReader(adb).SaveContactAsync(Numero, "Fulano", CancellationToken.None);

        r.Should().NotStartWith("ok");
        r.Should().Contain("não encontra");
    }

    // A cura do registro envenenado (gravado só com dígitos crus) vive em EnvioPontaAPontaTests, contra
    // o AndroidFalso. Ela depende de COMO o phone_lookup compara valores, e o falso de lá reproduz a
    // semântica medida no aparelho em 2026-08-06; o `AdbFalso` daqui roteia por trecho de comando e
    // modelaria essa parte por conta própria, com risco de validar uma ficção.

    [Fact]
    public async Task Numero_curto_nem_toca_no_aparelho()
    {
        var adb = AgendaCom([Linhas(10), Linhas(10, 11)], encontraNoFinal: true);

        var r = await new WhatsAppContactsReader(adb).SaveContactAsync("+5511", null, CancellationToken.None);

        r.Should().Be("phone inválido");
        adb.Comandos.Should().BeEmpty();
    }
}
