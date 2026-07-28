namespace MtrxSys.Core.Validation;

/// <summary>
/// A identidade de um telefone neste sistema: só os dígitos.
/// </summary>
/// <remarks>
/// <para>🔴 POR QUE ISTO EXISTE. Comparar telefone por TEXTO trata "+5588…" e "5588…" como pessoas
/// diferentes. Em 2026-07-27 isso gerou 50 contatos duplicados na produção — o mesmo grupo importado
/// por dois caminhos, cada um gravando num formato — e 50 jobs a mais na fila. O índice único do banco
/// (<c>IX_contacts_phone_digits</c>) usa exatamente esta forma; o código precisa concordar com ele.</para>
///
/// <para>Não normaliza nem valida: só extrai. Quem decide se o número é plausível é o
/// <see cref="BrazilPhoneValidator"/>; quem decide o formato de gravação é quem grava. Aqui é uma
/// pergunta só, "quais são os dígitos", e a resposta tem que ser a mesma em todo lugar.</para>
///
/// <para>Mantém <c>char.IsDigit</c> (e não só ASCII) por compatibilidade com as cópias que existiam
/// espalhadas — trocar o critério aqui mudaria silenciosamente o comportamento de quem já dependia
/// delas. Onde a diferença importa, que é interpolação em SQL, quem valida é o chamador.</para>
/// </remarks>
public static class PhoneDigits
{
    /// <summary>Só os dígitos de <paramref name="raw"/>. Entrada nula ou vazia devolve string vazia.</summary>
    public static string Of(string? raw) =>
        string.IsNullOrEmpty(raw) ? string.Empty : new string([.. raw.Where(char.IsDigit)]);

    /// <summary>Os dois telefones são o MESMO número, ignorando formato?</summary>
    public static bool Same(string? a, string? b)
    {
        var da = Of(a);
        return da.Length > 0 && string.Equals(da, Of(b), StringComparison.Ordinal);
    }
}
