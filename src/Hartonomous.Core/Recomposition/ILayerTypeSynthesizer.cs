using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Core.Recomposition;

/// <summary>
/// Per-layer-type tensor synthesizer — the reciprocal of
/// <c>ILayerTypeDecomposer</c> on the recomposer side. Each implementation
/// owns the exact closed-form synthesis math for one or more
/// <see cref="Hartonomous.Decomposers.Safetensors.TensorRole"/> values, reads
/// the corresponding attestation edges from substrate state, and produces a
/// row-major f64 weight matrix that the recomposer packs into the safetensors
/// output's wire dtype.
///
/// Per spec §VI Substrate Synthesis synthesis recomposer + docs/specs/recomposers/
/// algorithms/. The contract:
/// <list type="bullet">
/// <item>Exact math, no approximation. SVD / linear-system / centroid-aggregate
/// closed-form solutions only — no iterative / probabilistic fallbacks.
/// Per Law #6 + AP-11.</item>
/// <item>Honest abstention: under-attested cells stay at exact zero. Coverage
/// statistics returned in <see cref="SynthesisResult"/>.Coverage for the
/// recomposer's safetensors header metadata.</item>
/// <item>Source-filter aware: when <c>SynthesisContext.SourceModelIds</c> is
/// non-null, restrict consensus contribution to those source models only
/// (Mode 1 single-source round-trip). When null, the default unfiltered
/// consensus aggregates every ingested model with attestations on the target
/// tensor's role (Mode 2 cross-model consensus).</item>
/// </list>
///
/// Working template for synthesizers: <c>AttentionSynthesizer</c>
/// (reciprocal of <see cref="Hartonomous.Decomposers.Safetensors.Passes.AttentionBlockTuplePass"/>).
///
/// Per Phase C.0 of the implementation plan.
/// </summary>
public interface ILayerTypeSynthesizer
{
    /// <summary>
    /// Tensor role codes this synthesizer can produce — string identifiers
    /// derived from the <c>(PrimitiveKind, ArchetypeTuple, TupleSlot)</c>
    /// triple per docs/01-tensor-primitive-spec.md §I-§II (e.g. the
    /// <c>TupleSlot</c> enum value as its lowercase name, or composite codes
    /// like "attention_q" / "ffn_down" / "embedding_table"). The recomposer's
    /// dispatch table routes target tensors with matching role code to this
    /// synthesizer. String-keyed (rather than the C# enum) because
    /// Hartonomous.Core does not reference Hartonomous.Decomposers — the
    /// synthesizer surface is modality-agnostic infrastructure.
    /// </summary>
    /// <example>
    /// AttentionSynthesizer.TargetRoleCodes = ["attention_q", "attention_k", "attention_v", "attention_o"].
    /// FfnSynthesizer.TargetRoleCodes = ["ffn_gate", "ffn_up", "ffn_down"].
    /// EmbeddingSynthesizer.TargetRoleCodes = ["embedding_table", "position_table",
    ///     "position_table_2d", "token_type_table"].
    /// </example>
    IReadOnlyList<string> TargetRoleCodes { get; }

    /// <summary>
    /// The attestation_type code (per <c>sql/schema/seed/attestation_type.sql</c>)
    /// this synthesizer reads from substrate. Pairs with
    /// <c>ILayerTypeDecomposer.AttestationTypeCode</c> on the ingestion side.
    /// </summary>
    string AttestationTypeCode { get; }

    /// <summary>
    /// The edge_type code this synthesizer reads attestations from. Pairs
    /// with <c>ILayerTypeDecomposer.EmittedEdgeTypeCode</c>.
    /// </summary>
    string SourceEdgeTypeCode { get; }

    /// <summary>
    /// Synthesize one tensor of the target architecture.
    /// </summary>
    /// <param name="targetTensor">Specification of the target tensor: role,
    /// dtype, shape, layer/head/expert metadata for source filtering.</param>
    /// <param name="context">Substrate query interfaces, source filter,
    /// arena weighting, abstention threshold, recipe ID, target model
    /// architecture spec.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SynthesisResult> SynthesizeAsync(
        TargetTensorSpec targetTensor,
        SynthesisContext context,
        CancellationToken ct);
}
