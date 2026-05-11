using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One sign-bearing per-edge Glicko-2 rating event. Distinct from
/// <see cref="EdgeSignificanceRecord"/>: that primes a default mu via
/// INSERT...ON CONFLICT DO NOTHING (first-observation only); this fires
/// substrate.record_attestations_bulk on every emission, so cross-source /
/// cross-model corroboration accumulates on the SAME (arena, edge,
/// attestation_type) row.
///
/// Score in [0, 1] encodes sign — 1.0 = positive evidence (edge "won"
/// against the arena's neutral default opponent), 0.0 = negative,
/// 0.5 = ambiguous draw. Weight = magnitude of the underlying measurement
/// (|projection|, |response|, |cosine|); linearly scales the canonical Glicko
/// per-event delta inside the bulk SQL function.
///
/// Per docs/01-tensor-primitive-spec.md §V and AP-31. Decomposers that
/// extract sign-bearing measurements from tensor weights MUST emit this
/// record kind — sign-throwing (Math.Abs on the measurement before
/// emission) is the spec's primary banned anti-pattern for tensor
/// decomposition.
/// </summary>
public sealed record EdgeRatingEventRecord(
    string ContextTypeCode,
    string AttestationTypeCode,
    string EdgeTypeCode,
    Hash32 EdgeHash,
    double Score,
    double Weight) : IngestionRecord;
