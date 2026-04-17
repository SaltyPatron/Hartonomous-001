namespace Hartonomous.Core.Text.Segmentation;

/// <summary>
/// UAX #29 sentence-boundary segmentation. Implements rules SB1 – SB11 using
/// codepoint properties from <see cref="ICodepointProperties"/>. Extend / Format
/// codepoints attach to the preceding token per SB5.
/// </summary>
public static class SentenceBoundaries
{
    /// <summary>
    /// Enumerate sentences as contiguous UTF-8 byte ranges. Every input byte is
    /// covered by exactly one sentence range.
    /// </summary>
    public static List<SentenceRange> Enumerate(ReadOnlySpan<byte> utf8, ICodepointProperties properties)
    {
        List<SentenceRange> sentences = new();
        if (utf8.IsEmpty)
        {
            return sentences;
        }

        List<SbToken> tokens = CollectTokens(utf8, properties);
        if (tokens.Count == 0)
        {
            return sentences;
        }

        int n = tokens.Count;
        int start = 0;
        for (int i = 1; i <= n; i++)
        {
            bool brk = (i == n) || IsBreakBefore(tokens, i);
            if (!brk)
            {
                continue;
            }

            long byteStart = tokens[start].ByteOffset;
            long byteEnd = (i < n) ? tokens[i].ByteOffset : utf8.Length;
            sentences.Add(new SentenceRange(byteStart, (int)(byteEnd - byteStart)));
            start = i;
        }
        return sentences;
    }

    private readonly record struct SbToken(SentenceBreak Sb, long ByteOffset, int TotalByteLength);

    private static List<SbToken> CollectTokens(ReadOnlySpan<byte> utf8, ICodepointProperties properties)
    {
        List<SbToken> tokens = new();
        int idx = 0;
        long byteOffset = 0;
        while (idx < utf8.Length)
        {
            (int cp, int consumed) = Utf8.DecodeOne(utf8[idx..]);
            if (cp < 0 || consumed == 0)
            {
                break;
            }

            SentenceBreak sb = properties.GetSentenceBreak(cp);
            bool isAttach = sb is SentenceBreak.Extend or SentenceBreak.Format;
            bool canAttach = isAttach
                && tokens.Count > 0
                && tokens[^1].Sb is not (SentenceBreak.CR or SentenceBreak.LF or SentenceBreak.Sep);

            if (canAttach)
            {
                SbToken prev = tokens[^1];
                tokens[^1] = prev with { TotalByteLength = prev.TotalByteLength + consumed };
            }
            else
            {
                tokens.Add(new SbToken(sb, byteOffset, consumed));
            }

            byteOffset += consumed;
            idx += consumed;
        }
        return tokens;
    }

    private static bool IsBreakBefore(List<SbToken> tokens, int i)
    {
        SbToken a = tokens[i - 1];
        SbToken b = tokens[i];

        // SB3: CR × LF
        if (a.Sb == SentenceBreak.CR && b.Sb == SentenceBreak.LF)
        {
            return false;
        }
        // SB4: (Sep | CR | LF) ÷
        if (a.Sb is SentenceBreak.Sep or SentenceBreak.CR or SentenceBreak.LF)
        {
            return true;
        }
        // SB6: ATerm × Numeric
        if (a.Sb == SentenceBreak.ATerm && b.Sb == SentenceBreak.Numeric)
        {
            return false;
        }
        // SB7: (Upper | Lower) ATerm × Upper
        if (i >= 2 && a.Sb == SentenceBreak.ATerm && b.Sb == SentenceBreak.Upper &&
            tokens[i - 2].Sb is SentenceBreak.Upper or SentenceBreak.Lower)
        {
            return false;
        }
        // SB8: ATerm Close* Sp* × (¬{OLetter, Upper, Lower, Sep, CR, LF, STerm, ATerm})* Lower
        if (HasATermContext(tokens, i) && LookaheadSuggestsContinuation(tokens, i))
        {
            return false;
        }
        // SB8a: (STerm | ATerm) Close* Sp* × (SContinue | STerm | ATerm)
        if (b.Sb is SentenceBreak.SContinue or SentenceBreak.STerm or SentenceBreak.ATerm
            && HasSATermContext(tokens, i))
        {
            return false;
        }
        // SB9: (STerm | ATerm) Close* × (Close | Sp | Sep | CR | LF)
        if (b.Sb is SentenceBreak.Close or SentenceBreak.Sp or SentenceBreak.Sep
                 or SentenceBreak.CR or SentenceBreak.LF)
        {
            int k = i - 1;
            while (k >= 0 && tokens[k].Sb == SentenceBreak.Close)
            {
                k--;
            }
            if (k >= 0 && tokens[k].Sb is SentenceBreak.STerm or SentenceBreak.ATerm)
            {
                return false;
            }
        }
        // SB10: (STerm | ATerm) Close* Sp* × (Sp | Sep | CR | LF)
        if (b.Sb is SentenceBreak.Sp or SentenceBreak.Sep or SentenceBreak.CR or SentenceBreak.LF
            && HasSATermContext(tokens, i))
        {
            return false;
        }
        // SB11: (STerm | ATerm) Close* Sp* (Sep | CR | LF)? ÷
        if (HasSATermTerminated(tokens, i))
        {
            return true;
        }
        // SB998: otherwise, no break.
        return false;
    }

    private static bool HasSATermContext(List<SbToken> tokens, int i)
    {
        int k = i - 1;
        while (k >= 0 && tokens[k].Sb == SentenceBreak.Sp)
        {
            k--;
        }
        while (k >= 0 && tokens[k].Sb == SentenceBreak.Close)
        {
            k--;
        }
        return k >= 0 && tokens[k].Sb is SentenceBreak.STerm or SentenceBreak.ATerm;
    }

    private static bool HasATermContext(List<SbToken> tokens, int i)
    {
        int k = i - 1;
        while (k >= 0 && tokens[k].Sb == SentenceBreak.Sp)
        {
            k--;
        }
        while (k >= 0 && tokens[k].Sb == SentenceBreak.Close)
        {
            k--;
        }
        return k >= 0 && tokens[k].Sb == SentenceBreak.ATerm;
    }

    private static bool LookaheadSuggestsContinuation(List<SbToken> tokens, int i)
    {
        for (int j = i; j < tokens.Count; j++)
        {
            SentenceBreak s = tokens[j].Sb;
            if (s is SentenceBreak.OLetter or SentenceBreak.Upper or SentenceBreak.Sep
                  or SentenceBreak.CR or SentenceBreak.LF or SentenceBreak.STerm or SentenceBreak.ATerm)
            {
                return false;
            }
            if (s == SentenceBreak.Lower)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasSATermTerminated(List<SbToken> tokens, int i)
    {
        int k = i - 1;
        if (k >= 0 && tokens[k].Sb is SentenceBreak.Sep or SentenceBreak.CR or SentenceBreak.LF)
        {
            k--;
        }
        while (k >= 0 && tokens[k].Sb == SentenceBreak.Sp)
        {
            k--;
        }
        while (k >= 0 && tokens[k].Sb == SentenceBreak.Close)
        {
            k--;
        }
        return k >= 0 && tokens[k].Sb is SentenceBreak.STerm or SentenceBreak.ATerm;
    }
}
