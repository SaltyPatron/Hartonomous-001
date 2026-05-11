using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// One substrate.entity row queued for flush. (entity_type_id, hash) is the
/// composite primary key — the pipeline COPYs straight into substrate.entity
/// with ON CONFLICT DO NOTHING; same content from any decomposer collapses
/// to one row.
/// </summary>
internal readonly record struct EntityEntry(Hash32 Hash, string EntityTypeCode);
