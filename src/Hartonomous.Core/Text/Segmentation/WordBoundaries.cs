namespace Hartonomous.Core.Text.Segmentation;

/// <summary>
/// UAX #29 word-boundary segmentation. Implements rules WB1 – WB16 over UTF-8
/// input using codepoint properties sourced from <see cref="ICodepointProperties"/>.
/// Extend / Format / ZWJ codepoints attach to the preceding token per WB4 and do
/// not create independent boundaries.
/// </summary>
public static class WordBoundaries
{
    /// <summary>
    /// Enumerate word-like ranges (ALetter / Numeric / Katakana / Emoji, etc.).
    /// Pure-whitespace and pure-punctuation segments are skipped in this view.
    /// </summary>
    public static List<WordRange> EnumerateWords(ReadOnlySpan<byte> utf8, ICodepointProperties properties)
    {
        List<WordRange> words = new();
        if (utf8.IsEmpty)
        {
            return words;
        }

        List<WbToken> tokens = CollectTokens(utf8, properties);
        if (tokens.Count == 0)
        {
            return words;
        }

        bool[] breaks = ComputeBreaks(tokens);
        int n = tokens.Count;
        int segStart = 0;
        for (int i = 1; i <= n; i++)
        {
            if (!breaks[i])
            {
                continue;
            }

            WordKind kind = ClassifySegment(tokens, segStart, i);
            long byteStart = tokens[segStart].ByteOffset;
            long byteEnd = (i < n) ? tokens[i].ByteOffset : utf8.Length;
            int byteLen = (int)(byteEnd - byteStart);
            // Include WordKind.Other (whitespace/punct) ranges. The canonical
            // text decomposer emits these as raw_span text_compositions so
            // recompose_text reconstructs the surface text byte-for-byte.
            // Skipping Other here was the cause of recompose dropping all
            // inter-word spaces — composition walks reached only word_forms.
            if (byteLen > 0)
            {
                words.Add(new WordRange(byteStart, byteLen, kind));
            }
            segStart = i;
        }
        return words;
    }

    /// <summary>
    /// Enumerate every word-break opportunity as a byte offset (including 0 and
    /// the input length, per WB1 / WB2). Used where callers want the full break
    /// stream rather than only word-like segments.
    ///
    /// PRIMARY PATH: P/Invoke into the native UAX-29 kernel
    /// (<see cref="Hartonomous.Core.Native.TextDecomposeNative.WordBoundaries"/>).
    /// The native implementation is the substrate's SINGLE source of UAX-29
    /// truth (rule 10-text-and-semantics + Law #6 + CLAUDE.md compute-facade).
    /// Falls back to the in-process C# state machine ONLY when the native
    /// UCD blob is unavailable (cold-start ingest, isolated unit tests).
    /// The fallback path is on the deprecation list — task #21.
    /// </summary>
    public static List<long> EnumerateBoundaries(ReadOnlySpan<byte> utf8, ICodepointProperties properties)
    {
        if (utf8.IsEmpty)
        {
            return new List<long> { 0 };
        }

        if (TryNativeBoundaries(utf8, out List<long>? native))
        {
            return native!;
        }

        // Fallback: in-process C# state machine.
        List<long> positions = new();
        List<WbToken> tokens = CollectTokens(utf8, properties);
        positions.Add(0);
        if (tokens.Count == 0)
        {
            positions.Add(utf8.Length);
            return positions;
        }

        bool[] breaks = ComputeBreaks(tokens);
        int n = tokens.Count;
        for (int i = 1; i < n; i++)
        {
            if (breaks[i])
            {
                positions.Add(tokens[i].ByteOffset);
            }
        }
        positions.Add(utf8.Length);
        return positions;
    }

    /// <summary>
    /// Calls the native UAX-29 word boundary kernel and converts codepoint
    /// indices to UTF-8 byte offsets. Returns false if native isn't loadable
    /// (UCD blob missing or P/Invoke unavailable) so callers can fall back.
    /// </summary>
    private static unsafe bool TryNativeBoundaries(ReadOnlySpan<byte> utf8, out List<long>? boundaries)
    {
        boundaries = null;
        if (utf8.IsEmpty)
        {
            return false;
        }

        try
        {
            Hartonomous.Core.Text.SubstrateTextDecomposer.EnsureUcdLoaded();
        }
        catch (InvalidOperationException)  // BOUNDARY: native UCD blob load; fall back to in-process C# state machine if blob isn't on disk.
        {
            return false;
        }

        byte[] buf = new byte[utf8.Length];
        utf8.CopyTo(buf);

        int[] cpBoundaries;
        int cpCount;
        int wordCount;

        fixed (byte* utf8Ptr = buf)
        {
            IntPtr utf8Ip = (IntPtr) utf8Ptr;
            int rc = Hartonomous.Core.Native.TextDecomposeNative.CodepointCount(
                utf8Ip, (nuint) buf.Length, out cpCount);
            if (rc != 0) { return false; }

            // First call: get word count.
            rc = Hartonomous.Core.Native.TextDecomposeNative.WordBoundaries(
                utf8Ip, (nuint) buf.Length, IntPtr.Zero, 0, out wordCount);
            if (rc != 0) { return false; }

            cpBoundaries = new int[Math.Max(wordCount, 1)];
            fixed (int* outPtr = cpBoundaries)
            {
                int outCount;
                rc = Hartonomous.Core.Native.TextDecomposeNative.WordBoundaries(
                    utf8Ip, (nuint) buf.Length, (IntPtr) outPtr, cpBoundaries.Length, out outCount);
                if (rc != 0) { return false; }
                wordCount = outCount;
            }
        }

        // Native returns post-NFC codepoint indices. The UCD conformance tests
        // expect byte offsets into the ORIGINAL (pre-normalized) UTF-8. For
        // ASCII input these coincide; for non-ASCII normalization-stable input
        // they also coincide via byte-walking. For inputs where NFC changes
        // codepoint count we fall back to the C# state machine. This caveat is
        // tracked in task #21 (full native-only path implies a kernel API that
        // returns byte offsets directly or an inverse-NFC map).
        List<long> outBoundaries = new(wordCount + 1);
        int cursorBytes = 0;
        int cursorCps = 0;
        outBoundaries.Add(0);
        for (int i = 0; i < wordCount; i++)
        {
            int targetCp = cpBoundaries[i];
            if (targetCp <= 0) { continue; }
            // Walk forward in the source bytes counting codepoints until cursor matches.
            while (cursorCps < targetCp && cursorBytes < utf8.Length)
            {
                (int _, int consumed) = Utf8.DecodeOne(utf8.Slice(cursorBytes));
                if (consumed <= 0 || cursorBytes + consumed > utf8.Length) { return false; }  // malformed
                cursorBytes += consumed;
                cursorCps++;
            }
            if (cursorCps != targetCp) { return false; }  // NFC changed codepoint count → fall back
            outBoundaries.Add(cursorBytes);
        }
        if (outBoundaries[^1] != utf8.Length)
        {
            outBoundaries.Add(utf8.Length);
        }
        boundaries = outBoundaries;
        return true;
    }

    // LastPhysWb tracks the word_break of the LAST physical codepoint folded
    // into this token (independent of the base Wb). Some rules — WB3c
    // (ZWJ × ExtPict) and WB3d (WSegSpace × WSegSpace) — only fire when the
    // literal preceding codepoint matches; an intervening Extend/Format kills
    // them, even though WB4 attaches all of them to the same base.
    // UCD WordBreakTest.txt: line 1059 `200D × 24C2` joins (WB3c [3.3]); line
    // 1060 `200D × 0308 ÷ 24C2` breaks (WB999 [999.0]); line 1206
    // `0020 × 0308 ÷ 0020` likewise breaks despite both ends being WSegSpace.
    private readonly record struct WbToken(
        WordBreak Wb,
        long ByteOffset,
        int TotalByteLength,
        int Codepoint,
        bool IsExtPict,
        WordBreak LastPhysWb);

    private static List<WbToken> CollectTokens(ReadOnlySpan<byte> utf8, ICodepointProperties properties)
    {
        List<WbToken> tokens = new();
        int idx = 0;
        long byteOffset = 0;
        while (idx < utf8.Length)
        {
            (int cp, int consumed) = Utf8.DecodeOne(utf8[idx..]);
            if (cp < 0 || consumed == 0)
            {
                break;
            }

            WordBreak wb = properties.GetWordBreak(cp);
            bool isAttach = wb is WordBreak.Extend or WordBreak.Format or WordBreak.ZWJ;
            bool canAttach = isAttach
                && tokens.Count > 0
                && tokens[^1].Wb is not (WordBreak.CR or WordBreak.LF or WordBreak.Newline);

            if (canAttach)
            {
                WbToken prev = tokens[^1];
                tokens[^1] = prev with
                {
                    TotalByteLength = prev.TotalByteLength + consumed,
                    LastPhysWb = wb,
                };
            }
            else
            {
                bool isExtPict = properties.IsExtendedPictographic(cp);
                tokens.Add(new WbToken(wb, byteOffset, consumed, cp, isExtPict, wb));
            }

            byteOffset += consumed;
            idx += consumed;
        }
        return tokens;
    }

    private static bool[] ComputeBreaks(List<WbToken> tokens)
    {
        int n = tokens.Count;
        bool[] breaks = new bool[n + 1];
        breaks[0] = true;
        breaks[n] = true;

        for (int i = 1; i < n; i++)
        {
            breaks[i] = DecideBreak(tokens, i);
        }
        return breaks;
    }

    private static bool DecideBreak(List<WbToken> tokens, int i)
    {
        WbToken a = tokens[i - 1];
        WbToken b = tokens[i];

        // WB3: CR × LF
        if (a.Wb == WordBreak.CR && b.Wb == WordBreak.LF)
        {
            return false;
        }
        // WB3a: (Newline | CR | LF) ÷
        if (a.Wb is WordBreak.CR or WordBreak.LF or WordBreak.Newline)
        {
            return true;
        }
        // WB3b: ÷ (Newline | CR | LF)
        if (b.Wb is WordBreak.CR or WordBreak.LF or WordBreak.Newline)
        {
            return true;
        }
        // WB3c: ZWJ × Extended_Pictographic — applies to the LITERAL preceding
        // codepoint, not the WB4-attached base. An Extend/Format between ZWJ and
        // ExtPict cancels WB3c (UCD WordBreakTest.txt rule [999.0]).
        if (a.LastPhysWb == WordBreak.ZWJ && b.IsExtPict)
        {
            return false;
        }
        // WB3d: WSegSpace × WSegSpace — likewise needs literal adjacency. An
        // Extend between two spaces (line 1206) breaks per default rule.
        if (a.LastPhysWb == WordBreak.WSegSpace && b.Wb == WordBreak.WSegSpace)
        {
            return false;
        }
        // WB5: AHLetter × AHLetter
        if (IsAHLetter(a.Wb) && IsAHLetter(b.Wb))
        {
            return false;
        }
        // WB6: AHLetter × (MidLetter | MidNumLet | SingleQuote) AHLetter
        if (IsAHLetter(a.Wb) && IsMidLetterLike(b.Wb) &&
            i + 1 < tokens.Count && IsAHLetter(tokens[i + 1].Wb))
        {
            return false;
        }
        // WB7: AHLetter (MidLetter | MidNumLet | SingleQuote) × AHLetter
        if (i >= 2 && IsAHLetter(b.Wb) && IsMidLetterLike(a.Wb) && IsAHLetter(tokens[i - 2].Wb))
        {
            return false;
        }
        // WB7a: HebrewLetter × SingleQuote
        if (a.Wb == WordBreak.HebrewLetter && b.Wb == WordBreak.SingleQuote)
        {
            return false;
        }
        // WB7b: HebrewLetter × DoubleQuote HebrewLetter
        if (a.Wb == WordBreak.HebrewLetter && b.Wb == WordBreak.DoubleQuote &&
            i + 1 < tokens.Count && tokens[i + 1].Wb == WordBreak.HebrewLetter)
        {
            return false;
        }
        // WB7c: HebrewLetter DoubleQuote × HebrewLetter
        if (i >= 2 && b.Wb == WordBreak.HebrewLetter && a.Wb == WordBreak.DoubleQuote &&
            tokens[i - 2].Wb == WordBreak.HebrewLetter)
        {
            return false;
        }
        // WB8: Numeric × Numeric
        if (a.Wb == WordBreak.Numeric && b.Wb == WordBreak.Numeric)
        {
            return false;
        }
        // WB9: AHLetter × Numeric
        if (IsAHLetter(a.Wb) && b.Wb == WordBreak.Numeric)
        {
            return false;
        }
        // WB10: Numeric × AHLetter
        if (a.Wb == WordBreak.Numeric && IsAHLetter(b.Wb))
        {
            return false;
        }
        // WB11: Numeric (MidNum | MidNumLet | SingleQuote) × Numeric
        if (i >= 2 && b.Wb == WordBreak.Numeric && IsMidNumLike(a.Wb) &&
            tokens[i - 2].Wb == WordBreak.Numeric)
        {
            return false;
        }
        // WB12: Numeric × (MidNum | MidNumLet | SingleQuote) Numeric
        if (a.Wb == WordBreak.Numeric && IsMidNumLike(b.Wb) &&
            i + 1 < tokens.Count && tokens[i + 1].Wb == WordBreak.Numeric)
        {
            return false;
        }
        // WB13: Katakana × Katakana
        if (a.Wb == WordBreak.Katakana && b.Wb == WordBreak.Katakana)
        {
            return false;
        }
        // WB13a: (AHLetter | Numeric | Katakana | ExtendNumLet) × ExtendNumLet
        if ((IsAHLetter(a.Wb) || a.Wb == WordBreak.Numeric || a.Wb == WordBreak.Katakana ||
             a.Wb == WordBreak.ExtendNumLet) && b.Wb == WordBreak.ExtendNumLet)
        {
            return false;
        }
        // WB13b: ExtendNumLet × (AHLetter | Numeric | Katakana)
        if (a.Wb == WordBreak.ExtendNumLet &&
            (IsAHLetter(b.Wb) || b.Wb == WordBreak.Numeric || b.Wb == WordBreak.Katakana))
        {
            return false;
        }
        // WB15 / WB16: RI × RI when the preceding run of RIs has odd length.
        if (a.Wb == WordBreak.RegionalIndicator && b.Wb == WordBreak.RegionalIndicator)
        {
            int riRun = 1;
            for (int k = i - 2; k >= 0 && tokens[k].Wb == WordBreak.RegionalIndicator; k--)
            {
                riRun++;
            }
            if ((riRun % 2) == 1)
            {
                return false;
            }
        }
        // WB999
        return true;
    }

    private static bool IsAHLetter(WordBreak wb) =>
        wb is WordBreak.ALetter or WordBreak.HebrewLetter;

    private static bool IsMidLetterLike(WordBreak wb) =>
        wb is WordBreak.MidLetter or WordBreak.MidNumLet or WordBreak.SingleQuote;

    private static bool IsMidNumLike(WordBreak wb) =>
        wb is WordBreak.MidNum or WordBreak.MidNumLet or WordBreak.SingleQuote;

    private static WordKind ClassifySegment(List<WbToken> tokens, int start, int endEx)
    {
        bool hasEmoji = false;
        bool hasAlpha = false;
        bool hasNumeric = false;
        bool hasKatakana = false;

        for (int i = start; i < endEx; i++)
        {
            WbToken t = tokens[i];
            if (t.IsExtPict)
            {
                hasEmoji = true;
            }
            switch (t.Wb)
            {
                case WordBreak.ALetter:
                case WordBreak.HebrewLetter:
                case WordBreak.ExtendNumLet:
                    hasAlpha = true;
                    break;
                case WordBreak.Numeric:
                    hasNumeric = true;
                    break;
                case WordBreak.Katakana:
                    hasKatakana = true;
                    break;
            }
        }

        if (hasAlpha)
        {
            return WordKind.AlphaNumeric;
        }
        if (hasKatakana)
        {
            return WordKind.Katakana;
        }
        if (hasNumeric)
        {
            return WordKind.Numeric;
        }
        if (hasEmoji)
        {
            return WordKind.Emoji;
        }
        return WordKind.Other;
    }
}
