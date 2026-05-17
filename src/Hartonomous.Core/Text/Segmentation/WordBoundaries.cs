using Hartonomous.Core.Native;

namespace Hartonomous.Core.Text.Segmentation;

/// <summary>
/// UAX #29 word-boundary segmentation. Thin C# wrapper around the canonical
/// native implementation at <c>ext/libhartonomous/src/text_decompose.c</c>.
///
/// <para>
/// Single source of UAX-29 truth per rule 10-text-and-semantics + Law #6 +
/// the compute-facade discipline in CLAUDE.md. The PG extension's
/// <c>pg_text_decompose</c> and this C# binding both call into
/// <c>hartonomous_text_word_boundaries</c> in the same libhartonomous build —
/// one implementation, byte-identical hashes across PG and C#.
/// </para>
///
/// <para>
/// The prior in-process C# state machine (~440 lines implementing
/// WB1–WB16, ALetter/MidNum/MidLetter classification, ZWJ × ExtPict
/// handling, etc.) was a second UAX-29 implementation that drifted from the
/// native canonical and had no production callers. Deleted; the native
/// path is the only path.
/// </para>
/// </summary>
public static class WordBoundaries
{
    /// <summary>
    /// Enumerate every word-break opportunity as a byte offset (including 0
    /// and the input length, per WB1 / WB2). Calls the native UAX-29 kernel
    /// and converts codepoint boundaries to UTF-8 byte offsets via a single
    /// source-byte walk. <paramref name="properties"/> is retained for API
    /// compatibility but ignored — native sources UCD properties from the
    /// substrate's bundled UCD blob.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">
    /// Native UCD blob not loadable, or native returns codepoint boundaries
    /// that don't map cleanly to the original UTF-8 byte stream (NFC changed
    /// codepoint count). Substrate-content paths MUST have the blob installed;
    /// there is no in-process fallback because a second UAX-29 implementation
    /// would drift from the canonical one.
    /// </exception>
    public static List<long> EnumerateBoundaries(ReadOnlySpan<byte> utf8, ICodepointProperties properties)
        => EnumerateBoundaries(utf8);

    /// <summary>Native-only word boundary enumeration.</summary>
    public static unsafe List<long> EnumerateBoundaries(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            return new List<long> { 0 };
        }

        Hartonomous.Core.Text.SubstrateTextDecomposer.EnsureUcdLoaded();

        byte[] buf = new byte[utf8.Length];
        utf8.CopyTo(buf);

        int[] cpBoundaries;
        int wordCount;

        fixed (byte* utf8Ptr = buf)
        {
            IntPtr utf8Ip = (IntPtr) utf8Ptr;
            int rc = TextDecomposeNative.WordBoundaries(
                utf8Ip, (nuint) buf.Length, IntPtr.Zero, 0, out wordCount);
            if (rc != 0)
            {
                throw new System.InvalidOperationException(
                    $"hartonomous_text_word_boundaries returned {rc}");
            }

            cpBoundaries = new int[System.Math.Max(wordCount, 1)];
            fixed (int* outPtr = cpBoundaries)
            {
                int outCount;
                rc = TextDecomposeNative.WordBoundaries(
                    utf8Ip, (nuint) buf.Length, (IntPtr) outPtr, cpBoundaries.Length, out outCount);
                if (rc != 0)
                {
                    throw new System.InvalidOperationException(
                        $"hartonomous_text_word_boundaries returned {rc} on the read pass");
                }
                wordCount = outCount;
            }
        }

        // Native returns post-NFC codepoint indices. Walk source bytes forward
        // counting codepoints to map each boundary back to a byte offset.
        // Inputs whose NFC changes codepoint count cannot be safely mapped
        // here (the inverse-NFC map would need to live in the kernel) — they
        // raise rather than silently producing wrong byte offsets.
        List<long> outBoundaries = new(wordCount + 1);
        int cursorBytes = 0;
        int cursorCps = 0;
        outBoundaries.Add(0);
        for (int i = 0; i < wordCount; i++)
        {
            int targetCp = cpBoundaries[i];
            if (targetCp <= 0) { continue; }
            while (cursorCps < targetCp && cursorBytes < utf8.Length)
            {
                (int _, int consumed) = Utf8.DecodeOne(utf8.Slice(cursorBytes));
                if (consumed <= 0 || cursorBytes + consumed > utf8.Length)
                {
                    throw new System.InvalidOperationException(
                        "Malformed UTF-8 encountered while mapping native word boundaries to byte offsets.");
                }
                cursorBytes += consumed;
                cursorCps++;
            }
            if (cursorCps != targetCp)
            {
                throw new System.InvalidOperationException(
                    "Native UAX-29 word boundaries reference a post-NFC codepoint index that doesn't "
                    + "map to a position in the original UTF-8 byte stream. Pre-normalize the input "
                    + "or extend the native kernel to return byte offsets directly.");
            }
            outBoundaries.Add(cursorBytes);
        }
        if (outBoundaries[^1] != utf8.Length)
        {
            outBoundaries.Add(utf8.Length);
        }
        return outBoundaries;
    }
}
