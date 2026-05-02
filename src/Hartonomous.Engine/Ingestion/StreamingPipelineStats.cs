namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// Snapshot of streaming-pipeline counters. All counts are records EMITTED
/// (= written to staging via COPY), not records DRAINED to substrate (the
/// background flush worker reports its own drain counts separately).
/// </summary>
public sealed record StreamingPipelineStats
{
    public long EntitiesEmitted { get; init; }
    public long EntityClassificationsEmitted { get; init; }
    public long EdgesEmitted { get; init; }
    public long EdgeMembersEmitted { get; init; }
    public long JunctionsEmitted { get; init; }
    public long PhysicalitiesEmitted { get; init; }
    public long SequencesEmitted { get; init; }
    public long EntitySignificancesEmitted { get; init; }
    public long EntityModelSourcesEmitted { get; init; }
    public long CopyCommits { get; init; }
    public long CopyErrors { get; init; }
}
