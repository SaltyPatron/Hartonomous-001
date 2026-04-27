using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// One row queued for the substrate.physicality table. Carries WKB bytes that
/// the pipeline INSERTs into the single <c>geom geometry(GeometryZM)</c>
/// column via <c>ST_GeomFromWKB</c>. The four supported PostGIS subtypes —
/// POINT, POINTZ, POINTZM, LINESTRING, LINESTRINGZ, LINESTRINGZM — all encode
/// natively as WKB; the per-partition CHECK constraints (migration 0048)
/// reject geometries that don't match the partition's declared dimensionality.
///
/// 4D physicality types (s3_position, hilbert_value, weight_distribution,
/// embedding_firefly, codec_codevector_position, contour) carry POINTZM /
/// LINESTRINGZM where the M coordinate is a real spatial axis. The substrate's
/// <c>substrate.st_4d_*</c> function family (migration 0049) provides
/// 4D-aware distance / centroid / Frechet / Hausdorff over those geometries;
/// PostGIS's <c>gist_geometry_ops_nd</c> opclass indexes all four dimensions.
/// </summary>
internal readonly record struct PhysicalityEntry(
    EntityHandle Entity,
    string PhysicalityTypeCode,
    byte[] Wkb);
