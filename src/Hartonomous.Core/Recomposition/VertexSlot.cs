namespace Hartonomous.Core.Recomposition;

/// <summary>
/// One parsed vertex slot from a composition LINESTRINGZM physicality
/// geometry. Produced by <see cref="BulkTierContentWalk.ParsePackedLineString(byte[])"/>
/// from the EWKB byte stream emitted by
/// <see cref="Hartonomous.Core.Compute.Common.Geometry4dPayloadBuilder.LineString"/>.
///
/// <para>
/// The vertex stream is the mantissa-packed indexed child manifest: (X, Z) carry
/// the child entity's BLAKE3 hash prefix (low 52 bits, high 52 bits), (Y) carries
/// <c>bb_pack_ordinal_rle(ordinal, rle_count)</c>, (M) is reserved metadata. The
/// geometry IS the child manifest — no separate <c>substrate.sequence</c> table.
/// </para>
/// </summary>
public readonly record struct VertexSlot(
    long HashLow,
    long HashHi,
    int Ordinal,
    int Rle,
    int VertexIndex);
