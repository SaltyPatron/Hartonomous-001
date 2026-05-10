using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Data;
using Hartonomous.Core.Recomposition;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Recomposers;

/// <summary>
/// Phase C.2 implementation of <see cref="ISynthesisRecomposer"/>. Replaces
/// the phantom-scatter logic in <see cref="SafetensorsRecomposer"/> with
/// synthesizer-dispatch-based recomposition. The substrate's stored attestation
/// edges + per-arena consensus mu drive every tensor's content; honest
/// abstention masks under-attested cells to exact zero per spec §VIII.
///
/// Two operating modes:
/// <list type="bullet">
/// <item><b>Mode 1 (re-export):</b> walk the substrate's stored tree for the
/// given <c>model_source_id</c>; dispatch each stored tensor by role to the
/// matching synthesizer with source filter restricted to [modelSourceId];
/// emit safetensors. Single-source round-trip for any ingested model.</item>
/// <item><b>Mode 2 (build-a-bear):</b> walk a user-supplied
/// <see cref="TargetArchitectureSpec"/>; dispatch each target tensor by role
/// with optional source filter (default all-consensus); emit safetensors as
/// a NEW student model whose model_id is content-addressed by the recipe.</item>
/// </list>
///
/// The two modes share the per-layer-type synthesizer library; the difference
/// is the source of the target tensor list and the source filter default.
/// </summary>
public sealed partial class SynthesisRecomposer : ISynthesisRecomposer
{
    private readonly ILayerTypeSynthesizerRegistry _synthesizers;
    private readonly IEntityReader _entityReader;
    private readonly IPhysicalityReader _physicalityReader;
    private readonly IComputeFacade _compute;
    private readonly ILogger<SynthesisRecomposer> _logger;

    public SynthesisRecomposer(
        ILayerTypeSynthesizerRegistry synthesizers,
        IEntityReader entityReader,
        IPhysicalityReader physicalityReader,
        IComputeFacade compute,
        ILogger<SynthesisRecomposer> logger)
    {
        _synthesizers = synthesizers;
        _entityReader = entityReader;
        _physicalityReader = physicalityReader;
        _compute = compute;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<RecompositionReport> RecomposeIngestedModelAsync(
        long modelSourceId,
        RecompositionOptions options,
        Stream output,
        CancellationToken ct)
    {
        // Mode 1: read the stored model tree from substrate; dispatch with single-source filter.
        SynthesisContext context = new(
            Options: options,
            SourceModelIds: new long[] { modelSourceId },
            TargetArchitecture: null,
            EntityReader: _entityReader,
            PhysicalityReader: _physicalityReader,
            Compute: _compute);

        IReadOnlyList<TargetTensorSpec> targets = await ResolveStoredModelTensorsAsync(modelSourceId, options, ct);
        return await DispatchAndEmitAsync(targets, context, output, modelName: $"hartonomous_export_{modelSourceId}", ct);
    }

    /// <inheritdoc/>
    public async Task<RecompositionReport> RecomposeFromArchitectureSpecAsync(
        TargetArchitectureSpec target,
        RecompositionOptions options,
        IReadOnlyList<long>? sourceModelIds,
        Stream output,
        CancellationToken ct)
    {
        // Mode 2: enumerate target tensors from the architecture spec; dispatch with optional filter.
        SynthesisContext context = new(
            Options: options,
            SourceModelIds: sourceModelIds,
            TargetArchitecture: target,
            EntityReader: _entityReader,
            PhysicalityReader: _physicalityReader,
            Compute: _compute);

        IReadOnlyList<TargetTensorSpec> targets = EnumerateTargetTensorsFromSpec(target);
        return await DispatchAndEmitAsync(targets, context, output, modelName: target.ArchitectureClass, ct);
    }

    private async Task<RecompositionReport> DispatchAndEmitAsync(
        IReadOnlyList<TargetTensorSpec> targets,
        SynthesisContext context,
        Stream output,
        string modelName,
        CancellationToken ct)
    {
        Dictionary<string, TensorData> tensors = new(targets.Count, StringComparer.Ordinal);
        Dictionary<string, double> perTensorCoverage = new(targets.Count, StringComparer.Ordinal);
        long totalBytes = 0;
        double sumCoverage = 0;
        double minCoverage = 1.0;
        double sumZeroFraction = 0;
        HashSet<int> contributingSources = new();
        int synthesizedCount = 0;

        foreach (TargetTensorSpec target in targets)
        {
            ct.ThrowIfCancellationRequested();
            ILayerTypeSynthesizer? synthesizer = _synthesizers.GetSynthesizer(target.RoleCode);
            if (synthesizer is null)
            {
                Log.NoSynthesizerForRole(_logger, target.RoleCode, target.Name);
                byte[] zeros = AllocateZeroTensorBytes(target);
                tensors[target.Name] = new TensorData(target.Dtype, ToIntShape(target.Shape), zeros);
                perTensorCoverage[target.Name] = 0.0;
                totalBytes += zeros.LongLength;
                continue;
            }

            SynthesisResult result = await synthesizer.SynthesizeAsync(target, context, ct);
            tensors[target.Name] = new TensorData(target.Dtype, ToIntShape(target.Shape), result.Bytes);
            perTensorCoverage[target.Name] = result.AggregateCoverage;
            totalBytes += result.Bytes.LongLength;
            sumCoverage += result.AggregateCoverage;
            if (result.AggregateCoverage < minCoverage) { minCoverage = result.AggregateCoverage; }
            sumZeroFraction += 1.0 - result.AggregateCoverage;
            contributingSources.Add(result.ContributingSourceCount);
            synthesizedCount++;
        }

        SafetensorsFile file = new(tensors, modelName);
        IReadOnlyDictionary<string, string> auditMetadata = BuildAuditMetadata(context.Options, perTensorCoverage);
        await SafetensorsWriter.WriteAsync(file, output, auditMetadata, ct);

        return new RecompositionReport(
            TensorCount: targets.Count,
            TotalBytes: totalBytes,
            MeanCoverage: synthesizedCount > 0 ? sumCoverage / synthesizedCount : 0.0,
            MinCoverage: synthesizedCount > 0 ? minCoverage : 0.0,
            ZeroFractionMean: synthesizedCount > 0 ? sumZeroFraction / synthesizedCount : 1.0,
            ContributingSourceCount: contributingSources.Count,
            PerTensorCoverage: perTensorCoverage);
    }

    private async Task<IReadOnlyList<TargetTensorSpec>> ResolveStoredModelTensorsAsync(
        long modelSourceId, RecompositionOptions options, CancellationToken ct)
    {
        // Phase C.2.b implementation pending. Sequence:
        //   1. Resolve the model_architecture entity for the given model_source_id
        //      via a substrate.* SQL function (SELECT model_architecture_hash FROM
        //      substrate.entity_model_source ems JOIN ... WHERE ems.id = $1).
        //   2. Walk has_tensor edges from model_architecture (via _entityReader.
        //      GetOutboundEdgeTargetsAsync) to enumerate tensor entities.
        //   3. For each tensor entity, read its has_tensor_name / has_dtype /
        //      has_shape / in_layer metadata edges.
        //   4. Read its tensor_role classification via tensor_tensor_role junction.
        //   5. Construct TargetTensorSpec per tensor; return ordered list.
        await Task.CompletedTask;
        // Reference _entityReader to satisfy CA1822 — the future implementation
        // walks substrate via _entityReader; current stub keeps the binding live.
        _ = _entityReader;
        throw new NotImplementedException(
            $"SynthesisRecomposer.ResolveStoredModelTensorsAsync (Mode 1 re-export, model_source_id={modelSourceId}): " +
            $"substrate query backing pending Phase B.2 SQL functions for model-source-tensor enumeration.");
    }

    private static IReadOnlyList<TargetTensorSpec> EnumerateTargetTensorsFromSpec(TargetArchitectureSpec spec)
    {
        // Phase C.2.b implementation pending. The HuggingFace transformers convention
        // is well-documented per architecture (Llama / Mistral / Qwen / GPT-NeoX /
        // Gemma / Phi / etc.); this method enumerates the canonical tensor list
        // for the named architecture_class with the supplied dimensions. For
        // archetypal Llama-family monolith:
        //   embed_tokens.weight: TokenEmbedding [vocab_size, hidden_size]
        //   per layer (0..num_layers-1):
        //     self_attn.q_proj.weight: AttentionQuery [num_attention_heads*head_dim, hidden_size]
        //     self_attn.k_proj.weight: AttentionKey [num_kv_heads*head_dim, hidden_size]
        //     self_attn.v_proj.weight: AttentionValue [num_kv_heads*head_dim, hidden_size]
        //     self_attn.o_proj.weight: AttentionOutput [hidden_size, num_attention_heads*head_dim]
        //     mlp.gate_proj.weight: FfnGate [ffn_intermediate, hidden_size]
        //     mlp.up_proj.weight: FfnUp [ffn_intermediate, hidden_size]
        //     mlp.down_proj.weight: FfnDown [hidden_size, ffn_intermediate]
        //     input_layernorm.weight: RmsNorm [hidden_size]
        //     post_attention_layernorm.weight: RmsNorm [hidden_size]
        //   norm.weight: RmsNorm [hidden_size]
        //   lm_head.weight: LogitHead [vocab_size, hidden_size] (when not tied)
        // MoE adds router/expert variants per layer; LoRA adds A/B per adapter spec.
        throw new NotImplementedException(
            $"SynthesisRecomposer.EnumerateTargetTensorsFromSpec (Mode 2 build-a-bear): " +
            $"architecture-template enumeration pending — Phase C.2.b implementation. " +
            $"target arch={spec.ArchitectureClass} hidden={spec.HiddenSize} layers={spec.NumLayers}");
    }

    private static byte[] AllocateZeroTensorBytes(TargetTensorSpec target)
    {
        long elementCount = 1;
        for (int i = 0; i < target.Shape.Count; i++) { elementCount *= target.Shape[i]; }
        long bytes = elementCount * BytesPerElement(target.Dtype);
        if (bytes < 0 || bytes > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"Tensor {target.Name} ({target.Dtype} {string.Join('x', target.Shape)}) byte count out of range.");
        }
        return new byte[bytes];
    }

    private static int BytesPerElement(string dtype) => dtype switch
    {
        "F64" => 8,
        "F32" => 4,
        "BF16" or "F16" => 2,
        "F8_E4M3" or "F8_E5M2" or "I8" => 1,
        _ => throw new SynthesisDtypeException($"Unknown dtype '{dtype}'"),
    };

    private static int[] ToIntShape(IReadOnlyList<long> shape)
    {
        int[] result = new int[shape.Count];
        for (int i = 0; i < shape.Count; i++)
        {
            if (shape[i] > int.MaxValue)
            {
                throw new InvalidOperationException($"Shape dimension {shape[i]} exceeds int.MaxValue");
            }
            result[i] = (int)shape[i];
        }
        return result;
    }

    private static Dictionary<string, string> BuildAuditMetadata(
        RecompositionOptions options,
        Dictionary<string, double> perTensorCoverage)
    {
        Dictionary<string, string> metadata = new(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(options.RecipeId))
        {
            metadata["hartonomous_recipe_id"] = options.RecipeId;
        }
        if (options.ArenaCodes is { Count: > 0 })
        {
            metadata["hartonomous_arena_codes"] = string.Join(",", options.ArenaCodes);
        }
        if (options.AttestationTypeCodes is { Count: > 0 })
        {
            metadata["hartonomous_attestation_types"] = string.Join(",", options.AttestationTypeCodes);
        }
        metadata["hartonomous_significance_threshold"] =
            options.SignificanceThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture);
        metadata["hartonomous_tensor_count"] =
            perTensorCoverage.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Per-tensor coverage is large; emit aggregate only for the header to
        // avoid bloating the safetensors metadata block. Per-tensor stats can
        // be retrieved from the RecompositionReport returned by RecomposeAsync.
        double mean = 0;
        foreach (KeyValuePair<string, double> kv in perTensorCoverage) { mean += kv.Value; }
        if (perTensorCoverage.Count > 0) { mean /= perTensorCoverage.Count; }
        metadata["hartonomous_mean_coverage"] =
            mean.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
        return metadata;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "[synthesis-recomposer] no synthesizer registered for role '{Role}'; tensor {Name} emitted as honest-abstention zero")]
        public static partial void NoSynthesizerForRole(ILogger logger, string role, string name);
    }
}
