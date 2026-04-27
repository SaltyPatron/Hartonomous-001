using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Analysis;
using Hartonomous.Core.Data;
using Hartonomous.Core.Query;
using Hartonomous.Core.Recomposition;

namespace Hartonomous.Recomposers;

/// <summary>
/// Serializes substrate state into the safetensors wire format. NOT an AI
/// operation — AI lives in substrate traversal (inference, generation,
/// transformation via traverse_astar). This recomposer is a data export
/// pipe: query the substrate, assemble per-tensor (dtype, shape, bytes),
/// pack into the standard safetensors container via
/// <see cref="SafetensorsWriter"/>.
///
/// Per docs/specs/csharp/recomposers.md § "SafetensorsRecomposer" and
/// docs/specs/decomposers/safetensors.md § "Distillation (Recomposer)":
///   1. Walk <c>has_tensor</c> edges from the model_architecture entity
///      to its tensor entities.
///   2. Per tensor: read metadata edges (<c>has_tensor_name</c> for name,
///      <c>has_dtype</c>, <c>has_shape</c>) — each targets a text_composition
///      entity recomposed via the existing <see cref="ITextRecompositionReader"/>.
///   3. Per tensor: assemble byte payload. Where the substrate has per-tensor
///      content (bytes, edges, significance) the assembly fills in; where it
///      has nothing the bytes stay zero (the spec's "below-threshold weights
///      are zeros" sparsity outcome — Substrate Law #11).
///   4. Pack into <see cref="SafetensorsFile"/>; <see cref="SafetensorsWriter"/>
///      writes the binary container.
///
/// Output is structurally valid (correct dtype, shape, byte count, valid
/// safetensors header) regardless of substrate density. As more substrate
/// content accumulates (per-row / per-rank / per-head entities populated
/// by Track-2 passes), the assembly fills more of each tensor.
/// </summary>
public sealed class SafetensorsRecomposer : BaseRecomposer<SafetensorsFile>
{
    private readonly ITextRecompositionReader? _textReader;
    private readonly IPhysicalityReader? _physicalityReader;
    private readonly ISubstrateQuery? _query;

    public SafetensorsRecomposer(
        IEntityReader entityReader,
        ITextRecompositionReader? textReader = null,
        IPhysicalityReader? physicalityReader = null,
        ISubstrateQuery? query = null)
        : base(entityReader)
    {
        _textReader = textReader;
        _physicalityReader = physicalityReader;
        _query = query;
    }

    /// <summary>
    /// Distilled export: pulls only the tensors matching <paramref name="filter"/>
    /// (e.g. ModelSourceIds = Qwen-Coder model sources, MinSignificanceMu = 1500).
    /// Same recompose pipeline as <see cref="RecomposeAsync(long, RecompositionOptions, CancellationToken)"/>;
    /// only the tensor selection differs. Per architecture.md "Distillation = WHERE clause" —
    /// distillation and export are the same operation parameterized by the query.
    /// </summary>
    public async Task<SafetensorsFile> RecomposeFilteredAsync(
        long modelArchitectureEntityId,
        SubstrateQueryFilter filter,
        RecompositionOptions options,
        CancellationToken ct)
    {
        IReadOnlyList<long> tensorEntityIds = _query is not null
            ? await _query.QueryTensorsForArchitectureAsync(modelArchitectureEntityId, filter, ct)
            : await EntityReader.GetOutboundEdgeTargetsAsync(modelArchitectureEntityId, "has_tensor", ct);

        Dictionary<string, TensorData> tensors = new(tensorEntityIds.Count, StringComparer.Ordinal);
        foreach (long tensorId in tensorEntityIds)
        {
            ct.ThrowIfCancellationRequested();
            (string name, string dtype, int[] shape) = await ReadTensorMetadataAsync(tensorId, options, ct);
            byte[] bytes = await AssembleTensorBytesAsync(tensorId, dtype, shape, ct);
            tensors[name] = new TensorData(dtype, shape, bytes);
        }

        string modelName = await ResolveModelNameAsync(modelArchitectureEntityId, options, ct);
        return new SafetensorsFile(tensors, modelName);
    }

    public override Modality OutputModality => Modality.ModelWeights;

    public override async Task<SafetensorsFile> RecomposeAsync(
        long entityId,
        RecompositionOptions options,
        CancellationToken ct)
    {
        // Walk has_tensor edges from the architecture to every tensor entity.
        IReadOnlyList<long> tensorEntityIds = await EntityReader.GetOutboundEdgeTargetsAsync(
            entityId, "has_tensor", ct);

        // Per-tensor: read metadata + assemble byte payload.
        Dictionary<string, TensorData> tensors = new(tensorEntityIds.Count, StringComparer.Ordinal);
        foreach (long tensorId in tensorEntityIds)
        {
            ct.ThrowIfCancellationRequested();
            (string name, string dtype, int[] shape) = await ReadTensorMetadataAsync(tensorId, options, ct);
            byte[] bytes = await AssembleTensorBytesAsync(tensorId, dtype, shape, ct);
            tensors[name] = new TensorData(dtype, shape, bytes);
        }

        string modelName = await ResolveModelNameAsync(entityId, options, ct);
        return new SafetensorsFile(tensors, modelName);
    }

    public override async Task RecomposeToStreamAsync(
        long entityId,
        RecompositionOptions options,
        Stream output,
        CancellationToken ct)
    {
        SafetensorsFile file = await RecomposeAsync(entityId, options, ct);
        await SafetensorsWriter.WriteAsync(file, output, ct);
    }

    /// <summary>
    /// Reads the three metadata edges (has_tensor_name, has_dtype, has_shape)
    /// from a tensor entity and recomposes each target text document into
    /// its string value via <see cref="ITextRecompositionReader"/>.
    /// Falls back to deterministic placeholders when an edge or its target
    /// text is absent — keeps the package structurally valid for sparse
    /// substrate states.
    /// </summary>
    private async Task<(string Name, string Dtype, int[] Shape)> ReadTensorMetadataAsync(
        long tensorEntityId, RecompositionOptions options, CancellationToken ct)
    {
        string name = $"tensor_{tensorEntityId}";
        string dtype = "F32";
        int[] shape = [];

        IReadOnlyList<long> nameTargets = await EntityReader.GetOutboundEdgeTargetsAsync(
            tensorEntityId, "has_tensor_name", ct);
        IReadOnlyList<long> dtypeTargets = await EntityReader.GetOutboundEdgeTargetsAsync(
            tensorEntityId, "has_dtype", ct);
        IReadOnlyList<long> shapeTargets = await EntityReader.GetOutboundEdgeTargetsAsync(
            tensorEntityId, "has_shape", ct);

        if (_textReader is not null)
        {
            if (nameTargets.Count > 0)
            {
                string? n = await _textReader.RecomposeTextAsync(nameTargets[0], options.MaxDepth, ct);
                if (!string.IsNullOrEmpty(n)) { name = n; }
            }
            if (dtypeTargets.Count > 0)
            {
                string? d = await _textReader.RecomposeTextAsync(dtypeTargets[0], options.MaxDepth, ct);
                if (!string.IsNullOrEmpty(d)) { dtype = d.Trim(); }
            }
            if (shapeTargets.Count > 0)
            {
                string? s = await _textReader.RecomposeTextAsync(shapeTargets[0], options.MaxDepth, ct);
                if (!string.IsNullOrEmpty(s)) { shape = ParseShape(s); }
            }
        }

        return (name, dtype, shape);
    }

    /// <summary>
    /// Assembles the tensor's byte payload from substrate state. Default
    /// outcome for a substrate with no per-position content is a zero-filled
    /// buffer of the correct size — Law #11 sparsity. The assembly precedence:
    ///   1. 1-D tensors: tensor-attached contour (OneDTensorPass) →
    ///      has_layer_norm_scale → has_rope_freqs (unit-attached contour).
    ///   2. ≥2-D tensors: per-role unit scatter via substrate.sequence
    ///      children — each row-positioned per-role entity (ffn_neuron,
    ///      attention_component, embedding_position, logit_projection,
    ///      moe_*, object_query_slot, class_projection, bbox_projection,
    ///      vision_feature_direction, modality_basis_vector, lora_component,
    ///      conv_filter, diffusion_component, conformer_component,
    ///      audio_codec_filter) carries its row content as a contour physicality,
    ///      scattered into the buffer at row=ordinal_position. The per-role
    ///      path is lossless on the rows the substrate actually has.
    ///   3. Fallback: walk has_rank_component edges and reconstruct via
    ///      Σ σ·uvᵀ. Lossy by rank truncation; used only when no per-role
    ///      units have been emitted for this tensor.
    /// </summary>
    private async Task<byte[]> AssembleTensorBytesAsync(
        long tensorEntityId, string dtype, int[] shape, CancellationToken ct)
    {
        long elementCount = 1;
        for (int i = 0; i < shape.Length; i++)
        {
            elementCount *= shape[i];
        }
        int bytesPerElement = BytesPerElement(dtype);
        long totalBytes = elementCount * bytesPerElement;
        if (totalBytes < 0)
        {
            throw new InvalidOperationException(
                $"Negative byte count for tensor {tensorEntityId} ({dtype} {string.Join('x', shape)}).");
        }
        if (totalBytes > int.MaxValue)
        {
            throw new NotSupportedException(
                $"Tensor {tensorEntityId} ({dtype} {string.Join('x', shape)}) exceeds int.MaxValue bytes; sharded output not yet implemented.");
        }

        byte[] buffer = new byte[totalBytes];

        if (_physicalityReader is null)
        {
            return buffer;
        }

        // 1-D path: tensor-attached contour (OneDTensorPass) first, then
        // walk to a per-role unit (LayerNormPass / RopeFreqPass) via
        // has_layer_norm_scale or has_rope_freqs and read its contour.
        if (shape.Length == 1 && shape[0] > 0)
        {
            int length = shape[0];
            double[]? values = await _physicalityReader.GetLineString4dAsync(
                tensorEntityId, "contour", ct);
            if (values is null || values.Length < length)
            {
                values = await TryReadUnitContourAsync(tensorEntityId, "has_layer_norm_scale", length, ct)
                       ?? await TryReadUnitContourAsync(tensorEntityId, "has_rope_freqs", length, ct);
            }
            if (values is null || values.Length < length)
            {
                return buffer;
            }
            double[] sliced = new double[length];
            Array.Copy(values, sliced, length);
            PackToWire(sliced, dtype, buffer);
            return buffer;
        }

        // ≥2-D: per-role unit scatter via sequence children. cols = product
        // of all dims after the first, so 4-D conv kernels (out, in, kh, kw)
        // and other rank-N tensors flow through the same scatter path —
        // outer index = sequence position; trailing dims = packed row content.
        if (shape.Length < 2 || shape[0] <= 0)
        {
            return buffer;
        }
        int rows = shape[0];
        long cols64 = 1;
        for (int d = 1; d < shape.Length; d++) { cols64 *= shape[d]; }
        if (cols64 <= 0 || cols64 > int.MaxValue) { return buffer; }
        int cols = (int)cols64;

        double[] accum = new double[(long)rows * cols];
        bool anyScattered = false;

        IReadOnlyList<(long ChildEntityId, int Position)> children =
            await EntityReader.GetSequenceChildrenAsync(tensorEntityId, ct);
        if (children.Count > 0)
        {
            foreach ((long childId, int pos) in children)
            {
                ct.ThrowIfCancellationRequested();
                if (pos < 0 || pos >= rows) { continue; }
                double[]? coords = await _physicalityReader.GetLineString4dAsync(
                    childId, "contour", ct);
                if (coords is null) { continue; }
                int take = Math.Min(cols, coords.Length);
                long rowBase = (long)pos * cols;
                for (int c = 0; c < take; c++)
                {
                    accum[rowBase + c] = coords[c];
                }
                anyScattered = true;
            }
        }

        if (anyScattered)
        {
            PackToWire(accum, dtype, buffer);
            return buffer;
        }

        // Fallback: SVD reconstruction (only meaningful for true 2-D tensors).
        if (shape.Length != 2 || shape[1] <= 0)
        {
            return buffer;
        }
        int m = rows;
        int n = cols;
        IReadOnlyList<long> rankComponentIds = await EntityReader.GetOutboundEdgeTargetsAsync(
            tensorEntityId, "has_rank_component", ct);
        if (rankComponentIds.Count == 0)
        {
            return buffer;
        }
        for (int rank = 0; rank < rankComponentIds.Count; rank++)
        {
            ct.ThrowIfCancellationRequested();
            double[]? coords = await _physicalityReader.GetLineString4dAsync(
                rankComponentIds[rank], "contour", ct);
            if (coords is null || coords.Length < 1 + m + n) { continue; }

            double sigma = coords[0];
            for (int r = 0; r < m; r++)
            {
                double ur = coords[1 + r];
                double sigmaUr = sigma * ur;
                long rowBase = (long)r * n;
                int vBase = 1 + m;
                for (int c = 0; c < n; c++)
                {
                    accum[rowBase + c] += sigmaUr * coords[vBase + c];
                }
            }
        }
        PackToWire(accum, dtype, buffer);
        return buffer;
    }

    /// <summary>
    /// Walks <paramref name="edgeTypeCode"/> from a tensor entity to its
    /// per-role unit, then reads the unit's contour physicality. Returns
    /// null if the edge or the unit's contour is absent (or shorter than
    /// the requested element count). Used for 1-D tensor reconstruction
    /// when the tensor itself carries no contour but a unit does (the
    /// LayerNormPass / RopeFreqPass cases).
    /// </summary>
    private async Task<double[]?> TryReadUnitContourAsync(
        long tensorEntityId, string edgeTypeCode, int minLength, CancellationToken ct)
    {
        if (_physicalityReader is null) { return null; }
        IReadOnlyList<long> targets = await EntityReader.GetOutboundEdgeTargetsAsync(
            tensorEntityId, edgeTypeCode, ct);
        foreach (long unitId in targets)
        {
            double[]? contour = await _physicalityReader.GetLineString4dAsync(
                unitId, "contour", ct);
            if (contour is not null && contour.Length >= minLength)
            {
                return contour;
            }
        }
        return null;
    }

    /// <summary>
    /// Packs an f64 accumulator into the wire bytes for the requested dtype.
    /// Mirrors the decode path in SafetensorsReader.DecodeChunk so a tensor
    /// emitted here parses back to the same logical values.
    /// </summary>
    private static void PackToWire(double[] accum, string dtype, byte[] buffer)
    {
        switch (dtype)
        {
            case "F64":
                for (int i = 0; i < accum.Length; i++)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(
                        buffer.AsSpan(i * 8, 8), accum[i]);
                }
                break;
            case "F32":
                for (int i = 0; i < accum.Length; i++)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                        buffer.AsSpan(i * 4, 4), (float)accum[i]);
                }
                break;
            case "F16":
                for (int i = 0; i < accum.Length; i++)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteHalfLittleEndian(
                        buffer.AsSpan(i * 2, 2), (Half)(float)accum[i]);
                }
                break;
            case "BF16":
                for (int i = 0; i < accum.Length; i++)
                {
                    int bits = BitConverter.SingleToInt32Bits((float)accum[i]);
                    ushort bf = (ushort)(bits >> 16);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                        buffer.AsSpan(i * 2, 2), bf);
                }
                break;
            // Integer dtypes: round-to-nearest within the dtype's representable
            // range. The substrate's per-rank f64 reconstruction may exceed
            // the int range (e.g., for I8); clamp.
            case "I8":
                for (int i = 0; i < accum.Length; i++)
                {
                    int v = (int)Math.Round(accum[i]);
                    if (v < sbyte.MinValue) { v = sbyte.MinValue; }
                    else if (v > sbyte.MaxValue) { v = sbyte.MaxValue; }
                    buffer[i] = (byte)(sbyte)v;
                }
                break;
            case "U8":
                for (int i = 0; i < accum.Length; i++)
                {
                    int v = (int)Math.Round(accum[i]);
                    if (v < 0) { v = 0; }
                    else if (v > byte.MaxValue) { v = byte.MaxValue; }
                    buffer[i] = (byte)v;
                }
                break;
            case "I16":
                for (int i = 0; i < accum.Length; i++)
                {
                    int v = (int)Math.Round(accum[i]);
                    if (v < short.MinValue) { v = short.MinValue; }
                    else if (v > short.MaxValue) { v = short.MaxValue; }
                    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(
                        buffer.AsSpan(i * 2, 2), (short)v);
                }
                break;
            case "I32":
                for (int i = 0; i < accum.Length; i++)
                {
                    int v = (int)Math.Round(accum[i]);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                        buffer.AsSpan(i * 4, 4), v);
                }
                break;
            case "I64":
                for (int i = 0; i < accum.Length; i++)
                {
                    long v = (long)Math.Round(accum[i]);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
                        buffer.AsSpan(i * 8, 8), v);
                }
                break;
            case "BOOL":
                for (int i = 0; i < accum.Length; i++)
                {
                    buffer[i] = accum[i] != 0 ? (byte)1 : (byte)0;
                }
                break;
            // FP8 variants and unsigned-int wider dtypes: leave zero (no
            // ingestion-side support yet for the inverse encoding path).
            default:
                // Zero-fill stays — the buffer is already new byte[] (zeros).
                break;
        }
    }

    /// <summary>
    /// Resolves the model_architecture entity's display name via the
    /// has_architecture_name edge emitted by ModelPassOrchestrator.BootstrapAsync
    /// (migration 0044). The target is a substrate document carrying the
    /// architecture class string ("BertModel", "Qwen2ForCausalLM", etc.).
    /// Falls back to the entity id when the edge or its target is absent.
    /// </summary>
    private async Task<string> ResolveModelNameAsync(
        long modelArchitectureEntityId, RecompositionOptions options, CancellationToken ct)
    {
        IReadOnlyList<long> nameTargets = await EntityReader.GetOutboundEdgeTargetsAsync(
            modelArchitectureEntityId, "has_architecture_name", ct);
        if (nameTargets.Count > 0 && _textReader is not null)
        {
            string? name = await _textReader.RecomposeTextAsync(nameTargets[0], options.MaxDepth, ct);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }
        return $"model_{modelArchitectureEntityId}";
    }

    /// <summary>Parse a shape literal like "[2048, 2048]" or "2048x2048" into an int[].</summary>
    private static int[] ParseShape(string text)
    {
        string trimmed = text.Trim().Trim('[', ']');
        if (trimmed.Length == 0)
        {
            return [];
        }
        string[] parts = trimmed.Contains(',', StringComparison.Ordinal)
            ? trimmed.Split(',')
            : trimmed.Split('x', 'X');
        int[] dims = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            dims[i] = int.Parse(parts[i].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }
        return dims;
    }

    /// <summary>Bytes per element for the dtypes safetensors carries.</summary>
    private static int BytesPerElement(string dtype) => dtype switch
    {
        "F64" => 8,
        "F32" => 4,
        "F16" or "BF16" => 2,
        "I64" or "U64" => 8,
        "I32" or "U32" => 4,
        "I16" or "U16" => 2,
        "I8" or "U8" or "BOOL" => 1,
        "F8_E4M3" or "F8_E5M2" => 1,
        _ => throw new NotSupportedException($"Unknown safetensors dtype '{dtype}'"),
    };
}
