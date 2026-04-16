namespace Hartonomous.Core.Monitoring;

public sealed record ProgressSnapshot
{
    public required string DecomposerCode { get; init; }
    public required string CurrentPhase { get; init; }
    public long EntitiesCreated { get; init; }
    public long EdgesCreated { get; init; }
    public long DuplicatesSkipped { get; init; }
    public long BytesProcessed { get; init; }
    public string? CurrentFile { get; init; }
    public int? CurrentBatch { get; init; }
}
