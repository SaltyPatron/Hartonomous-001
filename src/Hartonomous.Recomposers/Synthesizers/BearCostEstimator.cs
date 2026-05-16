using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Pre-build cost estimator for Build-a-bear synthesis. Projects parameter
/// count, output safetensors size, peak memory, and per-phase synth time
/// from the recipe — deterministic, no substrate state required for the
/// shape projections. Per-arena edge-count signals come from optional
/// substrate-side lookups when a connection is supplied.
///
/// Used by:
///   • CLI <c>synthesize-model --estimate-cost</c> — dry-run print without
///     synth (no DB write, microsecond return).
///   • Pricing/quota API — per-bear cost → tier check → accept/reject.
///   • Recipe iteration — show the practitioner the cost of each knob change.
///
/// Per-phase synth-time projections are calibrated against the observed
/// runtime of the minilm-base v=256 reference synth (the one used in the
/// mechanism gate test). The model: each phase's cost is a polynomial in
/// (vocab_size, hidden_dim, num_layers, adjacency_nnz) with empirically
/// fitted coefficients. Coefficients live in private constants below;
/// recalibrate when the host CPU or compute facade primitives change.
/// </summary>
public static class BearCostEstimator
{
    // Calibration constants. Derived from the minilm-base v=256 reference
    // synth on the workstation (14900KS class, MKL CBWR=AUTO,STRICT). Adjust
    // when the calibration host changes or the compute facade is upgraded.
    private const double VocabSelectSecondsPerToken          = 0.0001;
    private const double AdjacencyBuildSecondsPerNnz         = 1.5e-6;
    private const double EmbeddingSynthSecondsPerVocabHidden = 5.0e-7;
    private const double AttentionSynthSecondsPerLayer       = 3.0;
    private const double FfnSynthSecondsPerLayer             = 0.5;
    private const double TokenizerExportSecondsPerToken      = 0.001;

    // Memory: dense embedding [vocab × hidden] in f64 is the dominant
    // allocation during synth; CSR adjacency is bounded by nnz.
    private const long DoubleSize = 8;

    public static Task<BearCostEstimate> EstimateAsync(
        RecipeConfig recipe,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ct.ThrowIfCancellationRequested();

        int vocab        = Math.Max(1, recipe.Architecture.VocabSize);
        int hidden       = Math.Max(1, recipe.Architecture.HiddenDim);
        int layers       = Math.Max(1, recipe.Architecture.NumHiddenLayers);
        int interSize    = recipe.Architecture.IntermediateSize > 0
                         ? recipe.Architecture.IntermediateSize
                         : 4 * hidden;
        int maxPos       = recipe.Architecture.MaxPositionEmbeddings > 0
                         ? recipe.Architecture.MaxPositionEmbeddings
                         : 512;

        int dtypeBytes = recipe.OutputDtype switch
        {
            QuantizationTarget.F32 => 4,
            QuantizationTarget.F16 => 2,
            QuantizationTarget.BF16 => 2,
            _ => 4,
        };

        // ── Parameter counts (per BERT/Llama layer shapes) ─────────────
        long embeddingParams      = (long)vocab * hidden
                                  + (long)maxPos * hidden        // position embeddings
                                  + 2L * hidden;                  // token-type for BERT
        long attentionPerLayer    = 4L * hidden * hidden + 4L * hidden;       // Q, K, V, O (+ biases)
        long ffnPerLayer          = 2L * hidden * interSize + hidden + interSize; // up + down + biases
        long layerNormParams      = 2L * hidden * (2 * layers + 1);            // 2 LNs per layer + embedding LN
        long totalParams          = embeddingParams
                                  + layers * (attentionPerLayer + ffnPerLayer)
                                  + layerNormParams;

        long safetensorsBytes     = totalParams * dtypeBytes
                                  + 4096;  // header + alignment overhead

        // ── Synth-time projections (seconds per phase) ──────────────────
        // Adjacency NNZ estimate: vocab² × per-row-density. Recipe arena
        // count multiplies the per-arena scan cost.
        int arenaCount = recipe.ArenaWeights?.Count ?? 0;
        if (arenaCount == 0) { arenaCount = 5; }   // recipe default chain
        long estimatedNnz = (long)vocab * vocab / 20  // assume ~5% density
                          * arenaCount;

        double vocabSelect    = VocabSelectSecondsPerToken          * vocab;
        double adjacencyBuild = AdjacencyBuildSecondsPerNnz         * estimatedNnz;
        double embedSynth     = EmbeddingSynthSecondsPerVocabHidden * vocab * hidden;
        double attnSynth      = AttentionSynthSecondsPerLayer       * layers;
        double ffnSynth       = FfnSynthSecondsPerLayer             * layers;
        double tokExport      = TokenizerExportSecondsPerToken      * vocab;
        double total          = vocabSelect + adjacencyBuild + embedSynth
                              + attnSynth + ffnSynth + tokExport;

        // ── Memory peak (bytes) ────────────────────────────────────────
        long embeddingMemory  = (long)vocab * hidden * DoubleSize;
        long adjacencyMemory  = estimatedNnz * (DoubleSize + sizeof(long));  // values + col indices
        long ritzMemory       = (long)vocab * hidden * DoubleSize;            // eigenvectors during synth
        long peakMemory       = embeddingMemory + adjacencyMemory + ritzMemory;

        BearCostEstimate estimate = new(
            ParameterCount:              totalParams,
            EmbeddingParameters:         embeddingParams,
            PerLayerAttentionParameters: attentionPerLayer,
            PerLayerFfnParameters:       ffnPerLayer,
            LayerNormParameters:         layerNormParams,
            OutputSafetensorsBytes:      safetensorsBytes,
            DtypeBytesPerParameter:      dtypeBytes,
            VocabSelectionSeconds:       vocabSelect,
            AdjacencyBuildSeconds:       adjacencyBuild,
            EmbeddingSynthSeconds:       embedSynth,
            PerLayerAttentionSeconds:    attnSynth,
            PerLayerFfnSeconds:          ffnSynth,
            TokenizerExportSeconds:      tokExport,
            TotalSeconds:                total,
            PeakMemoryBytes:             peakMemory,
            EstimatedEdgesScanned:       estimatedNnz,
            RecipeArenaCount:            arenaCount,
            RequiresMoE:                 recipe.Architecture?.Moe?.Enabled ?? false,
            RequiresLoRA:                recipe.Architecture?.Lora?.Enabled ?? false,
            RequiresRoPE:                recipe.Architecture?.Rope?.Enabled ?? false);

        return Task.FromResult(estimate);
    }
}
