using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Decomposers.Safetensors.Passes.Internal;

/// <summary>
/// Substrate-write half of every Track 2 transformation tuple pass. Takes
/// the sign-bearing cells produced by an <see cref="ITensorSignalExtractor"/>
/// and emits typed attestation edges through the pass session — one edge per
/// cell, with sign-aware Glicko events firing on the per-arena
/// <c>edge_significance</c> rows the orchestrator's arena-priming sweep
/// established.
///
/// <para>
/// Per AP-25 / AP-32: emission produces ATTESTATION EDGES between existing
/// content entities (typically two <c>word_form</c> tokens), never per-role
/// phantom entities. The <c>edge_type_id</c> and <c>attestation_type_id</c>
/// stratify cross-model corroboration so two models attesting the same
/// edge identity accumulate distinct rating events on one edge row instead
/// of fragmenting into per-source debris.
/// </para>
/// </summary>
public interface IAttestationEmitter
{
    /// <summary>
    /// Stable identifier for this emitter. Format
    /// <c>"emitter.{tuple}.{attestation_type}"</c> (e.g.
    /// <c>"emitter.attention.qk_pattern"</c>).
    /// </summary>
    string EmitterId { get; }

    /// <summary>
    /// The substrate <c>edge_type</c> code edges emitted by this emitter
    /// carry (e.g. <c>"model_attention_pattern"</c>,
    /// <c>"model_ffn_full_path"</c>, <c>"model_concept_similarity"</c>).
    /// </summary>
    string EdgeTypeCode { get; }

    /// <summary>
    /// The substrate <c>attestation_type</c> code Glicko events fire under
    /// (e.g. <c>"model_attention_qk_pattern"</c>,
    /// <c>"model_ffn_full_path"</c>).
    /// </summary>
    string AttestationTypeCode { get; }

    /// <summary>
    /// Emit substrate attestation edges for the supplied
    /// <paramref name="cells"/> against the open
    /// <paramref name="session"/>'s ingestion batch. Implementations must
    /// honor sign-aware Glicko events (AP-31) and never collapse cells into
    /// phantom per-role entities (AP-25).
    /// </summary>
    Task EmitAsync(
        IReadOnlyList<TensorSignalCell> cells,
        IPassSession session,
        CancellationToken ct);
}
