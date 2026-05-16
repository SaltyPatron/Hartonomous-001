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

        // 1. Pick vocab.
        IReadOnlyList<VocabToken> vocab = await VocabSelector.SelectAsync(
            dataSource, arch.VocabSize, ct).ConfigureAwait(false);
        Console.Out.WriteLine($"VocabSelector: {vocab.Count} word_form entities selected.");

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
        BuildTensorSet(
            arch, options, embedTokens, embeddingF32, adj,
            tensors, ref substrateAttentionLayers, ref substrateFfnLayers);

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
        await TokenizerExporter.WriteAsync(vocab, outputDir, ct).ConfigureAwait(false);
        await WriteAuditAsync(arch, options, vocab.Count, adj,
            substrateAttentionLayers, substrateFfnLayers, outputDir, ct).ConfigureAwait(false);
    }

    private static void BuildTensorSet(
        TargetArchitectureSpec arch,
        RecompositionOptions options,
        TensorData embedTokens,
        float[] embeddingF32,
        SubstrateAdjacency adj,
        Dictionary<string, TensorData> tensors,
        ref int substrateAttentionLayers,
        ref int substrateFfnLayers)
    {
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

        for (int layer = 0; layer < arch.NumHiddenLayers; layer++)
        {
            int hidden = arch.HiddenDim;
            int interSize = arch.IntermediateSize;
            int numHeads = arch.NumAttentionHeads;
            int headDim = arch.EffectiveHeadDim;

            // Attention.
            AttentionMatrices attn;
            if (numHeads * headDim == hidden)
            {
                attn = AttentionSynthesizer.Synthesize(
                    adj, embeddingF32, hidden, numHeads, headDim, layer, options);
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

            // FFN.
            bool useSwiGlu = isLlama;
            FfnMatrices ffn = FfnSynthesizer.Synthesize(
                adj, embeddingF32, hidden, interSize, layer, useSwiGlu, options);
            if (ffn.DerivedFromSubstrate)
            {
                substrateFfnLayers++;
            }

            // Emit per architecture naming.
            EmitLayerTensors(arch, options, layer, attn, ffn, tensors, dt);
        }

        if (isLlama)
        {
            tensors["model.norm.weight"] = ScaffoldSynthesizer.Ones(
                "model.norm.weight", new[] { arch.HiddenDim }, dt);
        }
        else
        {
            tensors["embeddings.LayerNorm.weight"] = ScaffoldSynthesizer.Ones(
                "embeddings.LayerNorm.weight", new[] { arch.HiddenDim }, dt);
            tensors["embeddings.LayerNorm.bias"] = ScaffoldSynthesizer.Zeros(
                "embeddings.LayerNorm.bias", new[] { arch.HiddenDim }, dt);
            tensors["embeddings.position_embeddings.weight"] = ScaffoldSynthesizer.Initializer(
                "embeddings.position_embeddings.weight",
                new[] { arch.MaxPositionEmbeddings, arch.HiddenDim },
                arch.InitializerRange, options.LayerAssignmentSeed, dt);
            tensors["embeddings.token_type_embeddings.weight"] = ScaffoldSynthesizer.Initializer(
                "embeddings.token_type_embeddings.weight",
                new[] { 2, arch.HiddenDim },
                arch.InitializerRange, options.LayerAssignmentSeed, dt);
        }
    }

    private static void EmitLayerTensors(
        TargetArchitectureSpec arch,
        RecompositionOptions options,
        int layer,
        AttentionMatrices attn,
        FfnMatrices ffn,
        Dictionary<string, TensorData> tensors,
        QuantizationTarget dt)
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
            tensors[$"{p}.attention.output.LayerNorm.weight"] = ScaffoldSynthesizer.Ones(
                $"{p}.attention.output.LayerNorm.weight", new[] { hidden }, dt);
            tensors[$"{p}.attention.output.LayerNorm.bias"] = ScaffoldSynthesizer.Zeros(
                $"{p}.attention.output.LayerNorm.bias", new[] { hidden }, dt);

            tensors[$"{p}.intermediate.dense.weight"] = TensorPacker.PackF32(
                ffn.UpProj, new[] { interSize, hidden }, dt);
            tensors[$"{p}.intermediate.dense.bias"] = ScaffoldSynthesizer.Zeros(
                $"{p}.intermediate.dense.bias", new[] { interSize }, dt);
            tensors[$"{p}.output.dense.weight"] = TensorPacker.PackF32(
                ffn.DownProj, new[] { hidden, interSize }, dt);
            tensors[$"{p}.output.dense.bias"] = ScaffoldSynthesizer.Zeros(
                $"{p}.output.dense.bias", new[] { hidden }, dt);
            tensors[$"{p}.output.LayerNorm.weight"] = ScaffoldSynthesizer.Ones(
                $"{p}.output.LayerNorm.weight", new[] { hidden }, dt);
            tensors[$"{p}.output.LayerNorm.bias"] = ScaffoldSynthesizer.Zeros(
                $"{p}.output.LayerNorm.bias", new[] { hidden }, dt);
        }
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
