using System.Globalization;
using System.Text;

namespace Hartonomous.Core.Text.Segmentation;

/// <summary>
/// UAX #29 extended grapheme cluster segmentation. Thin C# wrapper around the
/// canonical native implementation at <c>ext/libhartonomous/src/text_decompose.c</c>.
///
/// <para>
/// Single source of UAX-29 truth (rule 10-text-and-semantics + Law #6 + the
/// compute-facade discipline in CLAUDE.md). The PG extension's
/// <c>pg_text_decompose</c> and this C# binding both call into
/// <c>hartonomous_text_grapheme_boundaries</c> in the same libhartonomous
/// build — one implementation, byte-identical hashes across PG and C#.
/// </para>
///
/// <para>
/// The prior C# state machine that lived here failed 425 / 766 UCD test
/// cases (44.5% conformance) and was a second implementation of the same
/// algorithm — exactly the kind of reinvented wheel the substrate's
/// canonical-implementation discipline forbids. Deleted; the native path
/// is the only path.
/// </para>
/// </summary>
public static class GraphemeClusters
{
    /// <summary>
    /// .NET <see cref="StringInfo.GetTextElementEnumerator(string)"/>-backed
    /// grapheme cluster enumeration. Delegates to Microsoft's UAX #29 implementation
    /// (currently UAX #29 v15.1 in .NET 9, Unicode 16). Provided as an
    /// explicitly-named alternative for callers who want a non-substrate UAX-29
    /// implementation (e.g. UI display, where the substrate's UCD version may
    /// differ from the host runtime's). It is NOT the substrate's identity-
    /// bearing path — anything that hashes content MUST go through
    /// <see cref="Enumerate"/> which uses the substrate's bundled UCD via native.
    /// </summary>
    public static List<GraphemeRange> EnumerateUsingNet(ReadOnlySpan<byte> utf8)
    {
        List<GraphemeRange> result = new();
        if (utf8.IsEmpty)
        {
            return result;
        }

        string s = Encoding.UTF8.GetString(utf8);
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

    /// <summary>
    /// Enumerate extended grapheme clusters over <paramref name="utf8"/>.
    /// Calls the native UAX-29 kernel and converts codepoint boundaries to
    /// UTF-8 byte ranges. The <paramref name="properties"/> argument is
    /// retained for API compatibility but unused — native sources UCD
    /// properties from the substrate's bundled UCD blob (Law #6: one UCD
    /// version owns substrate identity).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Native UCD blob not loadable. Substrate-content paths MUST have the
    /// blob installed; there is no in-process fallback because a second
    /// UAX-29 implementation would drift from the canonical one. Cold-start
    /// environments must call <c>SubstrateTextDecomposer.EnsureUcdLoaded()</c>
    /// (or rely on the auto-load) before any content-hashing path runs.
    /// </exception>
    public static List<GraphemeRange> Enumerate(ReadOnlySpan<byte> utf8, ICodepointProperties properties)
        => Enumerate(utf8);

    /// <summary>
    /// Native-only grapheme enumeration. The <see cref="ICodepointProperties"/>
    /// overload exists for legacy callers; new code should call this directly.
    /// </summary>
    public static List<GraphemeRange> Enumerate(ReadOnlySpan<byte> utf8)
    {
        List<GraphemeRange> result = new();
        if (utf8.IsEmpty)
        {
            return result;
        }

        if (!TryNativeEnumerate(utf8, out List<GraphemeRange>? native))
        {
            throw new InvalidOperationException(
                "GraphemeClusters.Enumerate: native UAX-29 kernel unavailable. "
                + "Substrate identity requires the bundled UCD blob; install "
                + "hartonomous-ucd to /opt/pg18/share/postgresql/extension/hartonomous-ucd "
                + "or set HARTONOMOUS_UCD_BLOB_DIR. The previous in-process C# "
                + "fallback was a second UAX-29 implementation that fragmented "
                + "substrate identity (44.5% UCD conformance) and was removed.");
        }
        return native!;
    }

    /// <summary>
    /// Count extended grapheme clusters without materializing the ranges.
    /// </summary>
    public static long Count(ReadOnlySpan<byte> utf8, ICodepointProperties properties)
        => Count(utf8);

    /// <summary>Native-only count.</summary>
    public static long Count(ReadOnlySpan<byte> utf8)
        => Enumerate(utf8).Count;

    /// <summary>
    /// Calls the native UAX-29 grapheme boundary kernel and materializes
    /// per-grapheme ranges in original-UTF-8 byte space. Returns false if
    /// native isn't loadable so callers can produce a clearer error.
    /// </summary>
    private static unsafe bool TryNativeEnumerate(ReadOnlySpan<byte> utf8, out List<GraphemeRange>? ranges)
    {
        ranges = null;
        if (utf8.IsEmpty)
        {
            return false;
        }

        try
        {
            Hartonomous.Core.Text.SubstrateTextDecomposer.EnsureUcdLoaded();
        }
        catch (InvalidOperationException)  // BOUNDARY: native UCD blob load. Caller-visible InvalidOperationException raised at the public Enumerate boundary; this internal short-circuit just propagates "blob missing".
        {
            return false;
        }

        byte[] buf = new byte[utf8.Length];
        utf8.CopyTo(buf);

        int[] cpBoundaries;
        int graphemeCount;

        fixed (byte* utf8Ptr = buf)
        {
            IntPtr utf8Ip = (IntPtr) utf8Ptr;
            int rc = Hartonomous.Core.Native.TextDecomposeNative.GraphemeBoundaries(
                utf8Ip, (nuint) buf.Length, IntPtr.Zero, 0, out graphemeCount);
            if (rc != 0) { return false; }

            cpBoundaries = new int[Math.Max(graphemeCount, 1)];
            fixed (int* outPtr = cpBoundaries)
            {
                int outCount;
                rc = Hartonomous.Core.Native.TextDecomposeNative.GraphemeBoundaries(
                    utf8Ip, (nuint) buf.Length, (IntPtr) outPtr, cpBoundaries.Length, out outCount);
                if (rc != 0) { return false; }
                graphemeCount = outCount;
            }
        }

        List<GraphemeRange> result = new(graphemeCount);
        int cursorBytes = 0;
        int cursorCps = 0;
        long clusterByteStart = 0;
        long clusterCpStart = 0;

        for (int i = 1; i <= graphemeCount; i++)
        {
            int targetCp = (i < graphemeCount) ? cpBoundaries[i] : -1;  // -1 = end-of-input
            while ((targetCp < 0 || cursorCps < targetCp) && cursorBytes < utf8.Length)
            {
                (int _, int consumed) = Utf8.DecodeOne(utf8.Slice(cursorBytes));
                if (consumed <= 0 || cursorBytes + consumed > utf8.Length) { return false; }
                cursorBytes += consumed;
                cursorCps++;
            }
            if (targetCp >= 0 && cursorCps != targetCp) { return false; }
            result.Add(new GraphemeRange(
                clusterByteStart,
                clusterCpStart,
                (int)(cursorBytes - clusterByteStart),
                (int)(cursorCps - clusterCpStart)));
            clusterByteStart = cursorBytes;
            clusterCpStart = cursorCps;
        }
        ranges = result;
        return true;
    }
}
