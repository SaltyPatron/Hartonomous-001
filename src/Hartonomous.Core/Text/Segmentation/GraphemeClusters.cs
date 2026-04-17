namespace Hartonomous.Core.Text.Segmentation;

/// <summary>
/// UAX #29 extended grapheme cluster segmentation. The primitive walks UTF-8
/// input once and applies the UAX #29 break rules using codepoint properties
/// sourced from the substrate (<see cref="ICodepointProperties"/>). All rules
/// implemented: GB1–GB9b, GB11 (emoji ZWJ sequences), GB12/GB13, GB999.
/// </summary>
public static class GraphemeClusters
{
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
