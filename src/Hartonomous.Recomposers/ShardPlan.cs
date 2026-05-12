namespace Hartonomous.Recomposers;

public sealed record ShardPlan(int ShardIndex, int ShardCount, IReadOnlyList<string> TensorNames, long TotalBytes);
