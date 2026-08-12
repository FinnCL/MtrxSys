using System.Text.RegularExpressions;

namespace MtrxSys.Infrastructure.Phone;

/// <summary>Agenda do Android (contacts provider) por adb. Grava contato e responde "esse número tem
/// WhatsApp?" pelo ESPELHO que o app publica na agenda.</summary>
/// <remarks>
/// <para>Nada aqui exige root: o `content query`/`content insert` roda com a identidade do shell, que
/// tem READ/WRITE_CONTACTS por padrão. Confirmado no físico em 2026-07-29.</para>
/// <para>🔴 A diferença que importa em relação ao engine do emulador: lá o veredito de existência tem
/// como FONTE PRIMÁRIA o `wa.db` do próprio WhatsApp, que exige root. Aqui só existe o espelho da
/// agenda — a mesma fonte que em 2026-07-27 ficou VAZIA por ~19h e fez o motor descartar 10 contatos
/// bons. Por isso <see cref="IsOnWhatsAppAsync"/> aqui só afirma o SIM: o "não" nunca é devolvido como
/// veredito, vira `null` (adia). Ver docs/engine-physical.md.</para>
/// </remarks>
internal sealed class WhatsAppContactsReader(IAdbRunner adb)
{
    private readonly IAdbRunner _adb = adb;

    private static string DigitsOf(string? phoneE164) =>
        new([.. (phoneE164 ?? string.Empty).Where(char.IsDigit)]);

    /// <summary>contact_id na saída do `content query`. Compilado e estático, como os do driver.</summary>
    /// <remarks>
    /// Roda por CONTATO: o `enviar` consulta a agenda antes de cada mensagem. O ganho de tempo é
    /// pequeno (a saída é curta), mas o padrão montado inline convivia com os compilados estáticos do
    /// <c>WhatsAppUiDriver</c> na mesma pasta, e duas convenções para a mesma coisa é a porta de
    /// entrada de "cada um faz de um jeito".
    /// </remarks>
    private static readonly Regex ContactIdRx = new(@"contact_id=(\d+)", RegexOptions.Compiled);

    /// <summary>O <c>_id</c> da linha <c>vnd.com.whatsapp.profile</c> deste número, com cache MUITO
    /// curto. null = não achei.</summary>
    /// <remarks>
    /// 🔴 LER PELO CONTATO AGREGADO NÃO ENXERGA O ESPELHO NESTE APARELHO, e o estrago era total: TODO
    /// número respondia "a agenda não confirma", inclusive 24 de 27 de um lote que estavam ativos no
    /// WhatsApp. Com o `segurar` ligado o console segurava a lista inteira, para sempre, e a mensagem
    /// culpava o contato quando a culpa era da consulta.
    ///
    /// <para>MEDIDO em 2026-08-12 no Galaxy A14 (SM_A145M, Android 15), contato 845 = +5511980370241:</para>
    /// <code>
    /// adb shell content query --uri content://com.android.contacts/contacts/845/data --projection mimetype
    ///   -> SÓ vnd.android.cursor.item/name e .../phone_v2
    /// adb shell content query --uri content://com.android.contacts/raw_contacts --where contact_id=845 --projection account_type
    ///   -> vnd.sec.contact.phone E com.whatsapp
    /// adb shell content query --uri content://com.android.contacts/raw_contacts/907/data --projection mimetype
    ///   -> .../vnd.com.whatsapp.profile, .../vnd.com.whatsapp.voip.call, .../video.call
    /// </code>
    /// A One UI acrescenta a coluna <c>sec_supports_uploading</c> e FILTRA a view de dados por ela. O
    /// próprio provider vazou o SQL ao recusar um `--where` malformado:
    /// <c>WHERE (1 AND sec_supports_uploading is not 0)</c>. A conta <c>com.whatsapp</c> não sobe para
    /// lugar nenhum, então as linhas dela somem do contato agregado. Pelo URI do RAW contact o filtro
    /// não se aplica e tudo aparece.
    ///
    /// <para>⚠️ Se for medir de novo, escreva aqui a DATA, o APARELHO e o COMANDO — a mesma disciplina
    /// do <see cref="LookupContactIdAsync"/>, e pelo mesmo motivo: foi a ausência dos três que deixou
    /// uma afirmação errada de pé com cara de evidência.</para>
    ///
    /// <para>Janela curta de propósito. O <c>SaveContactAsync</c> ESCREVE na agenda, e um cache generoso
    /// responderia "não está lá" logo depois de gravar. Alguns segundos cobrem as duas perguntas do
    /// mesmo envio — "tem WhatsApp?" (<see cref="IsOnWhatsAppAsync"/>) e "qual a URI da conversa?"
    /// (<see cref="WhatsAppChatUriAsync"/>), que antes pagavam a leitura em dobro — e vencem muito antes
    /// do contato seguinte, que vem depois de um intervalo de 150-360s.</para>
    /// </remarks>
    private async Task<string?> PerfilAsync(string digits, CancellationToken ct)
    {
        if (_cacheDigits == digits && Environment.TickCount64 - _cacheEmTicks < CacheMs)
        {
            return _cache;
        }
        var perfil = await AcharPerfilAsync(digits, ct);
        (_cacheDigits, _cache, _cacheEmTicks) = (digits, perfil, Environment.TickCount64);
        return perfil;
    }

    private const int CacheMs = 5_000;
    private string? _cacheDigits;
    private string? _cache;
    private long _cacheEmTicks;

    private async Task<string?> AcharPerfilAsync(string digits, CancellationToken ct)
    {
        // Nem está na agenda: quem chama grava e re-pergunta depois do sync.
        var contactId = await LookupContactIdAsync(digits, ct)
            ?? await LookupContactIdAsync("%2B" + digits, ct);
        if (contactId is null)
        {
            return null;
        }

        // Os raw contacts agregados neste contato. O `--where` é NUMÉRICO de propósito: um literal de
        // texto precisaria de aspas simples, e elas não sobrevivem à viagem até o shell do aparelho —
        // o provider recebe o valor sem aspas e responde erro de sintaxe SQL. Comparar id é imune a isso.
        var (rc, linhas, _) = await _adb.ShellAsync(
            "content query --uri content://com.android.contacts/raw_contacts "
            + $"--where contact_id={contactId}", ct);
        if (rc != 0 || string.IsNullOrWhiteSpace(linhas))
        {
            return null;
        }

        var candidatos = new List<(string DataId, bool ENosso)>();
        foreach (var linha in Registros(linhas))
        {
            if (!linha.Contains("account_type=com.whatsapp", StringComparison.Ordinal)
                || RowIdRx.Match(linha) is not { Success: true } raw)
            {
                continue;
            }
            var (dc, dados, _) = await _adb.ShellAsync(
                "content query --uri content://com.android.contacts/raw_contacts/"
                + $"{raw.Groups[1].Value}/data", ct);
            if (dc != 0 || string.IsNullOrWhiteSpace(dados))
            {
                continue;
            }
            foreach (var d in Registros(dados))
            {
                if (d.Contains("vnd.com.whatsapp.profile", StringComparison.Ordinal)
                    && RowIdRx.Match(d) is { Success: true } perfil)
                {
                    // O espelho carrega o número da CONTA em `data1`, como `<dígitos>@s.whatsapp.net`.
                    candidatos.Add((perfil.Groups[1].Value,
                        d.Contains($"data1={digits}@", StringComparison.Ordinal)));
                }
            }
        }

        // 🔴 QUAL ESPELHO, quando o contato agregado tem mais de um. A agregação do Android junta raw
        // contacts que ela julga ser a mesma pessoa, e o WhatsApp publica UM espelho por número que
        // reconhece. Abrir o espelho errado manda a campanha para OUTRA PESSOA, e isso não tem desfazer
        // — então a preferência é sempre o que traz o nosso número em `data1`.
        foreach (var c in candidatos)
        {
            if (c.ENosso)
            {
                return c.DataId;
            }
        }

        // 🔴 NENHUM CASOU E EXISTE UM SÓ: VALE, e este ramo não é detalhe — é a maioria dos casos. A
        // conta guarda o número na forma que tinha quando foi registrada, e ela diverge do que gravamos
        // com frequência: em 2026-08-12, num lote de 27, 24 tinham espelho e 16 DELES vinham na forma de
        // 12 dígitos, sem o 9º (ex.: contato +5541999644042, espelho 554199644042@s.whatsapp.net).
        // Exigir o casamento exato aqui devolveria "não sei" para dois terços de uma lista boa, que é o
        // falso negativo que este método existe para matar.
        //
        // De quebra, é o que resolve a segunda chance sem gastá-la: abrir pelo espelho usa um contato JÁ
        // RESOLVIDO, então a forma do 9º dígito deixa de importar no envio.
        //
        // Vários sem nenhum casando é outra coisa: aí é ambiguidade, e diante dela a saída é não
        // escolher, porque o erro abre a conversa de outra pessoa.
        return candidatos.Count == 1 ? candidatos[0].DataId : null;
    }

    /// <summary>contact_id do primeiro resultado do phone_lookup, ou null.</summary>
    /// <remarks>
    /// 🔴 O comentário antigo dizia, como fato medido, que o phone_lookup casa STRING e que "+55…" NÃO é
    /// achado por "55…". É FALSO neste aparelho. Medido em 2026-08-06 no Galaxy A14 (SM_A145M,
    /// Android 15), com um contato gravado como "+55DDD9XXXXXXXX" e contact_id conhecido:
    /// <code>
    /// adb shell content query --uri content://com.android.contacts/phone_lookup/&lt;valor&gt; --projection contact_id
    ///   %2B + digitos  -> ACHOU (mesmo id)
    ///   digitos crus   -> ACHOU (mesmo id)   &lt;- o comentário antigo dizia que não
    ///   últimos 7      -> não achou          &lt;- logo, NÃO é min-match
    ///   forma irmã (com/sem o 9º dígito) -> não achou
    /// </code>
    /// A explicação que cobre os quatro resultados: a comparação é sobre a SEQUÊNCIA DE DÍGITOS, com o
    /// "+" e a formatação normalizados fora dos dois lados.
    ///
    /// <para>Consequência: perguntar nas duas formas é redundante AQUI, mas segue sendo feito por quem
    /// chama, porque a semântica é do provider e pode mudar com versão de Android. O que NÃO pode é
    /// alguém decidir a forma do registro a partir desta função — para isso existe
    /// <see cref="TemFormaE164Async"/>, que olha o valor gravado e não depende de como o provider
    /// compara.</para>
    ///
    /// <para>⚠️ Se for medir de novo, escreva aqui a DATA, o APARELHO e o COMANDO. Foi a ausência dos
    /// três que deixou uma afirmação errada de pé, com cara de evidência, até alguém conferir.</para>
    /// </remarks>
    private async Task<string?> LookupContactIdAsync(string lookupValue, CancellationToken ct)
    {
        var (rc, outp, _) = await _adb.ShellAsync($"content query --uri content://com.android.contacts/phone_lookup/{lookupValue} --projection contact_id", ct);
        if (rc != 0)
        {
            return null;
        }
        var m = ContactIdRx.Match(outp ?? "");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>As linhas de dados do contato agregado. null = não deu pra ler.</summary>
    /// <remarks>
    /// 🔴 SEGUE LENDO O CONTATO AGREGADO DE PROPÓSITO, e não é descuido depois do que se descobriu em
    /// <see cref="PerfilAsync"/>. Quem usa isto é o <see cref="TemFormaE164Async"/>, que pergunta "COMO
    /// o número está gravado?" — e a resposta só vale sobre o registro que NÓS criamos. O espelho do
    /// WhatsApp traz um `phone_v2` próprio, já em E.164 com o "+" (medido em 2026-08-12, raw contact 907
    /// do contato 845). Juntar os raw contacts aqui faria um registro envenenado, gravado só com dígitos
    /// crus, parecer saudável — e a cura do <see cref="SaveContactAsync"/> nunca rodaria.
    ///
    /// <para>Ou seja: o filtro da One UI que quebrava a leitura do espelho é justamente o que mantém
    /// esta pergunta honesta. Depender disso seria frágil, então a garantia real é o escopo — esta
    /// função responde sobre o telefone visível do contato, e o espelho é assunto do
    /// <see cref="PerfilAsync"/>.</para>
    /// </remarks>
    private async Task<string?> ContactDataAsync(string contactId, CancellationToken ct)
    {
        var (rc, dados, _) = await _adb.ShellAsync(
            $"content query --uri content://com.android.contacts/contacts/{contactId}/data", ct);
        return rc == 0 && !string.IsNullOrWhiteSpace(dados) ? dados : null;
    }

    /// <summary>Este contato está gravado na forma E.164, com o "+"? null = não deu pra ler.</summary>
    /// <remarks>
    /// 🔴 Existe porque o <see cref="LookupContactIdAsync"/> NÃO consegue responder isto. Ele compara
    /// sequências de dígitos, então perguntar por "%2B…" e por "…" devolve a mesma coisa: o par de
    /// chamadas que o SaveContactAsync fazia eram DUAS VEZES A MESMA PERGUNTA, e a segunda nunca podia
    /// dar um resultado diferente da primeira. Na prática o ramo da cura era inalcançável, e um registro
    /// envenenado (só dígitos crus, invisível pro WhatsApp) fazia o método responder "já existe" e nunca
    /// criar a forma boa.
    ///
    /// <para>A pergunta certa não é "o provider acha este número?", é "COMO ele está gravado?". Essa só
    /// o valor armazenado responde, e ele não depende de como o provider compara — o que também torna
    /// isto imune a mudar de versão de Android.</para>
    ///
    /// <para>Busca a string literal em vez de parsear as colunas: o valor gravado aparece cru nas linhas
    /// de dados (conferido no A14 em 2026-08-06), e um contains é robusto à ordem e ao conjunto de
    /// colunas que o provider resolve devolver.</para>
    /// </remarks>
    private async Task<bool?> TemFormaE164Async(string contactId, string digits, CancellationToken ct) =>
        await ContactDataAsync(contactId, ct) is { } dados
            ? dados.Contains("+" + digits, StringComparison.Ordinal)
            : null;

    /// <summary>URI da linha de perfil do WhatsApp deste contato na agenda, pra abrir a conversa SEM
    /// passar pela resolução de número. null = não achei.</summary>
    /// <remarks>
    /// 🔴 POR QUE ISTO EXISTE. Todo envio hoje abre a conversa por `whatsapp://send?phone=…`, que exige
    /// o app RESOLVER o número no servidor. Esse caminho falha em dois casos medidos: quando a conta
    /// guarda o número na outra forma do 9º dígito, e quando a conta está restringida. Nos dois o app
    /// responde "este número não tem WhatsApp" para gente que tem.
    ///
    /// <para>A linha `vnd.com.whatsapp.profile` é o registro que o PRÓPRIO app publica na agenda para
    /// quem ele já reconheceu como usuário — é o que a lista do botão "+" mostra. Abrir por ela usa um
    /// contato JÁ RESOLVIDO, então não há o que resolver e não há como o app negar.</para>
    ///
    /// <para>É o mesmo que uma pessoa faz à mão: abrir o WhatsApp, tocar no "+", achar o contato e
    /// abrir. Só que num comando, sem navegar por telas que mudam de layout a cada versão.</para>
    /// </remarks>
    public async Task<string?> WhatsAppChatUriAsync(string phoneE164, CancellationToken ct)
    {
        var digits = DigitsOf(phoneE164);
        if (digits.Length < 8)
        {
            return null;
        }
        return await PerfilAsync(digits, ct) is { } dataId
            ? $"content://com.android.contacts/data/{dataId}"
            : null;
    }

    /// <summary>A saída do `content query` partida em REGISTROS, e não em linhas.</summary>
    /// <remarks>
    /// 🔴 UM REGISTRO OCUPA MAIS DE UMA LINHA. O `hash_id` das linhas de dados vem com uma QUEBRA DENTRO
    /// do valor, e o `_id` do registro cai depois dela. Medido em 2026-08-12 no Galaxy A14 (SM_A145M,
    /// Android 15):
    /// <code>
    /// adb shell content query --uri content://com.android.contacts/raw_contacts/907/data
    ///   -> 10 linhas para 5 registros; a linha que traz o `mimetype` NÃO traz o `_id`
    /// </code>
    /// Partir por '\n' encontrava o espelho e o descartava em seguida, por "não achei o id" — um falso
    /// negativo silencioso, do mesmo feitio do bug que este arquivo acabou de corrigir. O delimitador de
    /// verdade é o `Row: N` no começo da linha.
    /// </remarks>
    private static IEnumerable<string> Registros(string saida) =>
        RegistroRx.Split(saida).Where(r => r.Length > 0);

    private static readonly Regex RegistroRx =
        new(@"(?m)^Row:\s*\d+\s", RegexOptions.Compiled);

    /// <summary><c>_id</c> de um registro do `content query`, ancorado.</summary>
    /// <remarks>
    /// 🔴 A ÂNCORA NÃO É ENFEITE. A mesma linha traz `raw_contact_id=45` nas linhas de dados e
    /// `contact_id=845` nas de raw_contacts, e um padrão solto casaria o "_id=" DE DENTRO deles. Abrir
    /// pelo id errado abre outra coisa, ou nada.
    /// </remarks>
    private static readonly Regex RowIdRx =
        new(@"(?:^|[,\s])_id=(\d+)", RegexOptions.Compiled);

    /// <summary>Marca estável da conta do WhatsApp registrada. null = não deu pra saber.</summary>
    /// <remarks>
    /// 🔴 O QUE ISTO LÊ, e por que não precisa de root. O WhatsApp mantém um sync adapter que publica os
    /// contatos que ele reconhece dentro de uma conta própria do Android (<c>account_type</c>
    /// <c>com.whatsapp</c>), e o <c>account_name</c> dessas linhas identifica a conta registrada. É a
    /// MESMA tabela e o MESMO mecanismo que o <see cref="SaveContactAsync"/> já usa, com a identidade do
    /// shell — medido neste aparelho em 2026-07-29. A alternativa seria ler as shared_prefs do app, que é
    /// o que o engine do emulador faz e que num aparelho sem root não dá.
    ///
    /// <para>🔴 DEVOLVE O VALOR CRU DE PROPÓSITO, sem tentar extrair "o número". Quem chama só compara
    /// com o que viu da última vez. Interpretar o formato seria apostar em como o app nomeia a conta, e
    /// errar isso silenciosamente confundiria conta trocada com conta igual, que é exatamente o dano que
    /// esta função existe pra evitar. Como identificador, qualquer string estável serve.</para>
    ///
    /// <para>Registrar outra conta faz o app recriar essas linhas sob o novo nome, e é isso que torna a
    /// mudança observável daqui.</para>
    /// </remarks>
    public async Task<string?> IdentidadeDaContaAsync(CancellationToken ct)
    {
        var (rc, saida, _) = await _adb.ShellAsync(
            "content query --uri content://com.android.contacts/raw_contacts "
            + "--projection account_name --where \"account_type='com.whatsapp'\"", ct);
        if (rc != 0 || string.IsNullOrWhiteSpace(saida))
        {
            return null;
        }
        // Uma linha por contato espelhado, todas com o mesmo account_name: basta a primeira. Vazio não
        // vira identidade — seria tratar "o app não publicou nada" como se fosse uma conta, e aí um
        // aparelho mudo pareceria uma troca a cada lote.
        var m = AccountNameRx.Match(saida);
        var nome = m.Success ? m.Groups[1].Value.Trim() : "";
        return nome.Length == 0 ? null : nome;
    }

    private static readonly Regex AccountNameRx =
        new(@"account_name=([^,\r\n]+)", RegexOptions.Compiled);

    /// <summary>O número tem WhatsApp, segundo o próprio aparelho?</summary>
    /// <returns>true = o app publicou o espelho (é usuário). null = NÃO SEI. NUNCA devolve false.</returns>
    /// <remarks>
    /// 🔴 O `false` está deliberadamente ausente. Sem o `wa.db`, "o espelho não está lá" tem pelo menos
    /// três causas indistinguíveis: o número não é usuário, o sync horário ainda não rodou, ou o espelho
    /// está num daqueles buracos de horas. Duas dessas são temporárias e a consequência do erro é
    /// TERMINAL (`MarkSkipped` no DispatchEngine). Adiar custa um ciclo; descartar custa o contato.
    ///
    /// <para>⚠️ Essa assimetria é o que tornou o bug do <see cref="PerfilAsync"/> tão caro de enxergar:
    /// uma leitura cega devolve `null` para todo mundo, e `null` é exatamente o que um aparelho honesto
    /// devolveria num dia ruim de sync. O sintoma que denuncia é a UNIFORMIDADE — negativo em 100% de um
    /// lote não é o mundo real, é uma consulta olhando para o lugar errado.</para>
    /// </remarks>
    public async Task<bool?> IsOnWhatsAppAsync(string phoneE164, CancellationToken ct)
    {
        var digits = DigitsOf(phoneE164);
        // A marca que o WhatsApp publica para quem é usuário da plataforma. Só o SIM sai daqui.
        return digits.Length >= 8 && await PerfilAsync(digits, ct) is not null ? true : null;
    }

    /// <summary>Grava o número na agenda do Android. Idempotente e best-effort.</summary>
    public async Task<string> SaveContactAsync(string phoneE164, string? name, CancellationToken ct)
    {
        var digits = DigitsOf(phoneE164);
        if (digits.Length < 8)
        {
            return "phone inválido";
        }

        // 1) Já está na agenda? Duas perguntas DIFERENTES, nesta ordem: primeiro "existe?", depois
        //    "COMO está gravado?". A segunda é a que importa, porque só a forma E.164 com o "+" é
        //    resolvível pelo WhatsApp: um contato gravado com os dígitos crus existe pro Android e é
        //    INVISÍVEL pro app, que passa a marcá-lo como sem conta de forma persistente.
        //
        // 🔴 As duas ESTAVAM sendo feitas pelo phone_lookup, com e sem o "%2B", como se a forma da
        //    consulta revelasse a forma do registro. Não revela: o provider compara sequências de
        //    dígitos (medido em 2026-08-06, ver LookupContactIdAsync). Eram duas vezes a mesma pergunta,
        //    a segunda nunca podia discordar da primeira, e a cura abaixo era inalcançável — pior, um
        //    registro envenenado respondia "já existe" e a forma boa nunca era criada.
        //
        //    As duas formas seguem sendo consultadas porque a semântica é do provider e pode variar com
        //    a versão do Android; sob comparação por string exata isto continua achando o registro.
        var contactId = await LookupContactIdAsync("%2B" + digits, ct)
            ?? await LookupContactIdAsync(digits, ct);

        var cru = false;
        if (contactId is not null)
        {
            switch (await TemFormaE164Async(contactId, digits, ct))
            {
                case true:
                    return "já existe";
                case null:
                    // Achei o contato e não consegui ver como ele está gravado. Criar às cegas duplica;
                    // dizer "já existe" pode estar escondendo um registro envenenado. Nenhuma das duas
                    // se sustenta sem o dado, então não faço nada e digo por quê.
                    return $"o número já está na agenda (contato {contactId}) mas não consegui ler em que "
                        + "forma. Não mexi em nada. Tente de novo.";
                default:
                    // 🔴 ESTÁ NA AGENDA, MAS NÃO NA FORMA QUE O WHATSAPP RESOLVE. Registro envenenado,
                    //    de antes da correção do E.164. Seguimos e criamos a forma correta AO LADO.
                    //    Sim, ficam duas entradas para o mesmo telefone; a antiga o app já não enxergava,
                    //    então não é duplicata do ponto de vista dele. Curar sozinho vale o registro
                    //    extra, e a alternativa seria apagar contato a contato na mão.
                    cru = true;
                    break;
            }
        }

        // 2) O MAIOR _id ANTES de inserir. Ver o passo 3: é ele que transforma "o último deve ser o
        //    nosso" numa afirmação conferível.
        var (ac, antesRows, ae) = await _adb.ShellAsync("content query --uri content://com.android.contacts/raw_contacts --projection _id", ct);
        if (ac != 0)
        {
            return $"não consegui ler a agenda: {Detail(antesRows, ae)}";
        }
        var antes = MaxRawContactId(antesRows);

        // 3) Cria o raw contact. Conta VAZIA: num aparelho COM conta Google o Android atribui à conta
        //    padrão sozinho e o contato sincroniza.
        var (ic, io, ie) = await _adb.ShellAsync("content insert --uri content://com.android.contacts/raw_contacts "
            + "--bind account_type:s: --bind account_name:s:", ct);
        if (ic != 0)
        {
            return $"não criei o contato: {Detail(io, ie)}";
        }

        // 4) Qual _id é o nosso. O `content insert` não devolve o id do que criou, então só resta ler
        //    a agenda de novo e deduzir — e a dedução PRECISA ser conferida.
        //
        // 🔴 "O disparo é SEQUENCIAL, então o maior _id é o nosso" era a regra antiga, herdada do
        // orquestrador do emulador. Lá ela vale: container isolado, sem conta Google. AQUI NÃO. O
        // aparelho tem outros escritores em raw_contacts, e o principal é o PRÓPRIO WhatsApp: o
        // espelho que IsOnWhatsAppAsync lê (vnd.com.whatsapp.profile) mora em raw contacts da conta
        // com.whatsapp, criados pelo sync adapter dele. A conta Google faz o mesmo. Ou seja, GRAVAR UM
        // CONTATO É O QUE ACORDA O ESCRITOR CONCORRENTE, e o `enviar` grava a cada volta do laço.
        //
        // Entre o insert e esta leitura há duas idas e voltas de adb. Uma linha inserida por eles
        // nessa janela fazia `rid` apontar pro raw contact DE OUTRA PESSOA — e os passos seguintes
        // gravavam nome e telefone do alvo em cima dela, o que sobe pra conta Google. O nosso raw
        // contact ficava órfão, o alvo NÃO entrava na agenda, o WhatsApp não achava a conta e
        // respondia "não está no WhatsApp". Exatamente o sintoma perseguido por horas em 2026-08-05,
        // com um "ok" devolvido aqui dizendo que estava tudo certo.
        //
        // O salto de _id denuncia a corrida: inserimos UMA linha, então o esperado é antes+1. Mais que
        // isso significa que alguém escreveu junto, e aí não dá pra saber qual id é o nosso. Nesse
        // caso a saída é PARAR, não escolher. Gravar é idempotente e barato de repetir; escrever no
        // contato errado não tem desfazer.
        var (qc, rows, qe) = await _adb.ShellAsync("content query --uri content://com.android.contacts/raw_contacts --projection _id", ct);
        if (qc != 0)
        {
            return $"não consegui ler a agenda: {Detail(rows, qe)}";
        }
        var rid = MaxRawContactId(rows);
        if (rid <= 0)
        {
            return "a agenda respondeu sem nenhum _id";
        }
        if (rid <= antes)
        {
            // O insert disse que deu certo e nenhum id novo apareceu. Seguir daqui gravaria em cima de
            // um contato que já existia antes de chegarmos.
            return "o contato foi criado mas a agenda não mostrou nenhum registro novo; não gravei nada "
                + "por cima. Tente de novo.";
        }
        if (rid > antes + 1)
        {
            return "a agenda mudou embaixo de mim enquanto eu criava o contato (o WhatsApp ou a conta "
                + "Google escreveram junto), então não dá pra saber qual registro é o meu. Não gravei "
                + "nada por cima. Tente de novo — gravar é idempotente.";
        }

        // 5) Nome e telefone. O nome falhar deixa contato sem nome (ainda serve); o TELEFONE falhar
        //    deixa contato ÓRFÃO, inútil e invisível — por isso os dois são verificados.
        var safeName = SanitizeContactName(name, digits);
        var (nc, no, ne) = await _adb.ShellAsync("content insert --uri content://com.android.contacts/data "
            + $"--bind raw_contact_id:i:{rid} --bind mimetype:s:vnd.android.cursor.item/name "
            + $"--bind data1:s:{safeName}", ct);
        if (nc != 0)
        {
            return $"não gravei o nome do contato {rid}: {Detail(no, ne)}";
        }
        // 🔴 GRAVA EM E.164, COM O "+". Sem ele o valor é só uma sequência de 12-13 dígitos, e o
        // Android/WhatsApp normalizam contra o país do CHIP: no Brasil o número nacional tem 10 ou 11
        // dígitos, então "558498420730" não casa com nada e a normalização produz lixo. O WhatsApp
        // sincroniza a agenda, não resolve aquele contato e o marca como SEM CONTA — de forma
        // persistente, porque a resposta fica em cache. A partir daí o deep link responde "não tem
        // WhatsApp" para uma pessoa que existe e está ativa.
        //
        // Diagnosticado em 2026-08-05: falhava com o 9º dígito E sem ele, o que descartava o formato
        // do número e apontava pra o que era COMUM a todos — a forma como foram gravados.
        //
        // O próprio LookupContactIdAsync já documentava que o phone_lookup casa STRING e não número,
        // e por isso procura nos dois formatos. Gravar no ambíguo contradizia o que a busca sabia.
        var (rc, outp, err) = await _adb.ShellAsync("content insert --uri content://com.android.contacts/data "
            + $"--bind raw_contact_id:i:{rid} --bind mimetype:s:vnd.android.cursor.item/phone_v2 "
            + $"--bind data1:s:+{digits}", ct);
        if (rc != 0)
        {
            return $"não gravei o telefone (contato {rid} ficou órfão na agenda): {Detail(outp, err)}";
        }

        // 6) 🔴 PÓS-CONDIÇÃO: a agenda RESOLVE este número agora? É a mesma pergunta do passo 1, feita
        //    depois de escrever, e é o que separa "mandei os comandos" de "o contato existe".
        //
        // Todo o resto deste método afirma sucesso por código de retorno do adb, que só diz que o
        // comando rodou — não que ele produziu o efeito pretendido. Um rc==0 gravando num raw contact
        // que não é o nosso devolvia "ok" com o alvo AUSENTE da agenda, e o erro só reaparecia lá na
        // frente, disfarçado de "este número não tem WhatsApp": a mensagem que aponta pro contato
        // quando a culpa é da gravação. Perguntar de volta custa uma chamada adb e fecha essa fenda.
        //
        // Pergunta pela forma E.164 COM o "+" porque é a única que o WhatsApp resolve — a mesma razão
        // do passo 1 e do comentário logo acima.
        if (await LookupContactIdAsync("%2B" + digits, ct) is null)
        {
            return $"gravei o contato {rid} mas a agenda não encontra +{digits} depois disso. NÃO conte "
                + "com este número no aparelho: o WhatsApp não vai enxergá-lo. Tente de novo.";
        }

        // Distingue criar de CURAR: quem opera precisa saber que o aparelho tinha um registro que o
        // WhatsApp não enxergava, porque isso explica as falhas anteriores daquele número.
        return cru ? "ok (corrigido: havia um registro sem o +, invisível pro WhatsApp)" : "ok";
    }

    // Maior _id entre as linhas do content query. Sozinho NÃO identifica o registro recém-criado: quem
    // faz isso é a comparação antes/depois no SaveContactAsync, que trata o salto inesperado como
    // corrida em vez de escolher um id no escuro.
    private static int MaxRawContactId(string rows)
    {
        var max = 0;
        foreach (Match m in Regex.Matches(rows ?? string.Empty, @"_id=(\d+)"))
        {
            if (int.TryParse(m.Groups[1].Value, out var v) && v > max)
            {
                max = v;
            }
        }
        return max;
    }

    /// <summary>Nome pronto pro shell do aparelho: só letra, dígito e espaço, e depois entre aspas.</summary>
    /// <remarks>
    /// As duas defesas são propositais. O filtro tira o que poderia virar comando (aspas, ;, $, `);
    /// as aspas preservam o ESPAÇO, que antes era descartado e transformava "Fulano de Tal" em
    /// "FulanodeTal" na agenda. Filtrar sem citar perde o nome; citar sem filtrar é injeção de shell.
    /// </remarks>
    private static string SanitizeContactName(string? name, string fallbackDigits)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ShellQuote("C" + fallbackDigits);
        }
        var clean = new string([.. name.Where(c => char.IsLetterOrDigit(c) || c == ' ')]).Trim();
        return ShellQuote(clean.Length == 0 ? "C" + fallbackDigits : clean);
    }

    /// <summary>Aspas simples pro shell DO APARELHO (o adb junta os argumentos e o shell de lá
    /// reinterpreta).</summary>
    private static string ShellQuote(string s) =>
        "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Detail(string? outp, string? err)
    {
        var raw = string.IsNullOrWhiteSpace(err) ? outp : err;
        var flat = (raw ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length switch
        {
            0 => "(sem detalhe)",
            > 200 => flat[..200] + "…",
            _ => flat,
        };
    }
}
