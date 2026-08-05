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

    /// <summary>contact_id do primeiro resultado do phone_lookup, ou null.</summary>
    /// <remarks>O phone_lookup casa STRING, não número: um contato gravado como "+55…" NÃO é achado por
    /// "55…" e vice-versa (medido). Por isso quem chama tenta os dois formatos.</remarks>
    private async Task<string?> LookupContactIdAsync(string lookupValue, CancellationToken ct)
    {
        var (rc, outp, _) = await _adb.ShellAsync($"content query --uri content://com.android.contacts/phone_lookup/{lookupValue} --projection contact_id", ct);
        if (rc != 0)
        {
            return null;
        }
        var m = Regex.Match(outp ?? "", @"contact_id=(\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>O número tem WhatsApp, segundo o próprio aparelho?</summary>
    /// <returns>true = o app publicou o espelho (é usuário). null = NÃO SEI. NUNCA devolve false.</returns>
    /// <remarks>
    /// 🔴 O `false` está deliberadamente ausente. Sem o `wa.db`, "o espelho não está lá" tem pelo menos
    /// três causas indistinguíveis: o número não é usuário, o sync horário ainda não rodou, ou o espelho
    /// está num daqueles buracos de horas. Duas dessas são temporárias e a consequência do erro é
    /// TERMINAL (`MarkSkipped` no DispatchEngine). Adiar custa um ciclo; descartar custa o contato.
    /// </remarks>
    public async Task<bool?> IsOnWhatsAppAsync(string phoneE164, CancellationToken ct)
    {
        var digits = DigitsOf(phoneE164);
        if (digits.Length < 8)
        {
            return null;
        }
        var contactId = await LookupContactIdAsync(digits, ct)
            ?? await LookupContactIdAsync("%2B" + digits, ct);
        if (contactId is null)
        {
            return null; // nem está na agenda: quem chama salva e re-pergunta depois do sync
        }
        var (rc, data, _) = await _adb.ShellAsync($"content query --uri content://com.android.contacts/contacts/{contactId}/data", ct);
        if (rc != 0)
        {
            return null;
        }
        // A marca que o WhatsApp cria para quem é usuário da plataforma. Só o SIM sai daqui.
        return data.Contains("vnd.com.whatsapp.profile", StringComparison.Ordinal) ? true : null;
    }

    /// <summary>Grava o número na agenda do Android. Idempotente e best-effort.</summary>
    public async Task<string> SaveContactAsync(string phoneE164, string? name, CancellationToken ct)
    {
        var digits = DigitsOf(phoneE164);
        if (digits.Length < 8)
        {
            return "phone inválido";
        }

        // 1) Já está na agenda? A pergunta que vale é sobre a forma E.164, COM o "+": é a única que o
        //    WhatsApp consegue resolver. Um contato gravado só com os dígitos crus existe pro Android
        //    e é INVISÍVEL pro WhatsApp, que o marca como sem conta.
        var comMais = await LookupContactIdAsync("%2B" + digits, ct) is not null;
        if (comMais)
        {
            return "já existe";
        }

        // 🔴 SÓ A FORMA CRUA EXISTE = REGISTRO ENVENENADO, de antes desta correção. Devolver
        //    "já existe" aqui deixaria o aparelho permanentemente incapaz de alcançar essa pessoa, e
        //    exigiria apagar contato a contato na mão. Seguimos e criamos a forma correta ao lado.
        //    Sim, ficam duas entradas para o mesmo telefone; a antiga o WhatsApp já não enxergava, então
        //    não é duplicata do ponto de vista dele. Curar sozinho vale o registro extra.
        var cru = await LookupContactIdAsync(digits, ct) is not null;

        // 2) Cria o raw contact. Conta VAZIA: num aparelho COM conta Google o Android atribui à conta
        //    padrão sozinho e o contato sincroniza.
        var (ic, io, ie) = await _adb.ShellAsync("content insert --uri content://com.android.contacts/raw_contacts "
            + "--bind account_type:s: --bind account_name:s:", ct);
        if (ic != 0)
        {
            return $"não criei o contato: {Detail(io, ie)}";
        }

        // 3) Pega o _id recém-criado. O disparo é SEQUENCIAL, então o MAIOR _id é o nosso.
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

        // 4) Nome e telefone. O nome falhar deixa contato sem nome (ainda serve); o TELEFONE falhar
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
        // Distingue criar de CURAR: quem opera precisa saber que o aparelho tinha um registro que o
        // WhatsApp não enxergava, porque isso explica as falhas anteriores daquele número.
        return cru ? "ok (corrigido: havia um registro sem o +, invisível pro WhatsApp)" : "ok";
    }

    // Maior _id entre as linhas do content query — o raw contact recém-inserido.
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
