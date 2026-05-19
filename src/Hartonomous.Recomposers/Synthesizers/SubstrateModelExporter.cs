using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Orchestrates a substrate-derived export of a target architecture as
/// standard safetensors + config.json + tokenizer.json into a directory
/// loadable by HuggingFace transformers (and downstream by llama.cpp's
/// convert-hf-to-gguf.py).
///
/// Pipeline:
///   1. <see cref="VocabSelector"/> — pick top-N word_form entities by
///      substrate edge_count (already deterministic).
///   2. <see cref="SubstrateAdjacencyBuilder"/> — build a sparse V×V
///      adjacency over selected vocab from substrate.edge_significance.mu,
///      arena-weighted per <see cref="RecompositionOptions.ArenaWeights"/>.
///   3. <see cref="EmbeddingSynthesizer"/> — Laplacian eigenmap of W via
///      <see cref="Hartonomous.Core.Compute.Ingestion.LaplacianEigenmap"/>;
///      top hidden_dim non-trivial eigenvectors → [vocab × hidden] embedding.
///   4. For each layer:
///      <see cref="AttentionSynthesizer"/> — Ritz pairs of W →
///        per-head Q/K/V/O projection matrices.
///      <see cref="FfnSynthesizer"/> — Ritz pairs of W →
///        gate/up/down memory slots.
///   5. <see cref="ConfigEmitter"/>, <see cref="TokenizerExporter"/>,
///      audit JSON.
///
/// Every numerical step routes through the Compute facade
/// (<see cref="Hartonomous.Core.Compute.Ingestion"/> — Spectra/Eigen/MKL).
/// No imports of MKL.NET / Eigen.NET / OnnxRuntime outside the facade
/// per <see cref="Hartonomous.Core.Compute.ComputeFacade"/>.
/// </summary>
public static class SubstrateModelExporter
{
    public static async Task ExportAsync(
        NpgsqlDataSource dataSource,
        TargetArchitectureSpec arch,
        RecompositionOptions options,
        string outputDir,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(arch);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(outputDir);

        Directory.CreateDirectory(outputDir);

        // 1. Pick vocab. If the recipe supplies seed concepts, use the
        //    knowledge-selection BFS (the Build-a-bear product mechanism —
        //    domain-targeted vocab from user-chosen concepts). Otherwise
        //    fall back to the legacy top-by-edge-degree VocabSelector.
        IReadOnlyList<VocabToken> vocab;
        if (!options.SeedConcepts.IsDefaultOrEmpty)
        {
            // Recipe arena weights drive the BFS edge weighting. Empty weights
            // means equal-weight across all arenas in significance_context.
            Dictionary<string, double> arenaWeights = options.ArenaWeights.IsEmpty
                ? new Dictionary<string, double>(StringComparer.Ordinal)
                : new Dictionary<string, double>(options.ArenaWeights, StringComparer.Ordinal);
            vocab = await KnowledgeSelector.SelectFromConceptsAsync(
                dataSource, options.SeedConcepts, arenaWeights,
                arch.VocabSize, options.KnowledgeBfsTopK, ct).ConfigureAwait(false);
            Console.Out.WriteLine($"KnowledgeSelector: seeds={options.SeedConcepts.Length} → vocab={vocab.Count} (BFS over arena-weighted edge_member)");
        }
        else
        {
            vocab = await VocabSelector.SelectAsync(
                dataSource, arch.VocabSize, ct).ConfigureAwait(false);
            Console.Out.WriteLine($"VocabSelector (legacy top-by-edge-degree): {vocab.Count} word_form entities selected.");
        }

        // 2. Build substrate adjacency over selected vocab.
        SubstrateAdjacency adj = await SubstrateAdjacencyBuilder.BuildAsync(
            dataSource, vocab, options, ct).ConfigureAwait(false);
        Console.Out.WriteLine(SubstrateAdjacencyBuilder.DebugCsrSummary(adj));

        // 3. Synthesize embedding via Laplacian eigenmap.
        TensorData embedTokens = EmbeddingSynthesizer.Synthesize(adj, vocab, arch.HiddenDim, options);
        float[] embeddingF32 = TensorPacker.UnpackToF32(embedTokens);
        Console.Out.WriteLine($"EmbeddingSynthesizer: [{arch.VocabSize} × {arch.HiddenDim}] derived.");

        // 4. Per-layer attention + FFN.
        Dictionary<string, TensorData> tensors = new(StringComparer.Ordinal);
        int substrateAttentionLayers = 0;
        int substrateFfnLayers = 0;
        (substrateAttentionLayers, substrateFfnLayers) = await BuildTensorSetAsync(
            dataSource, vocab, arch, options, embedTokens, embeddingF32, adj,
            tensors, ct).ConfigureAwait(false);

        Console.Out.WriteLine(
            $"Per-layer synth: attention substrate-derived={substrateAttentionLayers}/{arch.NumHiddenLayers}, "
            + $"FFN substrate-derived={substrateFfnLayers}/{arch.NumHiddenLayers}");

        // 5. Write safetensors.
        SafetensorsFile file = new(tensors, $"hartonomous-{arch.Architecture.ToLowerInvariant()}");
        Dictionary<string, string> auditMetadata = new(StringComparer.Ordinal)
        {
            ["hartonomous_recomposer_version"] = "v2-substrate-spectral",
            ["hartonomous_architecture"] = arch.Architecture,
            ["hartonomous_vocab_size"] = arch.VocabSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["hartonomous_hidden_dim"] = arch.HiddenDim.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["hartonomous_num_layers"] = arch.NumHiddenLayers.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["hartonomous_quantization"] = options.OutputDtype.ToString(),
            ["hartonomous_adj_nnz"] = adj.Nnz.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["hartonomous_adj_non_isolated"] = adj.NonIsolatedNodes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["hartonomous_recipe"] = SerializeRecipe(options),
        };

        string safetensorsPath = Path.Combine(outputDir, "model.safetensors");
        await using (FileStream fs = File.Create(safetensorsPath))
        {
            await SafetensorsWriter.WriteAsync(file, fs, auditMetadata, ct).ConfigureAwait(false);
        }

        // 6. Companion files.
        await ConfigEmitter.WriteAsync(arch, options, outputDir, ct).ConfigureAwait(false);
        await TokenizerExporter.WriteAsync(vocab, dataSource, outputDir, ct).ConfigureAwait(false);
        await WriteAuditAsync(arch, options, vocab.Count, adj,
            substrateAttentionLayers, substrateFfnLayers, outputDir, ct).ConfigureAwait(false);
    }

    private static async Task<(int attnLayers, int ffnLayers)> BuildTensorSetAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyList<VocabToken> vocab,
        TargetArchitectureSpec arch,
        RecompositionOptions options,
        TensorData embedTokens,
        float[] embeddingF32,
        SubstrateAdjacency adj,
        Dictionary<string, TensorData> tensors,
        CancellationToken ct)
    {
        int substrateAttentionLayers = 0;
        int substrateFfnLayers = 0;
        QuantizationTarget dt = options.OutputDtype;
        bool isLlama = arch.Architecture.Contains("Llama", StringComparison.OrdinalIgnoreCase);

        string embedName = isLlama ? "model.embed_tokens.weight" : "embeddings.word_embeddings.weight";
        string lmHeadName = isLlama ? "lm_head.weight" : "cls.predictions.decoder.weight";

        tensors[embedName] = embedTokens;
        if (arch.TieWordEmbeddings)
        {
            tensors[lmHeadName] = embedTokens;
        }
        else
        {
            tensors[lmHeadName] = ScaffoldSynthesizer.Initializer(
                lmHeadName, new[] { arch.VocabSize, arch.HiddenDim },
                arch.InitializerRange, options.LayerAssignmentSeed, dt);
        }

        // Load substrate-derived LayerNorm stats per arena. Replaces the
        // scaffold γ=1/β=0 init in EmitLayerTensors. Without per-arena LN,
        // activation variance compounds layer-to-layer → softmax saturates →
        // attention collapses to argmax → output degenerates to repetition.
        // Substrate-native γ = 1/stddev(mu in arena), β = -mean/stddev.
        HashSet<string> allArenas = new(StringComparer.Ordinal);
        for (int li = 0; li < arch.NumHiddenLayers; li++)
        {
            foreach (string a in SynthesisSection.DefaultLayerArenaChain[li % SynthesisSection.DefaultLayerArenaChain.Count])
            {
                allArenas.Add(a);
            }
        }
        IReadOnlyDictionary<string, LayerNormStats> layerNormStats;
        try
        {
            layerNormStats = await LayerNormSynthesizer.LoadStatsAsync(
                dataSource, allArenas.ToArray(), ct).ConfigureAwait(false);
            Console.Out.WriteLine($"  LayerNormStats: {layerNormStats.Count} arenas loaded for substrate-derived γ/β");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)  // BOUNDARY: substrate stats are an optional enrichment; missing per_arena_entity_significance_stats function (older extension) falls back to scaffold γ=1/β=0 init.
        {
            Console.Out.WriteLine($"  LayerNormStats: skipped ({ex.GetType().Name}); keeping scaffold γ=1/β=0");
            layerNormStats = new Dictionary<string, LayerNormStats>(StringComparer.Ordinal);
        }

        for (int layer = 0; layer < arch.NumHiddenLayers; layer++)
        {
            int hidden = arch.HiddenDim;
            int interSize = arch.IntermediateSize;
            int numHeads = arch.NumAttentionHeads;
            int headDim = arch.EffectiveHeadDim;

            // Per-layer arena assignment is the right design (each transformer
            // Per-layer arena assignment: each transformer layer reads a
            // different substrate adjacency built from its assigned arena
            // subset (per SynthesisSection.DefaultLayerArenaChain). Layer-
            // depth-driven function composition emerges from this — early
            // layers project the substrate's lexical/morphological surface,
            // mid layers its syntactic / semantic surface, deep layers its
            // translation / pattern-confidence surface. Without per-layer
            // adjacency, all 6 layers project from the same spectral
            // decomposition and depth buys nothing.
            //
            // Future perf path: extract to substrate SQL function
            // `substrate.build_synth_adjacency_csr(vocab, arenas, include_indirect)`
            // with C/C++ kernel offload — and/or factor the per-arena
            // breakdown out of the heavy edge_member self-join so all 6
            // adjacencies materialize from ONE PG scan. For now: 6 serial
            // builds, each with this layer's arena weights.
            IReadOnlyList<string> layerArena =
                SynthesisSection.DefaultLayerArenaChain[layer % SynthesisSection.DefaultLayerArenaChain.Count];
            Console.Out.WriteLine($"  layer {layer}: arena={string.Join(",", layerArena)} — building per-layer adjacency");
            RecompositionOptions layerOptions = WithLayerArenaWeights(options, layerArena);
            SubstrateAdjacency layerAdj = await SubstrateAdjacencyBuilder.BuildAsync(
                dataSource, vocab, layerOptions, ct).ConfigureAwait(false);
            Console.Out.WriteLine($"    layer {layer} adj: " + SubstrateAdjacencyBuilder.DebugCsrSummary(layerAdj));

            // Attention.
            AttentionMatrices attn;
            if (numHeads * headDim == hidden && layerAdj.Nnz > 0)
            {
                attn = AttentionSynthesizer.Synthesize(
                    layerAdj, embeddingF32, hidden, numHeads, headDim, layer, options);
            }
            else
            {
                attn = new AttentionMatrices
                {
                    HiddenDim = hidden, NumHeads = numHeads, HeadDim = headDim,
                    Wq = DetInit((long)numHeads * headDim * hidden, options, layer, 1, 0.02),
                    Wk = DetInit((long)numHeads * headDim * hidden, options, layer, 2, 0.02),
                    Wv = DetInit((long)numHeads * headDim * hidden, options, layer, 3, 0.02),
                    Wo = IdentityMatrix(hidden, 1.0 / numHeads),
                    DerivedFromSubstrate = false, RitzPairsUsed = 0,
                };
            }
            if (attn.DerivedFromSubstrate)
            {
                substrateAttentionLayers++;
            }

            // FFN-as-substrate-edges: each slot IS a concrete substrate edge.
            // Key direction = E[source], value direction = E[target],
            // weighted by signed sqrt(|mu-1500|/100). Inspectable + sparse-
            // by-construction. Falls back to Ritz-pair construction if the
            // edge-slot SQL function isn't installed (older extension).
            bool useSwiGlu = isLlama;
            FfnMatrices ffn;
            try
            {
                ffn = await FfnEdgeSlotSynthesizer.SynthesizeAsync(
                    dataSource, vocab, embeddingF32, layerArena, hidden, interSize,
                    layer, useSwiGlu, options, ct).ConfigureAwait(false);
            }
            catch (Npgsql.PostgresException pe) when (pe.SqlState == "42883")  // BOUNDARY: substrate.select_synth_edges_for_ffn not installed (older extension); fall back to Ritz-pair FFN.
            {
                ffn = layerAdj.Nnz > 0
                    ? FfnSynthesizer.Synthesize(layerAdj, embeddingF32, hidden, interSize, layer, useSwiGlu, options)
                    : new FfnMatrices
                    {
                        HiddenDim = hidden, IntermediateDim = interSize,
                        GateProj = useSwiGlu ? new float[(long)interSize * hidden] : null,
                        UpProj = new float[(long)interSize * hidden],
                        DownProj = new float[(long)hidden * interSize],
                        UseSwiGlu = useSwiGlu, DerivedFromSubstrate = false, RitzSlotsUsed = 0,
                    };
            }
            if (ffn.DerivedFromSubstrate)
            {
                substrateFfnLayers++;
            }

            // Emit per architecture naming. Pass per-layer arena's LN stats
            // (use the layer's *primary* arena — first entry in the recipe chain).
            string layerPrimaryArena = layerArena.Count > 0 ? layerArena[0] : "source_authority";
            EmitLayerTensors(arch, options, layer, attn, ffn, tensors, dt, layerNormStats, layerPrimaryArena);
        }

        // The embeddings/output-projection LayerNorm reads the recipe's
        // PRIMARY arena (first entry in the chain) for substrate-derived γ/β.
        string primaryArena = SynthesisSection.DefaultLayerArenaChain[0][0];

        if (isLlama)
        {
            tensors["model.norm.weight"] = TensorPacker.PackF32(
                LayerNormSynthesizer.GammaFor(primaryArena, arch.HiddenDim, layerNormStats),
                new[] { arch.HiddenDim }, dt);
        }
        else
        {
            tensors["embeddings.LayerNorm.weight"] = TensorPacker.PackF32(
                LayerNormSynthesizer.GammaFor(primaryArena, arch.HiddenDim, layerNormStats),
                new[] { arch.HiddenDim }, dt);
            tensors["embeddings.LayerNorm.bias"] = TensorPacker.PackF32(
                LayerNormSynthesizer.BetaFor(primaryArena, arch.HiddenDim, layerNormStats),
                new[] { arch.HiddenDim }, dt);
            tensors["embeddings.position_embeddings.weight"] = ScaffoldSynthesizer.Initializer(
                "embeddings.position_embeddings.weight",
                new[] { arch.MaxPositionEmbeddings, arch.HiddenDim },
                arch.InitializerRange, options.LayerAssignmentSeed, dt);
            tensors["embeddings.token_type_embeddings.weight"] = ScaffoldSynthesizer.Initializer(
                "embeddings.token_type_embeddings.weight",
                new[] { 2, arch.HiddenDim },
                arch.InitializerRange, options.LayerAssignmentSeed, dt);

            // Substrate-derived position embeddings replace the deterministic
            // init when content trajectory ordinals provide signal. The query
            // is heavy (walks every text_composition's child manifest); if it
            // fails for any reason, fall back to the deterministic init that
            // was already written above. Position embeddings are improveable
            // post-hoc by recipe iteration; not gating the mechanism test.
            try
            {
                TensorData posEmbed = await PositionEmbeddingSynthesizer.SynthesizeAsync(
                    dataSource, vocab, embeddingF32, arch.HiddenDim,
                    arch.MaxPositionEmbeddings, options, ct).ConfigureAwait(false);
                tensors["embeddings.position_embeddings.weight"] = posEmbed;
                Console.Out.WriteLine("  PositionEmbedding: substrate-derived");
            }
            // BOUNDARY: position embedding is an optional enrichment over the
            // deterministic init already written above. Any failure (missing
            // function, query timeout, type mismatch, null row) is non-fatal
            // for the mechanism gate — log the cause and continue.
            catch (Exception ex) when (ex is not OperationCanceledException)  // BOUNDARY: position embedding is an optional enrichment over deterministic init; any failure (missing function, query timeout, null rows) is non-fatal.
            {
                Console.Out.WriteLine($"  PositionEmbedding: skipped ({ex.GetType().Name}: {ex.Message}); keeping deterministic init");
            }
        }

        return (substrateAttentionLayers, substrateFfnLayers);
    }

    private static void EmitLayerTensors(
        TargetArchitectureSpec arch,
        RecompositionOptions options,
        int layer,
        AttentionMatrices attn,
        FfnMatrices ffn,
        Dictionary<string, TensorData> tensors,
        QuantizationTarget dt,
        IReadOnlyDictionary<string, LayerNormStats> layerNormStats,
        string layerArena)
    {
        int hidden = arch.HiddenDim;
        int interSize = arch.IntermediateSize;
        bool isLlama = arch.Architecture.Contains("Llama", StringComparison.OrdinalIgnoreCase);

        if (isLlama)
        {
            string p = $"model.layers.{layer}";
            tensors[$"{p}.self_attn.q_proj.weight"] = TensorPacker.PackF32(
                attn.Wq, new[] { hidden, hidden }, dt);
            tensors[$"{p}.self_attn.k_proj.weight"] = TensorPacker.PackF32(
                attn.Wk, new[] { hidden, hidden }, dt);
            tensors[$"{p}.self_attn.v_proj.weight"] = TensorPacker.PackF32(
                attn.Wv, new[] { hidden, hidden }, dt);
            tensors[$"{p}.self_attn.o_proj.weight"] = TensorPacker.PackF32(
                attn.Wo, new[] { hidden, hidden }, dt);

            tensors[$"{p}.mlp.gate_proj.weight"] = TensorPacker.PackF32(
                ffn.GateProj ?? throw new InvalidOperationException("Llama FFN requires SwiGLU gate"),
                new[] { interSize, hidden }, dt);
            tensors[$"{p}.mlp.up_proj.weight"] = TensorPacker.PackF32(
                ffn.UpProj, new[] { interSize, hidden }, dt);
            tensors[$"{p}.mlp.down_proj.weight"] = TensorPacker.PackF32(
                ffn.DownProj, new[] { hidden, interSize }, dt);

            tensors[$"{p}.input_layernorm.weight"] = ScaffoldSynthesizer.Ones(
                $"{p}.input_layernorm.weight", new[] { hidden }, dt);
            tensors[$"{p}.post_attention_layernorm.weight"] = ScaffoldSynthesizer.Ones(
                $"{p}.post_attention_layernorm.weight", new[] { hidden }, dt);
        }
        else
        {
            string p = $"encoder.layer.{layer}";

            tensors[$"{p}.attention.self.query.weight"] = TensorPacker.PackF32(
                attn.Wq, new[] { hidden, hidden }, dt);
            tensors[$"{p}.attention.self.query.bias"] = ScaffoldSynthesizer.Zeros(
                $"{p}.attention.self.query.bias", new[] { hidden }, dt);
            tensors[$"{p}.attention.self.key.weight"] = TensorPacker.PackF32(
                attn.Wk, new[] { hidden, hidden }, dt);
            tensors[$"{p}.attention.self.key.bias"] = ScaffoldSynthesizer.Zeros(
                $"{p}.attention.self.key.bias", new[] { hidden }, dt);
            tensors[$"{p}.attention.self.value.weight"] = TensorPacker.PackF32(
                attn.Wv, new[] { hidden, hidden }, dt);
            tensors[$"{p}.attention.self.value.bias"] = ScaffoldSynthesizer.Zeros(
                $"{p}.attention.self.value.bias", new[] { hidden }, dt);
            tensors[$"{p}.attention.output.dense.weight"] = TensorPacker.PackF32(
                attn.Wo, new[] { hidden, hidden }, dt);
            tensors[$"{p}.attention.output.dense.bias"] = ScaffoldSynthesizer.Zeros(
                $"{p}.attention.output.dense.bias", new[] { hidden }, dt);
            tensors[$"{p}.attention.output.LayerNorm.weight"] = TensorPacker.PackF32(
                LayerNormSynthesizer.GammaFor(layerArena, hidden, layerNormStats),
                new[] { hidden }, dt);
            tensors[$"{p}.attention.output.LayerNorm.bias"] = TensorPacker.PackF32(
                LayerNormSynthesizer.BetaFor(layerArena, hidden, layerNormStats),
                new[] { hidden }, dt);

            tensors[$"{p}.intermediate.dense.weight"] = TensorPacker.PackF32(
                ffn.UpProj, new[] { interSize, hidden }, dt);
            tensors[$"{p}.intermediate.dense.bias"] = ScaffoldSynthesizer.Zeros(
                $"{p}.intermediate.dense.bias", new[] { interSize }, dt);
            tensors[$"{p}.output.dense.weight"] = TensorPacker.PackF32(
                ffn.DownProj, new[] { hidden, interSize }, dt);
            tensors[$"{p}.output.dense.bias"] = ScaffoldSynthesizer.Zeros(
                $"{p}.output.dense.bias", new[] { hidden }, dt);
            tensors[$"{p}.output.LayerNorm.weight"] = TensorPacker.PackF32(
                LayerNormSynthesizer.GammaFor(layerArena, hidden, layerNormStats),
                new[] { hidden }, dt);
            tensors[$"{p}.output.LayerNorm.bias"] = TensorPacker.PackF32(
                LayerNormSynthesizer.BetaFor(layerArena, hidden, layerNormStats),
                new[] { hidden }, dt);
        }
    }

    /// <summary>
    /// Build a per-layer RecompositionOptions override that weights the
    /// layer's assigned arena(s) at 1.0 and keeps the two universal arenas
    /// (source_authority + corroboration_strength) at 0.3 baseline so even
    /// layers with sparse domain attestation density still produce a
    /// non-empty adjacency. Each transformer layer's AttentionSynthesizer
    /// then projects through a spectrum tailored to that layer's role
    /// (lexical / morphological / syntactic / semantic / translation /
    /// pattern-confidence / sequence-following per SynthesisSection.
    /// DefaultLayerArenaChain).
    /// </summary>
    private static RecompositionOptions WithLayerArenaWeights(
        RecompositionOptions options, IReadOnlyList<string> layerArenas)
    {
        var builder = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, double>(System.StringComparer.Ordinal);
        // Universal baseline — keeps adjacency non-empty for low-attestation
        // arenas, and preserves cross-source corroboration signal regardless
        // of layer role.
        builder["source_authority"] = 0.3;
        builder["corroboration_strength"] = 0.3;
        // Layer-specific arenas at full weight. Listed multiple times in
        // DefaultLayerArenaChain entries get implicitly accumulated (e.g.
        // layer 4 = [translation_quality, frequency_significance] → both
        // weight 1.0 → adjacency reflects union of the two surfaces).
        foreach (string arena in layerArenas)
        {
            builder[arena] = 1.0;
        }
        return options with { ArenaWeights = builder.ToImmutable() };
    }

    private static float[] DetInit(long n, RecompositionOptions opt, int layer, int salt, double stddev)
    {
        ulong rng = unchecked((ulong)(long)(opt.LayerAssignmentSeed ^ (layer * 7919 + salt)))
                    * 0xBF58_476D_1CE4_E5B9UL;
        if (rng == 0)
        {
            rng = 0xCAFEDEADFACEBEEFUL;
        }
        float[] v = new float[n];
        for (long i = 0; i < n; i++)
        {
            rng = rng * 6364136223846793005UL + 1442695040888963407UL;
            ulong r2 = rng * 6364136223846793005UL + 1442695040888963407UL;
            double u1 = (((rng >> 11) & 0x1F_FFFF_FFFF_FFFFUL) + 1.0) / (double)(1UL << 53);
            double u2 = ((r2 >> 11) & 0x1F_FFFF_FFFF_FFFFUL) / (double)(1UL << 53);
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            v[i] = (float)(stddev * z);
            rng = r2;
        }
        return v;
    }

    private static float[] IdentityMatrix(int n, double scale)
    {
        float[] m = new float[(long)n * n];
        for (int i = 0; i < n; i++)
        {
            m[(long)i * n + i] = (float)scale;
        }
        return m;
    }

    private static string SerializeRecipe(RecompositionOptions options)
    {
        using MemoryStream ms = new();
        using Utf8JsonWriter w = new(ms);
        w.WriteStartObject();
        w.WriteStartObject("arena_weights");
        foreach ((string k, double v) in options.ArenaWeights)
        {
            w.WriteNumber(k, v);
        }
        w.WriteEndObject();
        w.WriteStartObject("provenance_weights");
        foreach ((string k, double v) in options.ProvenanceWeights)
        {
            w.WriteNumber(k, v);
        }
        w.WriteEndObject();
        w.WriteNumber("layer_assignment_seed", options.LayerAssignmentSeed);
        w.WriteNumber("significance_floor", options.SignificanceFloor);
        w.WriteBoolean("honest_abstention", options.HonestAbstention);
        w.WriteString("output_dtype", options.OutputDtype.ToString());
        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task WriteAuditAsync(
        TargetArchitectureSpec arch,
        RecompositionOptions options,
        int actualVocabSize,
        SubstrateAdjacency adj,
        int substrateAttnLayers,
        int substrateFfnLayers,
        string outputDir,
        CancellationToken ct)
    {
        string auditJson = $$"""
        {
          "hartonomous_recomposer_version": "v2-substrate-spectral",
          "architecture": "{{arch.Architecture}}",
          "shape": {
            "vocab_size_requested": {{arch.VocabSize}},
            "vocab_size_actual":    {{actualVocabSize}},
            "hidden_dim":           {{arch.HiddenDim}},
            "num_hidden_layers":    {{arch.NumHiddenLayers}},
            "num_attention_heads":  {{arch.NumAttentionHeads}},
            "intermediate_size":    {{arch.IntermediateSize}}
          },
          "substrate_signal": {
            "adjacency_nnz":        {{adj.Nnz}},
            "non_isolated_nodes":   {{adj.NonIsolatedNodes}},
            "attention_layers_substrate_derived": {{substrateAttnLayers}},
            "ffn_layers_substrate_derived":       {{substrateFfnLayers}}
          },
          "recipe": {{SerializeRecipe(options)}}
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(outputDir, "hartonomous_audit.json"),
            auditJson, ct).ConfigureAwait(false);
    }
}
