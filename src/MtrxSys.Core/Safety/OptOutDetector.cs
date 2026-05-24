using System.Text;

namespace MtrxSys.Core.Safety;

/// <summary>
/// Detecta pedidos de descadastro em mensagens recebidas. Normaliza a entrada
/// (minúsculas, sem acento, sem pontuação) e reconhece tanto o comando isolado
/// numa resposta curta ("SAIR", "sair por favor", "quero parar") quanto frases
/// explícitas em textos maiores ("não quero receber mais mensagens").
/// É conservador: em frases longas a palavra solta não dispara, pra evitar
/// falso positivo (ex.: "vou sair mais tarde de casa hoje").
/// </summary>
public static class OptOutDetector
{
    // Limite de palavras pra tratar uma resposta como "comando" de saída.
    private const int ShortReplyMaxWords = 3;

    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
    {
        "sair", "saia", "parar", "pare", "para", "stop", "cancelar", "cancela",
        "descadastrar", "remover", "remova", "remove",
    };

    // Frase explícita em qualquer parte do texto (mesmo em mensagens longas).
    private static readonly string[] Phrases =
    [
        "para de mandar", "parar de receber", "parar de mandar", "pare de enviar", "pare de mandar",
        "nao quero receber", "nao quero mais", "nao envie", "nao manda", "nao me manda",
        "me descadastr", "me remov", "me tira", "tirar da lista", "sair da lista",
        "remover meu", "cancelar inscricao", "sem mensagens", "para de enviar",
    ];

    public static bool IsOptOut(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var norm = Normalize(body);
        if (norm.Length == 0)
        {
            return false;
        }

        foreach (var phrase in Phrases)
        {
            if (norm.Contains(phrase, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // Comando isolado numa resposta curta (até 3 palavras): "SAIR", "sair por favor",
        // "quero parar", "SAIR 🙏". Em mensagens longas não dispara pela palavra solta.
        var words = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= ShortReplyMaxWords)
        {
            foreach (var w in words)
            {
                if (Commands.Contains(w))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Minúsculas, sem acento, e qualquer coisa que não seja letra/número vira espaço
    // (pontuação, emoji etc.). Espaços colapsados — facilita comparar palavras e frases.
    private static string Normalize(string s)
    {
        var lowered = s.ToLowerInvariant();
        var sb = new StringBuilder(lowered.Length);
        foreach (var ch in lowered)
        {
            var c = ch switch
            {
                'á' or 'à' or 'ã' or 'â' or 'ä' => 'a',
                'é' or 'è' or 'ê' or 'ë' => 'e',
                'í' or 'ì' or 'î' or 'ï' => 'i',
                'ó' or 'ò' or 'õ' or 'ô' or 'ö' => 'o',
                'ú' or 'ù' or 'û' or 'ü' => 'u',
                'ç' => 'c',
                _ => ch,
            };
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
