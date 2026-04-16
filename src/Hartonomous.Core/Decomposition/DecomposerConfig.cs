namespace Hartonomous.Core.Decomposition;

public sealed class DecomposerConfig
{
    public required string SourceDirectory { get; init; }
    public int BatchSize { get; init; } = 10_000;
    public string ConnectionString { get; init; } = "Host=localhost;Port=5433;Database=hartonomous;Username=hartonomous;Password=hartonomous";
}
