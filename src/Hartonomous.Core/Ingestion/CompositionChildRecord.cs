using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// Composition metadata for a physicality-backed parent trajectory. It records
/// which child hash starts at which 1-based ordinal and how many contiguous
/// positions that child occupies.
/// </summary>
public sealed record CompositionChildRecord(
    Hash32 ParentEntityHash,
    int Ordinal,
    Hash32 ChildEntityHash,
    int RleCount = 1) : IngestionRecord;
