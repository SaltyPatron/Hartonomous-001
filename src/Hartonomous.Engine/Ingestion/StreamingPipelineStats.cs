namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// Snapshot of streaming-pipeline counters. All counts are records EMITTED
/// by producers and drained directly into substrate core tables via
/// session-local temp staging. There is no separate background flush worker
/// — drain happens inline within the same chunk that COPY-loaded the temp
/// table, before the next chunk reads from the channel.
/// </summary>
public sealed record StreamingPipelineStats
{
    public long EntitiesEmitted { get; init; }
    public long EntityClassificationsEmitted { get; init; }
    public long EdgesEmitted { get; init; }
    public long EdgeMembersEmitted { get; init; }
    public long JunctionsEmitted { get; init; }
    public long PhysicalitiesEmitted { get; init; }
    public long EntitySignificancesEmitted { get; init; }
    public long EdgeSignificancesEmitted { get; init; }
    public long EntityModelSourcesEmitted { get; init; }
    public long CopyCommits { get; init; }
    public long CopyErrors { get; init; }
}
