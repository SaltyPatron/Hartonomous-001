using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// One substrate.entity row queued for flush. Hash is the PK; same content
/// from any decomposer collapses to one row (ON CONFLICT DO NOTHING).
///
/// Centroid + Hilbert are pre-computed by the producer (native text decomposer
/// emits per-tier centroids in cp_c / gc_c / w_c / comp_c; the EmitCallback
/// surfaces them as record.centroid; SubstrateTextDecomposer's OnRecord
/// passes them to AddEntity). NaN sentinels mean "producer did not provide";
/// the drain treats those as NULL in the COPY stream and the
/// substrate.update_entity_centroid_from_physicality trigger (when enabled)
/// fills them reactively after physicality INSERT.
/// </summary>
internal readonly record struct EntityEntry(
    Hash32 Hash,
    string EntityTypeCode,
    double CentroidX,
    double CentroidY,
    double CentroidZ,
    double CentroidM,
    long?  HilbertIndex)
{
    public EntityEntry(Hash32 hash, string entityTypeCode)
        : this(hash, entityTypeCode, double.NaN, double.NaN, double.NaN, double.NaN, null)
    {
    }

    public bool HasCentroid =>
        !double.IsNaN(CentroidX) && !double.IsNaN(CentroidY)
        && !double.IsNaN(CentroidZ) && !double.IsNaN(CentroidM);
}
