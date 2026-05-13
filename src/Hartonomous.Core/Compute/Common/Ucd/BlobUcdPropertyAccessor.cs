using System;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Geometry;
using Hartonomous.Core.Native;
using Hartonomous.Core.Text;
using Hartonomous.Core.Text.Segmentation;

namespace Hartonomous.Core.Compute.Common.Ucd;

/// <summary>
/// Blob-backed implementation of <see cref="IUcdPropertyAccessor"/>. Every
/// read routes through libhartonomous's <c>hartonomous_ucd_cp_*</c> exports
/// against the embedded UCD tables (segmentation, case folding, extended
/// pictographic) and the embedded UCD blob (per-codepoint BLAKE3 hash + S³
/// centroid). One source of truth across C# / native / PG callers; AP-7
/// compliance by construction (no DB round-trip, no 303,808-row eager
/// preload).
///
/// <para>
/// Lazy-loads the UCD blob via
/// <see cref="SubstrateTextDecomposer.EnsureUcdLoaded"/> on first access;
/// subsequent calls are O(1) array indices in libhartonomous-resident
/// memory. Singleton lifetime is appropriate; the underlying tables are
/// process-global.
/// </para>
/// </summary>
public sealed class BlobUcdPropertyAccessor : IUcdPropertyAccessor
{
    /// <summary>
    /// Worst-case expansion length for any UCD full case fold mapping at
    /// Unicode 17.0. Sized for future-proofing — present-day expansions
    /// max out at 3 codepoints.
    /// </summary>
    private const int FullCaseFoldMaxLen = 8;

    /// <inheritdoc/>
    public GraphemeBreak GetGcb(int codepoint)
    {
        SubstrateTextDecomposer.EnsureUcdLoaded();
        return (GraphemeBreak)TextDecomposeNative.UcdCpGcb(codepoint);
    }

    /// <inheritdoc/>
    public WordBreak GetWb(int codepoint)
    {
        SubstrateTextDecomposer.EnsureUcdLoaded();
        return (WordBreak)TextDecomposeNative.UcdCpWb(codepoint);
    }

    /// <inheritdoc/>
    public SentenceBreak GetSb(int codepoint)
    {
        SubstrateTextDecomposer.EnsureUcdLoaded();
        return (SentenceBreak)TextDecomposeNative.UcdCpSb(codepoint);
    }

    /// <inheritdoc/>
    public LineBreak GetLb(int codepoint)
    {
        SubstrateTextDecomposer.EnsureUcdLoaded();
        return (LineBreak)TextDecomposeNative.UcdCpLb(codepoint);
    }

    /// <inheritdoc/>
    public bool IsExtendedPictographic(int codepoint)
    {
        SubstrateTextDecomposer.EnsureUcdLoaded();
        return TextDecomposeNative.UcdCpExtendedPictographic(codepoint) != 0;
    }

    /// <inheritdoc/>
    public int? SimpleCaseFold(int codepoint)
    {
        SubstrateTextDecomposer.EnsureUcdLoaded();
        int folded = TextDecomposeNative.UcdCpSimpleCaseFold(codepoint);
        return folded == codepoint ? null : folded;
    }

    /// <inheritdoc/>
    public unsafe ReadOnlySpan<int> FullCaseFold(int codepoint)
    {
        SubstrateTextDecomposer.EnsureUcdLoaded();
        int[] buffer = new int[FullCaseFoldMaxLen];
        int n;
        fixed (int* p = buffer)
        {
            n = TextDecomposeNative.UcdCpFullCaseFold(codepoint, p, FullCaseFoldMaxLen);
        }
        if (n <= 0 || n == 1)
        {
            // n == 1 is fold-to-self; callers asking for an expansion get
            // back empty so they fall through to SimpleCaseFold semantics.
            return ReadOnlySpan<int>.Empty;
        }
        return new ReadOnlySpan<int>(buffer, 0, n);
    }

    /// <inheritdoc/>
    public unsafe Point4D GetCodepointCentroid(int codepoint)
    {
        SubstrateTextDecomposer.EnsureUcdLoaded();
        Span<double> buf = stackalloc double[4];
        int rc;
        fixed (double* p = buf)
        {
            rc = TextDecomposeNative.UcdCpCentroid(codepoint, p);
        }
        if (rc != 0)
        {
            throw new InvalidOperationException(
                $"BlobUcdPropertyAccessor: no centroid for codepoint U+{codepoint:X4} (out of range or block not paged).");
        }
        return new Point4D(buf[0], buf[1], buf[2], buf[3]);
    }

    /// <inheritdoc/>
    public unsafe Hash32 GetCodepointHash(int codepoint)
    {
        SubstrateTextDecomposer.EnsureUcdLoaded();
        Span<byte> buf = stackalloc byte[Blake3.HashLen];
        int rc;
        fixed (byte* p = buf)
        {
            rc = TextDecomposeNative.UcdCpHash(codepoint, p);
        }
        if (rc != 0)
        {
            throw new InvalidOperationException(
                $"BlobUcdPropertyAccessor: no atom hash for codepoint U+{codepoint:X4} (out of range or block not paged).");
        }
        return new Hash32(buf);
    }

    /// <inheritdoc/>
    public unsafe bool IsCodepointAvailable(int codepoint)
    {
        if (codepoint < 0 || codepoint > 0x10FFFF)
        {
            return false;
        }
        SubstrateTextDecomposer.EnsureUcdLoaded();
        // Probe centroid availability: returns 0 on success, -1 if the
        // codepoint's Unicode block is not paged in (modular deploy).
        Span<double> buf = stackalloc double[4];
        fixed (double* p = buf)
        {
            return TextDecomposeNative.UcdCpCentroid(codepoint, p) == 0;
        }
    }
}
