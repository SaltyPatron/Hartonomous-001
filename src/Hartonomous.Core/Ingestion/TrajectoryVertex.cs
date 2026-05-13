using System;
using System.Buffers.Binary;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One mantissa-packed vertex of an <c>ingestion_trajectory</c> LINESTRINGZM /
/// MULTILINESTRINGZM. Each vertex carries 4 × 52-bit payloads (the explicit
/// mantissa of an IEEE-754 double; the 53rd bit is the implicit leading one
/// that the fixed exponent supplies) = 208 usable bits of structured data,
/// not metric coordinates. PostGIS still treats the result as geometry (R-tree
/// on bounding box, GiST, Fréchet, Hausdorff), but the geometric operators
/// carry STRUCTURAL meaning on the packed encoding (X+Z = child identity
/// sequence; Y = ordinal pattern; M = metadata).
///
/// <para>
/// Packing layout (mirrors <c>substrate.bb_*</c> SQL functions byte-for-byte;
/// <see cref="Hartonomous.Core.Compute.Common.MantissaPacking"/> is the
/// canonical C# encoder). All 52-bit payloads pack into a double via
/// <c>2^52 + value</c> — the result is exactly representable in IEEE-754
/// (both PG and C# encode/decode without rounding via simple addition /
/// subtraction):
/// <list type="bullet">
/// <item><see cref="ChildHashLo"/> — bits 0..51 of the child BLAKE3 hash
///   (little-endian unpack of bytes 0..7 / first 52 bits). Packs into the
///   vertex's <c>X</c> mantissa.</item>
/// <item><see cref="Ordinal"/> + <see cref="Rle"/> — ordinal in bits 0..31,
///   RLE run-length in bits 32..51 (20 bits), bit-banged into the vertex's
///   <c>Y</c> mantissa.</item>
/// <item><see cref="ChildHashHi"/> — bits 52..103 of the child BLAKE3 hash.
///   Packs into the vertex's <c>Z</c> mantissa. Combined with
///   <see cref="ChildHashLo"/> this is a 104-bit hash prefix — birthday
///   collision at ~2^52 ≈ 5 × 10^15 entities.</item>
/// <item><see cref="Metadata"/> — 52 free bits for attestation type, role
///   flag, edge discriminator, sub-tier flag (caller's choice). Packs into
///   the vertex's <c>M</c> mantissa.</item>
/// </list>
/// </para>
///
/// <para>
/// Reconstruction reads each vertex's (ChildHashLo, ChildHashHi) and JOINs
/// against the <c>(hash_bits_0_51, hash_bits_52_103)</c> composite btree on
/// <c>substrate.entity</c> — one batched btree point lookup per tier walk.
/// No GiST k-NN, no reverse-spatial lookup, no Hilbert indirection.
/// </para>
/// </summary>
public readonly record struct TrajectoryVertex(
    long ChildHashLo,
    long ChildHashHi,
    int Ordinal,
    int Rle,
    long Metadata)
{
    /// <summary>
    /// Lower 52-bit mask. Each mantissa carries a value in <c>[0, 2^52)</c>,
    /// packed into the double as <c>2^52 + value</c> (an integer in
    /// <c>[2^52, 2^53)</c>, exactly representable in IEEE-754). Defined here
    /// so both the struct and the
    /// <see cref="Hartonomous.Core.Compute.Common.MantissaPacking"/> helper
    /// can reference the same constant.
    /// </summary>
    public const long Mask52 = 0x000F_FFFF_FFFF_FFFFL;

    /// <summary>
    /// The constant <c>2^52</c>. Packing a 52-bit payload into a double is
    /// <c>2^52 + value</c>; unpacking is <c>(long)(double - 2^52)</c>. Both
    /// PG (<c>bb_pack_*</c> / <c>bb_unpack_*</c>) and C# share this constant
    /// for round-trip determinism.
    /// </summary>
    public const double MantissaBaseValue = 4503599627370496.0; // 2^52

    /// <summary>
    /// Build a vertex from the child's BLAKE3 hash + ordinal + RLE +
    /// metadata. Splits the hash into 52-bit lo/hi slices (little-endian byte
    /// order, matching the substrate's <c>bb_hash_lo(bytea)</c> /
    /// <c>bb_hash_hi(bytea)</c> SQL functions byte-for-byte).
    /// </summary>
    /// <param name="childHash">The child entity's BLAKE3 hash.</param>
    /// <param name="ordinal">1-based ordinal position; must fit in 32 bits.</param>
    /// <param name="rle">RLE run-length; must fit in 20 bits.</param>
    /// <param name="metadata">52-bit free-form metadata payload.</param>
    public static TrajectoryVertex FromHash(
        Hash32 childHash,
        int ordinal,
        int rle,
        long metadata)
    {
        Span<byte> bytes = stackalloc byte[Hash32.Length];
        childHash.CopyTo(bytes);
        long lo = (long)(BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(0, 8)) & unchecked((ulong)Mask52));
        long hi = (long)((BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(6, 8)) >> 4) & unchecked((ulong)Mask52));
        return new TrajectoryVertex(lo, hi, ordinal, rle, metadata & Mask52);
    }
}
