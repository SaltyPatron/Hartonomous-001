using System.Collections.Generic;

namespace Hartonomous.Engine.Godel;

/// <summary>
/// One sub-question's full Observe→Orient→Decide→Act trace. The Gödel
/// Engine's Act phase synthesizes a final response by selecting / ordering
/// these per-sub-question results.
///
/// Confidence: the highest-mu candidate's mu, scaled by Self-Consistency
/// (PathCount on the winner). Used by the Reflexion loop to decide whether
/// to retry with a broader strategy. RetryCount is the number of Reflexion
/// passes already burned.
/// </summary>
public sealed record SubQuestionResult(
    SubQuestion SubQuestion,
    PromptIntent Intent,
    byte[] PromptHash,
    int SeedCount,
    long DistinctTargets,
    IReadOnlyList<GodelCandidate> Candidates,
    int RetryCount,
    double Confidence,
    int ElapsedMs);
