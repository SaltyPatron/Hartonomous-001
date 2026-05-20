using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// One substrate.entity row queued for flush. Hash is the PK; same content
/// from any decomposer collapses to one row (ON CONFLICT DO NOTHING).
/// Identity only — no centroid, no hilbert, no geometry. Geometry lives
/// on substrate.physicality and is emitted via the AddPhysicality* surface.
/// </summary>
internal readonly record struct EntityEntry(
    Hash32 Hash,
    string EntityTypeCode);
