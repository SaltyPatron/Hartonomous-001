namespace Hartonomous.Core.Decomposition;

public sealed class DecomposerConfig
{
    public required string SourceDirectory { get; init; }
    public int BatchSize { get; init; } = 100_000;
    public required string ConnectionString { get; init; }
}
