using System;
using System.Runtime.InteropServices;

namespace Hartonomous.Core.Native;

/// <summary>
/// P/Invoke bindings for libhartonomous's in-process text decomposition.
/// Replaces the per-text Npgsql round-trip to substrate.text_decompose.
///
/// Same UAX#29 + BLAKE3 + 4D centroid algorithm as the PG-extension version
/// (both compile from the same C source); same UCD blob; byte-identical
/// hashes (Law #6).
///
/// Lifecycle: call <see cref="UcdLoad"/> once per process before any
/// <see cref="TextDecompose"/> call. The blob path is the directory
/// containing <c>hartonomous-ucd-17.0.0.idx</c>,
/// <c>hartonomous-ucd-17.0.0.reverse.bin</c>, and <c>blocks/</c>. Default
/// install path is <c>$share/extension/hartonomous-ucd/</c> on Linux,
/// or <c>ext/hartonomous_pg/src/generated/</c> in dev.
/// </summary>
public static partial class TextDecomposeNative
{
    public const int HashLen = 32;

    /* Record kinds (mirror hartonomous.h #define block). */
    public const int RecEntity         = 1;
    public const int RecClassification = 2;
    public const int RecPhysicality    = 3;
    public const int RecSequence       = 4;
    public const int RecSignificance   = 5;

    /* Entity-kind ints emitted by the native walk. The mapping to the
     * substrate.entity_type code is handled in C# (see SubstrateTextDecomposer). */
    public const int KindCodepoint        = 1;
    public const int KindGraphemeCluster  = 2;
    public const int KindWordForm         = 3;
    public const int KindTextComposition  = 9;

    public const int PhysS3Position = 1;
    public const int PhysContour    = 2;

    public const int SigSourceAuthority = 1;

    private const string Library = "hartonomous";

    /// <summary>
    /// Callback signature. Return 0 to continue; non-zero aborts the walk and
    /// is propagated as the function's return code.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int EmitCallback(IntPtr ctx, ref TextDecomposeRecord record);

#pragma warning disable CA1401 // P/Invoke method should not be visible
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_load",
        StringMarshalling = StringMarshalling.Utf8)]
    public static partial int UcdLoad(string dir);

    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_unload")]
    public static partial void UcdUnload();

    /// <summary>
    /// Returns 1 if <see cref="UcdLoad"/> has succeeded since the last unload,
    /// 0 otherwise. Cheap probe; does not touch the blob files.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_loaded_state")]
    public static partial int UcdLoadedState();

    /// <summary>
    /// Returns 1 when the loaded UCD atom catalog passes representative
    /// hash/centroid/reverse-lookup checks independent of PostgreSQL.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_catalog_ready")]
    public static partial int UcdCatalogReady();

    /// <summary>
    /// Returns 1 when libhartonomous has every generated UCD normalization and
    /// segmentation table required by native text decomposition linked in.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_tables_ready")]
    public static partial int UcdTablesReady();

    /// <summary>
    /// Copies the four S^3 centroid components for <paramref name="cp"/>
    /// into <paramref name="out4"/>. The centroid is
    /// <c>super_fibonacci_4d(uca_index[cp], 0x110000)</c> — UCA-collation-rank
    /// ordered, so case/accent pairs cluster on S^3.
    /// Returns 0 on success; -1 if the codepoint is out of range, the block
    /// file is missing, or <see cref="UcdLoad"/> was not called.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_cp_centroid")]
    public static unsafe partial int UcdCpCentroid(int cp, double* out4);

    /// <summary>
    /// Copies the 32-byte BLAKE3 atom hash for <paramref name="cp"/> into
    /// <paramref name="out32"/>. Returns 0 on success; -1 on failure.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_cp_hash")]
    public static unsafe partial int UcdCpHash(int cp, byte* out32);

    /// <summary>
    /// Returns the codepoint mapped to <paramref name="hash32"/>, or -1
    /// if not found. Hash bytes are taken from a 32-byte buffer.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_cp_from_hash")]
    public static unsafe partial int UcdCpFromHash(byte* hash32);

    /// <summary>UAX-#29 Grapheme_Cluster_Break property byte for <paramref name="cp"/>.</summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_cp_gcb")]
    public static partial byte UcdCpGcb(int cp);

    /// <summary>UAX-#29 Word_Break property byte for <paramref name="cp"/>.</summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_cp_wb")]
    public static partial byte UcdCpWb(int cp);

    /// <summary>UAX-#29 Sentence_Break property byte for <paramref name="cp"/>.</summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_cp_sb")]
    public static partial byte UcdCpSb(int cp);

    /// <summary>UAX-#14 Line_Break property byte for <paramref name="cp"/>.</summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_cp_lb")]
    public static partial byte UcdCpLb(int cp);

    /// <summary>UCD InCB property byte for <paramref name="cp"/> (UAX-#29 GB9c support).</summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_cp_incb")]
    public static partial byte UcdCpIncb(int cp);

    /// <summary>1 if <paramref name="cp"/> has Extended_Pictographic, 0 otherwise.</summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_cp_extended_pictographic")]
    public static partial int UcdCpExtendedPictographic(int cp);

    /// <summary>
    /// Simple case fold of <paramref name="cp"/>. Returns the codepoint
    /// itself when no folding applies. Equivalent to UCD's
    /// <c>Case_Folding.txt</c> 'C' status mapping.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_cp_simple_case_fold")]
    public static partial int UcdCpSimpleCaseFold(int cp);

    /// <summary>Simple lowercase mapping; returns <paramref name="cp"/> when no mapping applies.</summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_cp_simple_lowercase")]
    public static partial int UcdCpSimpleLowercase(int cp);

    /// <summary>Simple uppercase mapping; returns <paramref name="cp"/> when no mapping applies.</summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_cp_simple_uppercase")]
    public static partial int UcdCpSimpleUppercase(int cp);

    /// <summary>Simple titlecase mapping; returns <paramref name="cp"/> when no mapping applies.</summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_cp_simple_titlecase")]
    public static partial int UcdCpSimpleTitlecase(int cp);

    /// <summary>
    /// Full case fold expansion. Writes the expansion codepoints into
    /// <paramref name="outBuf"/> and returns the number written (>= 1 on
    /// success). Returns -1 if <paramref name="outBuf"/> is too small or
    /// <paramref name="cp"/> is out of range. Worst-case expansion in
    /// Unicode 17.0 is 4 codepoints; size buffers accordingly.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_cp_full_case_fold")]
    public static unsafe partial int UcdCpFullCaseFold(int cp, int* outBuf, int outMax);

    /// <summary>
    /// UCA primary-weight collation rank for <paramref name="cp"/> — the
    /// rank the substrate's S^3 Super-Fibonacci centroid is ordered by.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_ucd_cp_uca_index")]
    public static partial int UcdCpUcaIndex(int cp);

    /// <summary>
    /// Decompose UTF-8 bytes into the substrate's text DAG. Native walks the
    /// codepoint/grapheme/word/composition DAG and fires <paramref name="emit"/>
    /// once per record. <paramref name="outRootHash"/> receives the 32-byte
    /// composition hash on success. <paramref name="outRootCentroid"/> receives
    /// four doubles for the root composition centroid.
    ///
    /// Returns 0 on success; -1 null arg; -2 UcdLoad not called or failed;
    /// -3 zero-length input; -4 missing generated UCD tables; -9 allocation
    /// failure; or the callback's non-zero return.
    /// </summary>
    [DllImport(Library, EntryPoint = "hartonomous_text_decompose",
        CallingConvention = CallingConvention.Cdecl)]
    public static extern int TextDecompose(
        IntPtr utf8,
        nuint  utf8Len,
        int    topKind,
        double trustMu,
        EmitCallback emit,
        IntPtr ctx,
        IntPtr outRootHash,
        out int outRootKind,
        IntPtr outRootCentroid);

    /// <summary>
    /// Lightweight UAX-29 codepoint count for a UTF-8 buffer. NFC-normalized
    /// internally. Returns 0 on success; -1 null arg; -2 UCD not loaded.
    /// </summary>
    [DllImport(Library, EntryPoint = "hartonomous_text_codepoint_count",
        CallingConvention = CallingConvention.Cdecl)]
    public static extern int CodepointCount(
        IntPtr utf8,
        nuint  utf8Len,
        out int outCount);

    /// <summary>
    /// UAX-29 grapheme cluster boundaries. <c>outIndices</c> is a caller-owned
    /// int32 buffer; the function writes up to <c>outCapacity</c> entries and
    /// always sets <c>outCount</c> to the actual boundary count (re-call with a
    /// larger buffer if outCount &gt; outCapacity). Returns 0 on success; -1
    /// null arg; -2 UCD not loaded; -9 allocation failure.
    /// </summary>
    [DllImport(Library, EntryPoint = "hartonomous_text_grapheme_boundaries",
        CallingConvention = CallingConvention.Cdecl)]
    public static extern int GraphemeBoundaries(
        IntPtr utf8,
        nuint  utf8Len,
        IntPtr outIndices,
        int    outCapacity,
        out int outCount);

    /// <summary>
    /// UAX-29 word boundaries. Convention identical to <see cref="GraphemeBoundaries"/>.
    /// </summary>
    [DllImport(Library, EntryPoint = "hartonomous_text_word_boundaries",
        CallingConvention = CallingConvention.Cdecl)]
    public static extern int WordBoundaries(
        IntPtr utf8,
        nuint  utf8Len,
        IntPtr outIndices,
        int    outCapacity,
        out int outCount);

    /// <summary>
    /// UAX-29 sentence boundaries. Currently STUBBED in native (returns -3);
    /// C# fallback handles the algorithm pending native implementation.
    /// </summary>
    [DllImport(Library, EntryPoint = "hartonomous_text_sentence_boundaries",
        CallingConvention = CallingConvention.Cdecl)]
    public static extern int SentenceBoundaries(
        IntPtr utf8,
        nuint  utf8Len,
        IntPtr outIndices,
        int    outCapacity,
        out int outCount);

    /// <summary>
    /// 4D Hilbert curve index for a point (x, y, z, m). order = bit depth per
    /// axis; 16 → BIGINT-safe 64-bit index. Deterministic function of input.
    /// </summary>
    [DllImport("hartonomous", EntryPoint = "hartonomous_hilbert_index",
        CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong HilbertIndex(IntPtr point4d, int order);
#pragma warning restore CA1401
}
