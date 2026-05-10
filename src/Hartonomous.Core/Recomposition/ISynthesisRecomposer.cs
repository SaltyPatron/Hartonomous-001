using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Recomposition;

/// <summary>
/// Two-mode synthesis recomposer per spec §VI Build-a-bear product slice.
/// The dispatch surface that walks a target tensor list (substrate-sourced
/// in Mode 1; user-spec-sourced in Mode 2), routes each tensor to the
/// matching <see cref="ILayerTypeSynthesizer"/>, and packages the result
/// into a safetensors file. Replaces the phantom-scatter logic in
/// <c>SafetensorsRecomposer.AssembleTensorBytesAsync</c>.
///
/// Per Phase C.0. Both modes share the per-layer-type synthesizer library
/// (Phase C.1, C.3); the only difference is the source-of-target-tensor-list
/// and the default source filter:
/// <list type="bullet">
/// <item><b>Mode 1 (re-export):</b> <see cref="RecomposeIngestedModelAsync"/>
/// reads the substrate's stored tree for the given <c>model_source_id</c>,
/// dispatches per-tensor with source filter restricted to that model's id.
/// "Llama-4-Maverick goes in, Llama-4-Maverick comes out, ready for HF
/// upload."</item>
/// <item><b>Mode 2 (build-a-bear):</b>
/// <see cref="RecomposeFromArchitectureSpecAsync"/> walks the user's target
/// architecture spec, dispatches per-tensor with optional source filter
/// (default: all ingested models contribute to consensus). Output's
/// model_id is content-addressed by the recipe (target arch spec +
/// recomposition options + substrate state hash). Same shape/form as the
/// chosen target, but better than any single source because cross-model
/// consensus tightens evidence.</item>
/// </list>
/// </summary>
public interface ISynthesisRecomposer
{
    /// <summary>
    /// Mode 1: re-export an ingested model from substrate state. Walks the
    /// stored tree for the given <c>model_source_id</c>, dispatches each
    /// tensor by role to the matching synthesizer with source filter =
    /// [modelSourceId]. The output is a NEW student model (content-addressed
    /// by recipe) whose layout matches the source — when the source filter
    /// restricts consensus to one model and that model has full attestation
    /// density, behavior round-trips.
    /// </summary>
    /// <param name="modelSourceId">substrate.entity_model_source.id of the
    /// model to re-export.</param>
    /// <param name="options">Recomposition options (arena codes, abstention
    /// threshold, dtype overrides, recipe id, etc.).</param>
    /// <param name="output">Stream to write the safetensors file to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Per-tensor coverage statistics for the recomposer's output
    /// safetensors header metadata.</returns>
    Task<RecompositionReport> RecomposeIngestedModelAsync(
        long modelSourceId,
        RecompositionOptions options,
        Stream output,
        CancellationToken ct);

    /// <summary>
    /// Mode 2: build-a-bear from a custom target architecture spec. Walks
    /// the spec's tensor list, dispatches each tensor by role to the matching
    /// synthesizer with optional source filter. Default source filter is null
    /// (all-consensus); user can restrict via
    /// <paramref name="sourceModelIds"/> for advanced provenance-restricted
    /// distillation. Output is a NEW student model whose model_id is
    /// content-addressed by (target arch spec + recomposition options +
    /// substrate state hash).
    /// </summary>
    Task<RecompositionReport> RecomposeFromArchitectureSpecAsync(
        TargetArchitectureSpec target,
        RecompositionOptions options,
        IReadOnlyList<long>? sourceModelIds,
        Stream output,
        CancellationToken ct);
}

/// <summary>
/// Per-recomposition aggregate statistics. Written to safetensors header
/// metadata for audit / coverage reporting.
/// </summary>
/// <param name="TensorCount">Number of tensors synthesized.</param>
/// <param name="TotalBytes">Total bytes of weight data written.</param>
/// <param name="MeanCoverage">Coverage averaged across all tensors.</param>
/// <param name="MinCoverage">Worst-coverage tensor's aggregate coverage.</param>
/// <param name="ZeroFractionMean">Mean fraction of cells that ended at exact
/// zero post-honest-abstention. Per Lottery Ticket Hypothesis baseline this
/// is typically 60-90% for transformer weights.</param>
/// <param name="ContributingSourceCount">Distinct model_source_ids that
/// contributed at least one attestation across the synthesis.</param>
/// <param name="PerTensorCoverage">Per-tensor (name → coverage) for header
/// metadata.</param>
public sealed record RecompositionReport(
    int TensorCount,
    long TotalBytes,
    double MeanCoverage,
    double MinCoverage,
    double ZeroFractionMean,
    int ContributingSourceCount,
    IReadOnlyDictionary<string, double> PerTensorCoverage);
