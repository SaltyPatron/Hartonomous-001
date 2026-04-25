using System.Globalization;
using System.Text;

namespace Hartonomous.Core.Text.Segmentation;

/// <summary>
/// UAX #29 extended grapheme cluster segmentation. The primitive walks UTF-8
/// input once and applies the UAX #29 break rules using codepoint properties
/// sourced from the substrate (<see cref="ICodepointProperties"/>). All rules
/// implemented: GB1–GB9b, GB11 (emoji ZWJ sequences), GB12/GB13, GB999.
/// </summary>
public static class GraphemeClusters
{
    /// <summary>
    /// .NET-backed grapheme cluster enumeration via
    /// <see cref="StringInfo.GetTextElementEnumerator(string)"/>. This delegates
    /// to Microsoft's UAX #29 implementation (currently UAX #29 v15.1 in .NET 9
    /// targeting Unicode 16). Use this path when conformance to the official
    /// UCD <c>GraphemeBreakTest.txt</c> is required.
    /// <para>
    /// Why this exists: the hand-rolled <see cref="Enumerate"/> below fails 425
    /// of 766 UCD test cases (44.5% conformance, see
    /// <c>UcdConformanceTests.GraphemeClusters_Conform_To_UCD_Test_File</c>).
    /// Until those bugs are tracked down, every text-decomposition path that
    /// requires correct grapheme boundaries on non-ASCII content (combining
    /// marks, Devanagari conjuncts, complex emoji ZWJ) MUST use this method.
    /// </para>
    /// </summary>
    public static List<GraphemeRange> EnumerateUsingNet(ReadOnlySpan<byte> utf8)
    {
        List<GraphemeRange> result = new();
        if (utf8.IsEmpty)
        {
            return result;
        }

        string s = Encoding.UTF8.GetString(utf8);
        // Build per-char-index byte offset map so we can translate StringInfo's
        // char indices back to UTF-8 byte offsets without rescanning. Surrogate
        // pairs (one supplementary codepoint = 2 chars) share a single 4-byte
        // UTF-8 sequence; both char indices map to the same byte offset.
        int[] charToByte = new int[s.Length + 1];
        int byteCursor = 0;
        int i = 0;
        while (i < s.Length)
        {
            charToByte[i] = byteCursor;
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                charToByte[i + 1] = byteCursor;
                byteCursor += 4;
                i += 2;
                continue;
            }
            if (c <= 0x7F)
            {
                byteCursor += 1;
            }
            else if (c <= 0x7FF)
            {
                byteCursor += 2;
            }
            else
            {
                byteCursor += 3;
            }
            i++;
        }
        charToByte[s.Length] = byteCursor;

        long cpOffset = 0;
        TextElementEnumerator e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
        {
            int charIdx = e.ElementIndex;
            string te = (string)e.Current;
            int byteOffset = charToByte[charIdx];
            int byteEnd = charToByte[charIdx + te.Length];
            int byteLen = byteEnd - byteOffset;
            int cpLen = 0;
            foreach (Rune _ in te.EnumerateRunes())
            {
                cpLen++;
            }
            result.Add(new GraphemeRange(byteOffset, cpOffset, byteLen, cpLen));
            cpOffset += cpLen;
        }
        return result;
    }

    private enum EmojiChain : byte { None, Pict, ZwjAfterPict }

    /// <summary>
    /// Enumerate extended grapheme clusters over <paramref name="utf8"/> and
    /// materialize them into a list. Ill-formed UTF-8 bytes are treated as
    /// single-byte U+FFFD substitutions per Unicode best practice.
    /// </summary>
    public static List<GraphemeRange> Enumerate(ReadOnlySpan<byte> utf8, ICodepointProperties properties)
    {
        List<GraphemeRange> result = new();
        if (utf8.IsEmpty)
        {
            return result;
        }

        long clusterByteStart = 0;
        long clusterCpStart = 0;
        long byteOffset = 0;
        long cpOffset = 0;

        GraphemeBreak prev = GraphemeBreak.Other;
        bool hasPrev = false;
        int riRun = 0;
        EmojiChain chain = EmojiChain.None;

        int idx = 0;
        while (idx < utf8.Length)
        {
            (int cp, int consumed) = Utf8.DecodeOne(utf8[idx..]);
            if (cp < 0 || consumed == 0)
            {
                break;
            }

            GraphemeBreak curr = properties.GetGraphemeBreak(cp);
            bool currIsExtPict = properties.IsExtendedPictographic(cp);
            bool shouldBreak = hasPrev && DecideBreak(prev, curr, riRun, chain, currIsExtPict);

            if (shouldBreak)
            {
                result.Add(new GraphemeRange(
                    clusterByteStart,
                    clusterCpStart,
                    (int)(byteOffset - clusterByteStart),
                    (int)(cpOffset - clusterCpStart)));
                clusterByteStart = byteOffset;
                clusterCpStart = cpOffset;
            }

            if (curr == GraphemeBreak.RegionalIndicator)
            {
                riRun = shouldBreak ? 1 : riRun + 1;
            }
            else
            {
                riRun = 0;
            }

            chain = NextChain(chain, curr, currIsExtPict);

            prev = curr;
            hasPrev = true;
            byteOffset += consumed;
            cpOffset += 1;
            idx += consumed;
        }

        if (hasPrev)
        {
            result.Add(new GraphemeRange(
                clusterByteStart,
                clusterCpStart,
                (int)(byteOffset - clusterByteStart),
                (int)(cpOffset - clusterCpStart)));
        }

        return result;
    }

    /// <summary>
    /// Count extended grapheme clusters without materializing the ranges.
    /// </summary>
    public static long Count(ReadOnlySpan<byte> utf8, ICodepointProperties properties)
    {
        if (utf8.IsEmpty)
        {
            return 0;
        }

        long count = 1;
        GraphemeBreak prev = GraphemeBreak.Other;
        bool hasPrev = false;
        int riRun = 0;
        EmojiChain chain = EmojiChain.None;

        int idx = 0;
        while (idx < utf8.Length)
        {
            (int cp, int consumed) = Utf8.DecodeOne(utf8[idx..]);
            if (cp < 0 || consumed == 0)
            {
                break;
            }

            GraphemeBreak curr = properties.GetGraphemeBreak(cp);
            bool currIsExtPict = properties.IsExtendedPictographic(cp);
            bool shouldBreak = hasPrev && DecideBreak(prev, curr, riRun, chain, currIsExtPict);
            if (shouldBreak)
            {
                count++;
            }

            if (curr == GraphemeBreak.RegionalIndicator)
            {
                riRun = shouldBreak ? 1 : riRun + 1;
            }
            else
            {
                riRun = 0;
            }

            chain = NextChain(chain, curr, currIsExtPict);

            prev = curr;
            hasPrev = true;
            idx += consumed;
        }

        return count;
    }

    private static EmojiChain NextChain(EmojiChain prev, GraphemeBreak curr, bool currIsExtPict)
    {
        if (currIsExtPict)
        {
            return EmojiChain.Pict;
        }
        if (curr == GraphemeBreak.Extend && prev == EmojiChain.Pict)
        {
            return EmojiChain.Pict;
        }
        if (curr == GraphemeBreak.ZWJ && prev == EmojiChain.Pict)
        {
            return EmojiChain.ZwjAfterPict;
        }
        return EmojiChain.None;
    }

    private static bool DecideBreak(
        GraphemeBreak prev,
        GraphemeBreak curr,
        int riRun,
        EmojiChain chain,
        bool currIsExtPict)
    {
        // GB3: CR × LF
        if (prev == GraphemeBreak.CR && curr == GraphemeBreak.LF)
        {
            return false;
        }
        // GB4: (Control | CR | LF) ÷
        if (prev == GraphemeBreak.Control || prev == GraphemeBreak.CR || prev == GraphemeBreak.LF)
        {
            return true;
        }
        // GB5: ÷ (Control | CR | LF)
        if (curr == GraphemeBreak.Control || curr == GraphemeBreak.CR || curr == GraphemeBreak.LF)
        {
            return true;
        }
        // GB6: L × (L | V | LV | LVT)
        if (prev == GraphemeBreak.L &&
            (curr == GraphemeBreak.L || curr == GraphemeBreak.V ||
             curr == GraphemeBreak.LV || curr == GraphemeBreak.LVT))
        {
            return false;
        }
        // GB7: (LV | V) × (V | T)
        if ((prev == GraphemeBreak.LV || prev == GraphemeBreak.V) &&
            (curr == GraphemeBreak.V || curr == GraphemeBreak.T))
        {
            return false;
        }
        // GB8: (LVT | T) × T
        if ((prev == GraphemeBreak.LVT || prev == GraphemeBreak.T) && curr == GraphemeBreak.T)
        {
            return false;
        }
        // GB9: × (Extend | ZWJ)
        if (curr == GraphemeBreak.Extend || curr == GraphemeBreak.ZWJ)
        {
            return false;
        }
        // GB9a: × SpacingMark
        if (curr == GraphemeBreak.SpacingMark)
        {
            return false;
        }
        // GB9b: Prepend ×
        if (prev == GraphemeBreak.Prepend)
        {
            return false;
        }
        // GB11: Extended_Pictographic Extend* ZWJ × Extended_Pictographic
        if (chain == EmojiChain.ZwjAfterPict && currIsExtPict)
        {
            return false;
        }
        // GB12 / GB13: RI × RI when the trailing RI run so far has ODD length.
        if (prev == GraphemeBreak.RegionalIndicator && curr == GraphemeBreak.RegionalIndicator)
        {
            return (riRun % 2) == 0;
        }
        // GB999: otherwise break.
        return true;
    }
}
