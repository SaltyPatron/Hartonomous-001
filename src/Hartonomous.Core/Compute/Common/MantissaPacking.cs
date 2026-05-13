using System;

namespace Hartonomous.Core.Compute.Common;

/// <summary>
/// C# mirror of <c>substrate.bb_*</c> SQL functions. Provides bit-banged
/// encoding/decoding of <c>BIGINT</c> payloads into IEEE-754 double mantissas
/// for use as PostGIS LINESTRINGZM / MULTILINESTRINGZM vertex coordinates in
/// <c>ingestion_trajectory</c> physicality rows.
///
/// <para>
/// Encoding contract — round-trip determinism across C# (write side) and
/// SQL (read side, <c>substrate.bb_unpack_*</c>):
/// <code>
///   pack(value)   = 2^52 + (value &amp; Mask52)
///   unpack(double) = (long)(double - 2^52)
/// </code>
/// Both endpoints share the constant <c>2^52 = 4503599627370496.0</c>. The
/// double values produced lie in the integer-exact range <c>[2^52, 2^53)</c>;
/// PostGIS treats them as ordinary geometric coordinates while still letting
/// us recover the underlying 52-bit payload exactly.
/// </para>
///
/// <para>
/// Per-mantissa allocation in an <c>ingestion_trajectory</c> vertex:
/// <list type="bullet">
///   <item>X mantissa — child hash bits 0..51 via <see cref="PackHashLo"/>.</item>
///   <item>Y mantissa — (ordinal, RLE) bit-banged via <see cref="PackOrdinalRle"/>.</item>
///   <item>Z mantissa — child hash bits 52..103 via <see cref="PackHashHi"/>.</item>
///   <item>M mantissa — 52 bits of free-form metadata via <see cref="PackMetadata"/>.</item>
/// </list>
/// </para>
///
/// <para>
/// AP-9 reminder: the 52-bit hash slices the trajectory vertices carry are
/// content-derived from <c>Blake3.HashPrefix104</c>. Identity stays
/// content-addressed; the packing is geometric carriage, not an alternate
/// identifier system.
/// </para>
/// </summary>
public static class MantissaPacking
{
    /// <summary>
    /// Lower 52-bit mask. Identical to
    /// <see cref="Hartonomous.Core.Ingestion.TrajectoryVertex.Mask52"/>.
    /// </summary>
    public const long Mask52 = 0x000F_FFFF_FFFF_FFFFL;

    /// <summary>
    /// <c>2^52</c> — the base offset for the pack-into-mantissa encoding.
    /// Mirrors the constant in the <c>substrate.bb_*</c> SQL functions.
    /// </summary>
    public const double MantissaBaseValue = 4503599627370496.0;

    /// <summary>
    /// Pack a 52-bit BIGINT payload (child hash bits 0..51) into a double's
    /// mantissa. Inverse: <see cref="UnpackHashLo"/>.
    /// </summary>
    public static double PackHashLo(long bitsLo)
        => MantissaBaseValue + (double)(bitsLo & Mask52);

    /// <summary>Recover the 52-bit hash-lo payload.</summary>
    public static long UnpackHashLo(double mantissa)
        => (long)(mantissa - MantissaBaseValue);

    /// <summary>
    /// Pack a 52-bit BIGINT payload (child hash bits 52..103) into a double's
    /// mantissa. Same encoding as <see cref="PackHashLo"/>; the two functions
    /// share an encoding by design so PG / C# read either dimension uniformly.
    /// Inverse: <see cref="UnpackHashHi"/>.
    /// </summary>
    public static double PackHashHi(long bitsHi)
        => MantissaBaseValue + (double)(bitsHi & Mask52);

    /// <summary>Recover the 52-bit hash-hi payload.</summary>
    public static long UnpackHashHi(double mantissa)
        => (long)(mantissa - MantissaBaseValue);

    /// <summary>
    /// Pack (<paramref name="ordinal"/>, <paramref name="rle"/>) into a
    /// double's mantissa. Bit layout: ordinal in bits 0..31, RLE in
    /// bits 32..51.
    /// </summary>
    public static double PackOrdinalRle(int ordinal, int rle)
    {
        long payload =
              ((long)ordinal & 0xFFFF_FFFFL)
            | (((long)rle & 0xF_FFFFL) << 32);
        return MantissaBaseValue + (double)payload;
    }

    /// <summary>Recover the 32-bit ordinal from a packed (ordinal, RLE) mantissa.</summary>
    public static int UnpackOrdinal(double mantissa)
        => (int)((long)(mantissa - MantissaBaseValue) & 0xFFFF_FFFFL);

    /// <summary>Recover the 20-bit RLE run-length from a packed (ordinal, RLE) mantissa.</summary>
    public static int UnpackRle(double mantissa)
        => (int)(((long)(mantissa - MantissaBaseValue) >> 32) & 0xF_FFFFL);

    /// <summary>
    /// Pack 52 bits of free-form metadata into a double's mantissa. Same
    /// encoding as <see cref="PackHashLo"/>. Inverse: <see cref="UnpackMetadata"/>.
    /// </summary>
    public static double PackMetadata(long metadata)
        => MantissaBaseValue + (double)(metadata & Mask52);

    /// <summary>Recover the 52-bit metadata payload.</summary>
    public static long UnpackMetadata(double mantissa)
        => (long)(mantissa - MantissaBaseValue);
}
