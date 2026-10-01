using System.Text;

namespace DiskSweeper.Core.SmartFinds;

/// <summary>
/// Reader for Valve's KeyValues text format (Steam's .vdf and .acf files):
/// <c>"key" "value"</c> pairs and <c>"key" { ... }</c> blocks, with // comments.
/// </summary>
public sealed class Vdf
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Vdf> _blocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _blockOrder = [];

    public string? this[string key] => _values.TryGetValue(key, out string? value) ? value : null;

    public Vdf? Block(string key) => _blocks.TryGetValue(key, out var block) ? block : null;

    public IEnumerable<(string Key, Vdf Block)> Blocks => _blockOrder.Select(key => (key, _blocks[key]));

    public static Vdf Parse(string text)
    {
        int position = 0;
        var root = new Vdf();
        ParseInto(root, text, ref position, expectClose: false);
        return root;
    }

    public static Vdf? TryLoad(string file)
    {
        try
        {
            return Parse(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static void ParseInto(Vdf target, string text, ref int position, bool expectClose)
    {
        while (true)
        {
            string? token = NextToken(text, ref position, out bool quoted);
            if (token is null)
            {
                if (expectClose) throw new FormatException("Missing '}' at end of file.");
                return;
            }
            if (!quoted && token == "}")
            {
                if (!expectClose) throw new FormatException("Unexpected '}'.");
                return;
            }

            string key = token;
            string? next = NextToken(text, ref position, out bool nextQuoted);
            if (next is null) throw new FormatException($"Key '{key}' has no value.");

            if (!nextQuoted && next == "{")
            {
                var block = new Vdf();
                ParseInto(block, text, ref position, expectClose: true);
                if (target._blocks.TryAdd(key, block)) target._blockOrder.Add(key);
            }
            else
            {
                target._values[key] = next;
            }
        }
    }

    private static string? NextToken(string text, ref int position, out bool quoted)
    {
        quoted = false;
        while (position < text.Length)
        {
            char c = text[position];
            if (char.IsWhiteSpace(c))
            {
                position++;
            }
            else if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n') position++;
            }
            else
            {
                break;
            }
        }
        if (position >= text.Length) return null;

        char first = text[position];
        if (first is '{' or '}')
        {
            position++;
            return first.ToString();
        }

        var token = new StringBuilder();
        if (first == '"')
        {
            quoted = true;
            position++;
            while (position < text.Length && text[position] != '"')
            {
                char c = text[position++];
                if (c == '\\' && position < text.Length)
                {
                    char escaped = text[position++];
                    token.Append(escaped switch { 'n' => '\n', 't' => '\t', _ => escaped });
                }
                else
                {
                    token.Append(c);
                }
            }
            if (position >= text.Length) throw new FormatException("Unterminated string.");
            position++;
            return token.ToString();
        }

        while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}' or '"'))
            token.Append(text[position++]);
        return token.ToString();
    }
}
