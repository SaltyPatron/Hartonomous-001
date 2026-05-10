using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// One substrate.edge row plus its members. The pipeline computes the edge
/// hash from (edge_type_id, role-ordered participant hashes) at flush, then
/// writes the edge row + edge_member rows in one transaction. Members carry
/// EntityHandle directly — no surrogate-id resolve step.
///
/// <para><see cref="SignificanceOverrides"/> carries producer-calibrated
/// initial Glicko-2 mu values per arena. The pipeline's edge-significance
/// emission loop consults this map first and falls back to the provenance
/// default for arenas not covered. Empty (the common path) means every
/// arena uses the provenance default.</para>
///
/// <para><see cref="RatingEvents"/> carries sign-bearing per-edge Glicko-2
/// events (score in [0, 1], weight = magnitude of the underlying measurement).
/// Each event is buffered into the rating-event channel at flush and drained
/// in bulk via substrate.record_attestations_bulk per (arena, attestation_type)
/// chunk. Per docs/01-tensor-primitive-spec.md §V and AP-31. Empty = no rating
/// events fired (legacy prime-only emission); populated = sign-bearing
/// observation that ALWAYS fires (cross-model accumulation), distinct from
/// the SignificanceOverrides prime-on-conflict default.</para>
/// </summary>
internal readonly record struct EdgeEntry(
    string EdgeTypeCode,
    string ProvenanceCode,
    EdgeMemberSpec[] Members,
    EdgeSignificanceSpec[] SignificanceOverrides,
    EdgeRatingEvent[] RatingEvents);
