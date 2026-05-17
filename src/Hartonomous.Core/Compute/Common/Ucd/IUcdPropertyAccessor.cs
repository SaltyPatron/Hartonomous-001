using System;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Geometry;
using Hartonomous.Core.Text.Segmentation;

namespace Hartonomous.Core.Compute.Common.Ucd;

/// <summary>
/// Blob-backed UCD property reader. The single source of truth for every
/// codepoint-property read on the C# side — segmentation (UAX #14, UAX #29),
/// normalization, case folding, codepoint identity (hash + S³ centroid).
///
/// <para>
/// All reads route through libhartonomous's <c>huc_cp_*</c> exports against
/// the embedded UCD blob (<c>ext/libhartonomous/src/ucd_atoms_blob.c</c>),
/// mmap'd at process start and lazily paged per Unicode block. Cross-process
/// coherence with native <c>substrate.text_decompose</c> and PG-side
/// <c>pg_cp_*</c> is by construction: same blob, same baked values
/// (BLAKE3 + S³ Super-Fibonacci centroid pre-computed at codegen time).
/// </para>
///
/// <para>
/// Replaces <c>NpgsqlCodepointPropertiesCache</c> entirely. AP-7 compliance:
/// per-codepoint reads are O(1) blob lookups (no DB round-trip, no eager
/// 303,808-row preload). The 4D coordinate frame is shared with PG geometry
/// operations — every C# codepoint centroid is bit-identical to what
/// <c>substrate.pg_cp_centroid</c> returns for the same codepoint.
/// </para>
/// </summary>
public interface IUcdPropertyAccessor
{
    /// <summary>UAX #29 Grapheme_Cluster_Break property.</summary>
    GraphemeBreak GetGcb(int codepoint);

    /// <summary>UAX #29 Word_Break property.</summary>
    WordBreak GetWb(int codepoint);

    /// <summary>UAX #29 Sentence_Break property.</summary>
    SentenceBreak GetSb(int codepoint);

    /// <summary>UAX #14 Line_Break property.</summary>
    LineBreak GetLb(int codepoint);

    /// <summary>
    /// <c>Extended_Pictographic</c> binary property from <c>emoji-data.txt</c>.
    /// Drives UAX #29 GB11 (Extend ZWJ Extended_Pictographic).
    /// </summary>
    bool IsExtendedPictographic(int codepoint);

    /// <summary>
    /// Simple case fold mapping (single-codepoint). Returns <c>null</c> if no
    /// folding applies (the codepoint folds to itself).
    /// </summary>
    int? SimpleCaseFold(int codepoint);

    /// <summary>
    /// Full case fold mapping (multi-codepoint expansion, e.g. ß → s s).
    /// Returns the codepoint sequence in canonical UCD order, or an empty
    /// span if no expansion applies.
    /// </summary>
    ReadOnlySpan<int> FullCaseFold(int codepoint);

    /// <summary>
    /// The codepoint's 4D centroid — Super-Fibonacci S³ projection ordered by
    /// UCA collation rank, packed UCD bitmask on M. Pre-baked into the blob
    /// at codegen time; bit-identical to <c>substrate.pg_cp_centroid</c>.
    /// Required by every text-decomposer for grapheme / word / sentence
    /// trajectory emission (vertex = child codepoint centroid).
    /// </summary>
    Point4D GetCodepointCentroid(int codepoint);

    /// <summary>
    /// The codepoint's BLAKE3 content hash. Baked into the blob at codegen
    /// time; matches <c>Blake3.HashCodepoint(cp)</c> byte-for-byte. The
    /// universal identity of the tier-0 atom in the substrate's Merkle DAG.
    /// </summary>
    Hash32 GetCodepointHash(int codepoint);

    /// <summary>
    /// Whether the codepoint's Unicode block is present in the loaded blob.
    /// Returns <c>false</c> if the block is not deployed (modular deploy with
    /// only ASCII + Latin + practitioner's target scripts). Callers that
    /// receive <c>false</c> must abstain rather than fabricate property values.
    /// </summary>
    bool IsCodepointAvailable(int codepoint);

    /// <summary>UAX #44 General_Category enum code (0 = Cn ... 29 = Co).</summary>
    byte GetGeneralCategoryCode(int codepoint);

    /// <summary>UAX #44 Canonical_Combining_Class (0 = Not_Reordered).</summary>
    byte GetCanonicalCombiningClass(int codepoint);

    /// <summary>ISO 15924 script enum code (0 = Zzzz / Unknown).</summary>
    ushort GetScriptCode(int codepoint);

    /// <summary>Unicode block enum code (0 = No_Block).</summary>
    ushort GetBlockCode(int codepoint);

    /// <summary>UAX #9 Bidi_Class enum code (0 = L).</summary>
    byte GetBidiClassCode(int codepoint);

    /// <summary>UAX #11 East_Asian_Width enum code (0 = N).</summary>
    byte GetEastAsianWidthCode(int codepoint);

    /// <summary>Hangul_Syllable_Type enum code (0 = Not_Applicable).</summary>
    byte GetHangulSyllableType(int codepoint);

    /// <summary>Numeric_Type enum code (0 = None).</summary>
    byte GetNumericType(int codepoint);

    /// <summary>UCA primary-weight collation rank — the rank the S³
    /// Super-Fibonacci centroid is ordered by.</summary>
    int GetUcaIndex(int codepoint);

    /// <summary>Simple uppercase mapping. Returns codepoint itself if no mapping.</summary>
    int GetSimpleUppercase(int codepoint);

    /// <summary>Simple lowercase mapping. Returns codepoint itself if no mapping.</summary>
    int GetSimpleLowercase(int codepoint);

    /// <summary>Simple titlecase mapping. Returns codepoint itself if no mapping.</summary>
    int GetSimpleTitlecase(int codepoint);

    /// <summary>UAX #44 Decomposition_Type enum code (0 = None, 1 = Canonical,
    /// 2..17 = Compat variants per UAX #44 §5.7.3).</summary>
    byte GetDecompositionType(int codepoint);

    /// <summary>Canonical/compatibility decomposition mapping (NOT Hangul-
    /// expanded). Returns the decomposed codepoint sequence in canonical UCD
    /// order, or empty if no decomposition. Caller does NOT free.</summary>
    ReadOnlySpan<int> GetDecompositionMapping(int codepoint);
}
