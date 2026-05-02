using System;
using System.Collections.Generic;

namespace Hartonomous.Engine.Godel;

/// <summary>
/// The Gödel Engine's structured response to a prompt. Auditable end to end:
///
///   * <see cref="PrimaryAnswer"/> is the Act phase's synthesized output —
///     either a single recomposed target or a structured concatenation
///     across sub-questions.
///   * <see cref="SubQuestionResults"/> retains every sub-question's full
///     candidate list with mu / PathCount / recomposed text so the caller
///     can show alternates, ambiguities, or sources.
///   * <see cref="Abstained"/> is true when no candidate exceeded the
///     confidence floor — honest abstention, not fabrication.
///   * <see cref="ConfidenceFloor"/> records the threshold the engine
///     applied; useful for calibration when the OutcomeRecorder feeds
///     back accept/reject signals.
///   * <see cref="ReasoningTrace"/> is a free-form string the Gödel layer
///     fills with its OODA decisions (which arena weighting, which
///     decomposition, which retries) so a user can audit *why* the engine
///     picked what it did.
/// </summary>
public sealed record GodelResponse
{
    public required string PrimaryAnswer { get; init; }
    public required IReadOnlyList<SubQuestionResult> SubQuestionResults { get; init; }
    public required bool Abstained { get; init; }
    public required double ConfidenceFloor { get; init; }
    public required string ReasoningTrace { get; init; }
    public required TimeSpan TotalElapsed { get; init; }
}
