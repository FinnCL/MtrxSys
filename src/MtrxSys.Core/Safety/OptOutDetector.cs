using System.Globalization;
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

    // NÃO inclua "para": é preposição comuníssima em PT ("é para hoje?", "para qual cidade",
    // "manda para mim") e, numa resposta curta, dispararia opt-out falso — suprimindo um lead
    // interessado de forma global e irreversível (ledger nos 10 ambientes). O imperativo "pare"
    // existir só como "parar"/"pare"; o uso real ("para de mandar") já cai nas Phrases abaixo.
    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
    {
        "sair", "saia", "parar", "pare", "stop", "cancelar", "cancela",
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

    // "Bloqueadores" numa resposta curta: se aparecem junto do comando, ele NÃO é opt-out.
    //  • Objetos concretos ("pare o AUDIO", "sair do GRUPO", "cancela a FOTO"): o comando se refere
    //    ao objeto, não ao descadastro.
    //  • Negação ("NAO quero sair", "NUNCA vou parar"): inverte o sentido. Opt-out negado real
    //    ("nao quero mais", "nao quero receber") já é pego pelas Phrases antes de chegar aqui.
    // Fora esses casos, favorecemos DETECTAR o opt-out (é sagrado; perder um gera spam/ban/LGPD) —
    // "pode cancelar tudo", "cancela minha inscricao", "pare tudo" continuam disparando.
    private static readonly HashSet<string> ShortReplyBlockers = new(StringComparer.Ordinal)
    {
        // objetos concretos
        "audio", "audios", "video", "videos", "foto", "fotos", "imagem", "imagens",
        "figurinha", "figurinhas", "grupo", "grupos", "musica", "som", "ligacao",
        "chamada", "arquivo", "link", "pdf",
        // negação
        "nao", "nunca", "jamais", "nem",
    };

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

        // Comando numa resposta curta (até 3 palavras): "SAIR", "sair por favor", "quero parar",
        // "pode cancelar", "cancela minha inscricao", "SAIR 🙏". Dispara o opt-out A MENOS QUE apareça
        // um bloqueador (objeto concreto ou negação), que muda o sentido do comando — "pare o audio",
        // "sair do grupo", "nao quero sair". Em mensagens longas (>3 palavras) a palavra solta não
        // dispara: só as Phrases explícitas acima. Favorece detectar (opt-out é sagrado), sem os
        // falsos-positivos claros de objeto/negação.
        var words = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= ShortReplyMaxWords)
        {
            var hasCommand = false;
            var hasBlocker = false;
            foreach (var w in words)
            {
                if (Commands.Contains(w))
                {
                    hasCommand = true;
                }
                else if (ShortReplyBlockers.Contains(w))
                {
                    hasBlocker = true;
                }
            }
            if (hasCommand && !hasBlocker)
            {
                return true;
            }
        }

        return false;
    }

    // Minúsculas, sem acento, e qualquer coisa que não seja letra/número vira espaço
    // (pontuação, emoji etc.). Espaços colapsados — facilita comparar palavras e frases.
    // O mapa de acentos é explícito de propósito: o projeto roda com InvariantGlobalization=true
    // (Directory.Build.props), onde string.Normalize(FormD) é no-op (ICU não é carregado) e NÃO
    // removeria diacrítico nenhum — "não" continuaria "não" e deixaria de casar com "nao". Por isso
    // não dá pra decompor via NFD aqui; estende o mapa à mão se aparecer um diacrítico novo.
    private static string Normalize(string s)
    {
        var lowered = s.ToLowerInvariant();
        var sb = new StringBuilder(lowered.Length);
        foreach (var ch in lowered)
        {
            // Marca combinante (acento já separado da letra, entrada em NFD): descarta, pra
            // "a" + til virar "a" e não "a "/espaço. GetUnicodeCategory usa tabela embutida no
            // runtime (não ICU), então classifica certo mesmo com InvariantGlobalization.
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            var c = ch switch
            {
                'á' or 'à' or 'ã' or 'â' or 'ä' => 'a',
                'é' or 'è' or 'ê' or 'ë' => 'e',
                'í' or 'ì' or 'î' or 'ï' => 'i',
                'ó' or 'ò' or 'õ' or 'ô' or 'ö' => 'o',
                'ú' or 'ù' or 'û' or 'ü' => 'u',
                'ç' => 'c',
                'ñ' => 'n',
                _ => ch,
            };
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
