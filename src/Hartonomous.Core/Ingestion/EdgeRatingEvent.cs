namespace Hartonomous.Core.Ingestion;

/// <summary>
/// Sign-bearing per-edge Glicko-2 attestation event. The producer-side
/// rating surface for "this evidence supports / opposes this edge with this
/// magnitude" — per docs/01-tensor-primitive-spec.md §V and AP-31 in
/// .claude/rules/45-anti-patterns.md.
///
/// Score is the Glicko-2 outcome convention: 1.0 = positive evidence
/// ("the edge won against the default-neutral opponent"), 0.0 = negative
/// evidence ("the edge lost"), 0.5 = ambiguous draw.
///
/// Weight scales the per-event effect on mu and sigma — magnitude of the
/// measured signal (|projection|, |response|, |cosine|). The pipeline calls
/// substrate.record_attestation per event; the SQL surface clamps weight to
/// [0, 1024] and runs the Glicko bulk-update with weight rounds against the
/// arena's neutral default.
///
/// Distinct from <see cref="EdgeSignificanceSpec"/>: that one prime-sets an
/// initial mu via INSERT...ON CONFLICT DO NOTHING and never fires after the
/// first observation. EdgeRatingEvent FIRES every time, so cross-model
/// corroboration accumulates on the same (arena, edge, attestation_type) row.
/// Both surfaces coexist — the spec carries the prior, the event carries the
/// observation.
/// </summary>
public readonly record struct EdgeRatingEvent(
    string ContextTypeCode,
    string AttestationTypeCode,
    double Score,
    double Weight);
