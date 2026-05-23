using System.Text;
using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Core.Messaging;

public sealed class SpintaxExpander(IRandomSource rng)
{
    private const int MaxDepth = 8;
    private const int MaxOutput = 4096;

    public string Expand(string template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }
        var sb = new StringBuilder(Math.Min(template.Length * 2, MaxOutput));
        ExpandInto(template.AsMemory(), sb, depth: 0);
        return sb.Length > MaxOutput ? sb.ToString(0, MaxOutput) : sb.ToString();
    }

    private void ExpandInto(ReadOnlyMemory<char> input, StringBuilder output, int depth)
    {
        if (depth > MaxDepth)
        {
            output.Append(input.Span);
            return;
        }
        var span = input.Span;
        var i = 0;
        while (i < span.Length && output.Length < MaxOutput)
        {
            var c = span[i];
            if (c == '\\' && i + 1 < span.Length && (span[i + 1] == '{' || span[i + 1] == '}' || span[i + 1] == '|'))
            {
                output.Append(span[i + 1]);
                i += 2;
                continue;
            }
            if (c == '{')
            {
                if (i + 1 < span.Length && span[i + 1] == '{')
                {
                    var placeholderEnd = FindPlaceholderEnd(span, i + 2);
                    if (placeholderEnd > 0)
                    {
                        output.Append(span[i..(placeholderEnd + 2)]);
                        i = placeholderEnd + 2;
                        continue;
                    }
                }
                var end = FindMatchingBrace(span, i);
                if (end < 0)
                {
                    output.Append(span[i..]);
                    return;
                }
                var inner = input.Slice(i + 1, end - i - 1);
                var options = SplitTopLevel(inner);
                var chosen = options[rng.NextInt(0, options.Count)];
                ExpandInto(chosen, output, depth + 1);
                i = end + 1;
                continue;
            }
            output.Append(c);
            i++;
        }
    }

    private static int FindPlaceholderEnd(ReadOnlySpan<char> input, int startIndex)
    {
        for (var i = startIndex; i < input.Length - 1; i++)
        {
            if (input[i] == '}' && input[i + 1] == '}')
            {
                return i;
            }
        }
        return -1;
    }

    private static int FindMatchingBrace(ReadOnlySpan<char> input, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '\\' && i + 1 < input.Length)
            {
                i++;
                continue;
            }
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static List<ReadOnlyMemory<char>> SplitTopLevel(ReadOnlyMemory<char> input)
    {
        var result = new List<ReadOnlyMemory<char>>();
        var span = input.Span;
        var start = 0;
        var depth = 0;
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (c == '\\' && i + 1 < span.Length)
            {
                i++;
                continue;
            }
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
            }
            else if (c == '|' && depth == 0)
            {
                result.Add(input.Slice(start, i - start));
                start = i + 1;
            }
        }
        result.Add(input[start..]);
        return result;
    }
}
