namespace Hartonomous.Engine.Godel;

/// <summary>
/// One candidate target produced by a sub-question's forward pass. Rank is
/// by max-pooled mu (best first); PathCount is how many independent
/// (seed × arena) traversals reached the target — the Self-Consistency
/// signal. RecomposedText is the substrate-side reconstruction of the
/// target via substrate.recompose_text — a real walk through substrate
/// content, not a sampled string.
/// </summary>
public sealed record GodelCandidate(
    int Rank,
    byte[] TargetHash,
    double TotalMu,
    long PathCount,
    string RecomposedText);
