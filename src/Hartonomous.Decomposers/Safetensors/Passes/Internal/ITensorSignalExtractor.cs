using System;
using System.Collections.Generic;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Decomposers.Safetensors.Passes.Internal;

/// <summary>
/// Tuple-pass signal-extraction contract. The math half of every Track 2
/// transformation tuple pass (attention QK / VO, FFN factorization, embedding
/// firefly, LoRA delta). Reads tensor values directly (per AP-34: weights ARE
/// the activation pattern; no synthetic prompts), applies the per-tuple
/// projection math, returns sign-bearing <see cref="TensorSignalCell"/>
/// values for every cell above the per-tensor adaptive noise floor.
///
/// <para>
/// Thresholding is per-tensor adaptive only (Han et al. 2015 / Lottery Ticket
/// Hypothesis). No top-K truncation — every cell above floor is emitted; every
/// cell below is gradient-descent jitter and produces nothing (honest
/// non-storage, AP-33). Sign is preserved on the <see cref="TensorSignalCell.Score"/>
/// for downstream Glicko-2 event emission (AP-31).
/// </para>
/// </summary>
public interface ITensorSignalExtractor
{
    /// <summary>
    /// Stable identifier for this extractor. Used by the orchestrator for
    /// dispatch and by checkpointing. Format <c>"extractor.{tuple}.{slot}"</c>
    /// (e.g. <c>"extractor.attention.qk"</c>).
    /// </summary>
    string ExtractorId { get; }

    /// <summary>
    /// Apply the per-tuple math to <paramref name="tensor"/> values, threshold
    /// against the adaptive noise floor, and return the sign-bearing cells
    /// that survive. Caller is responsible for content-decoding the tensor to
    /// f64 first; the extractor operates on raw values.
    /// </summary>
    IReadOnlyList<TensorSignalCell> Extract(
        ReadOnlySpan<double> tensorValues,
        TensorSignalExtractionParameters parameters);
}
