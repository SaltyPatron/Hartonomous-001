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
    /// Native record passed to the emit callback. <c>HashA</c>, <c>HashB</c>,
    /// and <c>Wkb</c> are pointers into native-allocated buffers that are valid
    /// only for the duration of the callback — copy out anything you need.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Record
    {
        public int    Kind;
        public int    Subkind;
        public IntPtr HashA;
        public IntPtr HashB;
        public int    IntParam;
        public double DoubleParam;
        public IntPtr Wkb;
        public nuint  WkbLen;
    }

    /// <summary>
    /// Callback signature. Return 0 to continue; non-zero aborts the walk and
    /// is propagated as the function's return code.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int EmitCallback(IntPtr ctx, ref Record record);

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
#pragma warning restore CA1401
}
