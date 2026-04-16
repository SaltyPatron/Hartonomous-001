namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Parses UCA allkeys.txt to extract collation weights per codepoint.
/// Format: codepoint(s) ; [.primary.secondary.tertiary] # comment
/// </summary>
internal static class UcaParser
{
    public static Dictionary<int, CollationWeight> ParseAllKeys(string path)
    {
        Dictionary<int, CollationWeight> result = new(64000);

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#' || line[0] == '@')
            {
                continue;
            }

            int semi = line.IndexOf(';');
            if (semi < 0)
            {
                continue;
            }

            ReadOnlySpan<char> cpPart = line.AsSpan(0, semi).Trim();
            ReadOnlySpan<char> weightPart = line.AsSpan(semi + 1).Trim();

            if (cpPart.Contains(' '))
            {
                continue;
            }

            if (!TryParseHex(cpPart, out int cp))
            {
                continue;
            }

            int openBracket = weightPart.IndexOf('[');
            if (openBracket < 0)
            {
                continue;
            }

            ReadOnlySpan<char> ce = weightPart.Slice(openBracket);
            if (!TryParseCollationElement(ce, out ushort primary, out ushort secondary, out ushort tertiary))
            {
                continue;
            }

            result.TryAdd(cp, new CollationWeight(primary, secondary, tertiary));
        }

        return result;
    }

    private static bool TryParseHex(ReadOnlySpan<char> s, out int value)
    {
        value = 0;
        foreach (char c in s)
        {
            int digit;
            if (c >= '0' && c <= '9')
            {
                digit = c - '0';
            }
            else if (c >= 'A' && c <= 'F')
            {
                digit = c - 'A' + 10;
            }
            else if (c >= 'a' && c <= 'f')
            {
                digit = c - 'a' + 10;
            }
            else
            {
                return false;
            }

            value = (value << 4) | digit;
        }
        return s.Length > 0;
    }

    private static bool TryParseCollationElement(
        ReadOnlySpan<char> ce,
        out ushort primary, out ushort secondary, out ushort tertiary)
    {
        primary = secondary = tertiary = 0;

        if (ce.Length < 2 || ce[0] != '[')
        {
            return false;
        }

        int pos = 1;
        if (pos < ce.Length && (ce[pos] == '.' || ce[pos] == '*'))
        {
            pos++;
        }

        int dot1 = ce.Slice(pos).IndexOf('.');
        if (dot1 < 0)
        {
            return false;
        }

        if (!TryParseHex(ce.Slice(pos, dot1), out int p))
        {
            return false;
        }

        primary = (ushort)p;
        pos += dot1 + 1;

        int dot2 = ce.Slice(pos).IndexOf('.');
        if (dot2 < 0)
        {
            return false;
        }

        if (!TryParseHex(ce.Slice(pos, dot2), out int s))
        {
            return false;
        }

        secondary = (ushort)s;
        pos += dot2 + 1;

        int bracket = ce.Slice(pos).IndexOf(']');
        if (bracket < 0)
        {
            return false;
        }

        if (!TryParseHex(ce.Slice(pos, bracket), out int t))
        {
            return false;
        }

        tertiary = (ushort)t;
        return true;
    }
}
