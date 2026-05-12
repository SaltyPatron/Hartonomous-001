namespace Hartonomous.Engine.Inference;

internal sealed record SubstrateInferOutput(
    string? AnswerText,
    int SeedCount,
    long DistinctTargets,
    byte[]? BestTargetHash,
    double BestTotalMu,
    int ElapsedMs);
