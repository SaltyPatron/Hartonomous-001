namespace Hartonomous.Core.Recomposition;

public sealed record RecompositionOptions
{
    public int MaxDepth { get; init; } = int.MaxValue;
    public double SignificanceThreshold { get; init; }
    public string? ArenaFilter { get; init; }
    public bool IncludeProvenance { get; init; }
}
