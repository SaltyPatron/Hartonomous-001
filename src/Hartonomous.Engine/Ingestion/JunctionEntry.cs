using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// One row for a junction table. The pipeline routes by junction-table name
/// and writes (entity_type_id, entity_hash, reference_id [, mu]) directly.
/// </summary>
internal readonly record struct JunctionEntry(
    string JunctionTable,
    EntityHandle Entity,
    int ReferenceId,
    double? Mu);
