using System.Collections.Generic;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// Snapshot of background-flush counters. The per-kind breakdown is keyed by
/// drain-function name (e.g. "substrate.drain_staging_entity_chunk") because
/// the worker discovers drain functions from pg_proc rather than hardcoding
/// them — adding a staging table does not require a code change here.
/// </summary>
public sealed record StagingFlushStats
{
    public long TotalRowsDrained { get; init; }
    public IReadOnlyDictionary<string, long> RowsDrainedByFunction { get; init; }
        = new Dictionary<string, long>();
    public long IdleCycles { get; init; }
}
