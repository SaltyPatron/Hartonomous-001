using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Analysis;
using Hartonomous.Core.Data;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Query;
using Hartonomous.Core.Recomposition;

namespace Hartonomous.Recomposers;

/// <summary>
/// Serializes substrate state into the safetensors wire format. NOT an AI
/// operation — AI lives in substrate traversal. This recomposer is a data
/// export pipe: query the substrate, assemble per-tensor (dtype, shape, bytes),
/// pack into the standard safetensors container via SafetensorsWriter.
///
/// Hash-as-PK throughout: addresses every entity by composite EntityHandle.
/// Per docs/specs/csharp/recomposers.md.
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
    /// Distilled export: pulls only tensors matching <paramref name="filter"/>.
    /// Per architecture.md "Distillation = WHERE clause".
    /// </summary>
    public async Task<SafetensorsFile> RecomposeFilteredAsync(
        EntityHandle modelArchitecture,
        SubstrateQueryFilter filter,
        RecompositionOptions options,
        CancellationToken ct)
    {
        IReadOnlyList<EntityHandle> tensorHandles = _query is not null
            ? await _query.QueryTensorsForArchitectureAsync(modelArchitecture, filter, ct)
            : await EntityReader.GetOutboundEdgeTargetsAsync(modelArchitecture, "has_tensor", ct);

        Dictionary<string, TensorData> tensors = new(tensorHandles.Count, StringComparer.Ordinal);
        foreach (EntityHandle tensor in tensorHandles)
        {
            ct.ThrowIfCancellationRequested();
            (string name, string dtype, int[] shape) = await ReadTensorMetadataAsync(tensor, options, ct);
            byte[] bytes = await AssembleTensorBytesAsync(tensor, dtype, shape, options, ct);
            tensors[name] = new TensorData(dtype, shape, bytes);
        }

        string modelName = await ResolveModelNameAsync(modelArchitecture, options, ct);
        return new SafetensorsFile(tensors, modelName);
    }

    public override Modality OutputModality => Modality.ModelWeights;

    public override async Task<SafetensorsFile> RecomposeAsync(
        EntityHandle entity,
        RecompositionOptions options,
        CancellationToken ct)
    {
        IReadOnlyList<EntityHandle> tensorHandles = await EntityReader.GetOutboundEdgeTargetsAsync(
            entity, "has_tensor", ct);

        Dictionary<string, TensorData> tensors = new(tensorHandles.Count, StringComparer.Ordinal);
        foreach (EntityHandle tensor in tensorHandles)
        {
            ct.ThrowIfCancellationRequested();
            (string name, string dtype, int[] shape) = await ReadTensorMetadataAsync(tensor, options, ct);
            byte[] bytes = await AssembleTensorBytesAsync(tensor, dtype, shape, options, ct);
            tensors[name] = new TensorData(dtype, shape, bytes);
        }

        string modelName = await ResolveModelNameAsync(entity, options, ct);
        return new SafetensorsFile(tensors, modelName);
    }

    public override async Task RecomposeToStreamAsync(
        EntityHandle entity,
        RecompositionOptions options,
        Stream output,
        CancellationToken ct)
    {
        SafetensorsFile file = await RecomposeAsync(entity, options, ct);
        IReadOnlyDictionary<string, string>? auditMetadata = BuildAuditMetadata(options);
        await SafetensorsWriter.WriteAsync(file, output, auditMetadata, ct);
    }

    /// <summary>
    /// Sharded recompose: writes one or more model-NNNNN-of-MMMMM.safetensors
    /// shards plus a model.safetensors.index.json into <paramref name="outputDir"/>,
    /// honoring <see cref="RecompositionOptions.MaxShardBytes"/>. Loads in
    /// HuggingFace transformers / vLLM / llama.cpp without modification.
    /// </summary>
    public async Task RecomposeToShardsAsync(
        EntityHandle entity,
        RecompositionOptions options,
        string outputDir,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        Directory.CreateDirectory(outputDir);

        SafetensorsFile file = await RecomposeAsync(entity, options, ct);
        IReadOnlyDictionary<string, string>? auditMetadata = BuildAuditMetadata(options);

        List<ShardSplitter.TensorEntry> entries = new(file.Tensors.Count);
        foreach (KeyValuePair<string, TensorData> kv in file.Tensors)
        {
            entries.Add(new ShardSplitter.TensorEntry(kv.Key, kv.Value.Data.Length));
        }
        IReadOnlyList<ShardSplitter.ShardPlan> plans =
            ShardSplitter.Plan(entries, options.MaxShardBytes);

        if (plans.Count == 0)
        {
            return;
        }

        if (plans.Count == 1)
        {
            string single = Path.Combine(outputDir, ShardSplitter.ShardFileName(1, 1));
            await using FileStream fs = File.Create(single);
            await SafetensorsWriter.WriteAsync(file, fs, auditMetadata, ct);
            return;
        }

        foreach (ShardSplitter.ShardPlan plan in plans)
        {
            ct.ThrowIfCancellationRequested();
            Dictionary<string, TensorData> shardTensors = new(plan.TensorNames.Count, StringComparer.Ordinal);
            foreach (string name in plan.TensorNames)
            {
                shardTensors[name] = file.Tensors[name];
            }
            SafetensorsFile shardFile = new(shardTensors, file.ModelName);
            string path = Path.Combine(outputDir, ShardSplitter.ShardFileName(plan.ShardIndex, plan.ShardCount));
            await using FileStream fs = File.Create(path);
            await SafetensorsWriter.WriteAsync(shardFile, fs, auditMetadata, ct);
        }

        string indexPath = Path.Combine(outputDir, "model.safetensors.index.json");
        await File.WriteAllTextAsync(indexPath, ShardSplitter.BuildIndexJson(plans), ct);
    }

    private static Dictionary<string, string>? BuildAuditMetadata(RecompositionOptions options)
    {
        if (!options.IncludeProvenance)
        {
            return null;
        }
        Dictionary<string, string> meta = new(StringComparer.Ordinal)
        {
            ["hartonomous_recomposer_version"] = "v1",
            ["hartonomous_mode"] = options.Mode.ToString(),
            ["hartonomous_refinement_policy"] = options.RefinementPolicy.ToString(),
            ["hartonomous_quantization_policy"] = options.QuantizationPolicy.ToString(),
            ["hartonomous_lora_policy"] = options.LoraPolicy.ToString(),
        };
        if (!string.IsNullOrEmpty(options.RecipeId))
        {
            meta["hartonomous_recipe_id"] = options.RecipeId;
        }
        if (!string.IsNullOrEmpty(options.RequantizeTarget))
        {
            meta["hartonomous_requantize_target"] = options.RequantizeTarget;
        }
        if (options.ArenaCodes is { Count: > 0 } arenaCodes)
        {
            meta["hartonomous_arena_codes"] = string.Join(",", arenaCodes);
        }
        if (options.SignificanceThreshold > 0)
        {
            meta["hartonomous_significance_threshold"] =
                options.SignificanceThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (options.NoiseFloor > 0)
        {
            meta["hartonomous_noise_floor"] =
                options.NoiseFloor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return meta;
    }

    private async Task<(string Name, string Dtype, int[] Shape)> ReadTensorMetadataAsync(
        EntityHandle tensor, RecompositionOptions options, CancellationToken ct)
    {
        string name = $"tensor_{tensor.Hash.ToHexString()[..8].ToLowerInvariant()}";
        string dtype = "F32";
        int[] shape = [];

        IReadOnlyList<EntityHandle> nameTargets = await EntityReader.GetOutboundEdgeTargetsAsync(
            tensor, "has_tensor_name", ct);
        IReadOnlyList<EntityHandle> dtypeTargets = await EntityReader.GetOutboundEdgeTargetsAsync(
            tensor, "has_dtype", ct);
        IReadOnlyList<EntityHandle> shapeTargets = await EntityReader.GetOutboundEdgeTargetsAsync(
            tensor, "has_shape", ct);

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
    /// outcome with no per-position content is a zero-filled buffer of the
    /// correct size — Law #11 sparsity.
    /// </summary>
    private Task<byte[]> AssembleTensorBytesAsync(
        EntityHandle tensor, string dtype, int[] shape, CancellationToken ct)
        => AssembleTensorBytesAsync(tensor, dtype, shape, RecompositionOptions.Default, ct);

    private async Task<byte[]> AssembleTensorBytesAsync(
        EntityHandle tensor, string dtype, int[] shape, RecompositionOptions options, CancellationToken ct)
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
                $"Negative byte count for tensor {tensor} ({dtype} {string.Join('x', shape)}).");
        }
        if (totalBytes > int.MaxValue)
        {
            throw new NotSupportedException(
                $"Tensor {tensor} ({dtype} {string.Join('x', shape)}) exceeds int.MaxValue bytes; sharded output not yet implemented.");
        }

        byte[] buffer = new byte[totalBytes];

        if (_physicalityReader is null)
        {
            return buffer;
        }

        // 1-D: tensor-attached contour first, then walk to per-role unit.
        if (shape.Length == 1 && shape[0] > 0)
        {
            int length = shape[0];
            double[]? values = await _physicalityReader.GetLineString4dAsync(
                tensor, "contour", ct);
            if (values is null || values.Length < length)
            {
                values = await TryReadUnitContourAsync(tensor, "has_layer_norm_scale", length, ct)
                       ?? await TryReadUnitContourAsync(tensor, "has_rope_freqs", length, ct);
            }
            if (values is null || values.Length < length)
            {
                return buffer;
            }
            double[] sliced = new double[length];
            Array.Copy(values, sliced, length);
            ApplyNoiseFloor(sliced, options.NoiseFloor);
            PackToWire(sliced, dtype, buffer);
            return buffer;
        }

        // ≥2-D: per-role unit scatter via has_constituent children.
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

        IReadOnlyList<(EntityHandle Child, int Position)> children =
            await EntityReader.GetCompositionChildrenAsync(tensor, ct);
        if (children.Count > 0)
        {
            foreach ((EntityHandle childHandle, int rawPos) in children)
            {
                ct.ThrowIfCancellationRequested();
                // Position from get_composition_children is 1-based; tensor row index is 0-based.
                int pos = rawPos - 1;
                if (pos < 0 || pos >= rows) { continue; }
                double[]? coords = await _physicalityReader.GetLineString4dAsync(
                    childHandle, "contour", ct);
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
            ApplyNoiseFloor(accum, options.NoiseFloor);
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
        IReadOnlyList<EntityHandle> rankComponents = await EntityReader.GetOutboundEdgeTargetsAsync(
            tensor, "has_rank_component", ct);
        if (rankComponents.Count == 0)
        {
            return buffer;
        }
        for (int rank = 0; rank < rankComponents.Count; rank++)
        {
            ct.ThrowIfCancellationRequested();
            double[]? coords = await _physicalityReader.GetLineString4dAsync(
                rankComponents[rank], "contour", ct);
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
        ApplyNoiseFloor(accum, options.NoiseFloor);
        PackToWire(accum, dtype, buffer);
        return buffer;
    }

    private static void ApplyNoiseFloor(double[] values, double noiseFloor)
    {
        if (noiseFloor <= 0 || values is null) { return; }
        for (int i = 0; i < values.Length; i++)
        {
            if (Math.Abs(values[i]) < noiseFloor)
            {
                values[i] = 0.0;
            }
        }
    }

    private async Task<double[]?> TryReadUnitContourAsync(
        EntityHandle tensor, string edgeTypeCode, int minLength, CancellationToken ct)
    {
        if (_physicalityReader is null) { return null; }
        IReadOnlyList<EntityHandle> targets = await EntityReader.GetOutboundEdgeTargetsAsync(
            tensor, edgeTypeCode, ct);
        foreach (EntityHandle unit in targets)
        {
            double[]? contour = await _physicalityReader.GetLineString4dAsync(
                unit, "contour", ct);
            if (contour is not null && contour.Length >= minLength)
            {
                return contour;
            }
        }
        return null;
    }

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
            case "F8_E4M3":
                for (int i = 0; i < accum.Length; i++)
                {
                    buffer[i] = F32ToE4M3((float)accum[i]);
                }
                break;
            case "F8_E5M2":
                for (int i = 0; i < accum.Length; i++)
                {
                    buffer[i] = F32ToE5M2((float)accum[i]);
                }
                break;
            default:
                throw new NotSupportedException($"PackToWire: dtype '{dtype}' not implemented.");
        }
    }

    /// <summary>
    /// IEEE-style float32 → FP8 E4M3 (1 sign / 4 exp / 3 mantissa, bias 7).
    /// No infinity encoding; NaN = S.1111.111; max normal = ±448 (S.1111.110).
    /// Round-to-nearest-even. Overflow saturates to ±448.
    /// </summary>
    private static byte F32ToE4M3(float x)
    {
        if (float.IsNaN(x)) { return 0x7F; }
        int bits = BitConverter.SingleToInt32Bits(x);
        int sign = (bits >>> 31) & 1;
        int rawExp = (bits >> 23) & 0xFF;
        int mant23 = bits & 0x7FFFFF;
        if (rawExp == 0 && mant23 == 0) { return (byte)(sign << 7); }
        if (float.IsInfinity(x)) { return (byte)((sign << 7) | 0x7E); }
        int unbiased = rawExp - 127;

        // Subnormal range: 2^-9 .. 2^-7 (smallest subnormal = 2^-9 with mant=0b001).
        if (unbiased < -9)
        {
            // Below smallest subnormal — round only if 2^-10 with rounding-up; else zero.
            // Conservative: flush to zero.
            return (byte)(sign << 7);
        }
        if (unbiased < -6)
        {
            int shift = -6 - unbiased;            // 1..3
            int implicit24 = mant23 | 0x800000;   // include implicit 1
            int dropBits = 20 + shift;            // produce 3-bit mantissa
            int roundBit = 1 << (dropBits - 1);
            int lowerMask = roundBit - 1;
            int high = implicit24 >> dropBits;
            int lower = implicit24 & lowerMask;
            if ((implicit24 & roundBit) != 0 && (lower != 0 || (high & 1) != 0)) { high++; }
            if (high > 0x7)
            {
                // Carried into normal exp=1 (smallest normal in E4M3).
                return (byte)((sign << 7) | (1 << 3) | (high & 0x7));
            }
            return (byte)((sign << 7) | high);
        }

        // Normal: bias 7, 3-bit mantissa.
        int biased = unbiased + 7;
        int dropBits2 = 20;
        int roundBit2 = 1 << (dropBits2 - 1);
        int lowerMask2 = roundBit2 - 1;
        int high2 = mant23 >> dropBits2;
        int lower2 = mant23 & lowerMask2;
        if ((mant23 & roundBit2) != 0 && (lower2 != 0 || (high2 & 1) != 0))
        {
            high2++;
            if (high2 == 8) { high2 = 0; biased++; }
        }
        // Saturate at ±448 (biased=15, mant=6). 0x7F is NaN — never produce it for finite input.
        if (biased > 15 || (biased == 15 && high2 >= 7))
        {
            return (byte)((sign << 7) | 0x7E);
        }
        return (byte)((sign << 7) | (biased << 3) | high2);
    }

    /// <summary>
    /// IEEE-style float32 → FP8 E5M2 (1 sign / 5 exp / 2 mantissa, bias 15).
    /// IEEE-shaped: ±inf at S.11111.00, NaN at S.11111.{nonzero}.
    /// Round-to-nearest-even. Overflow → ±inf.
    /// </summary>
    private static byte F32ToE5M2(float x)
    {
        if (float.IsNaN(x)) { return 0x7E; }
        int bits = BitConverter.SingleToInt32Bits(x);
        int sign = (bits >>> 31) & 1;
        int rawExp = (bits >> 23) & 0xFF;
        int mant23 = bits & 0x7FFFFF;
        if (rawExp == 0 && mant23 == 0) { return (byte)(sign << 7); }
        if (float.IsInfinity(x)) { return (byte)((sign << 7) | 0x7C); }
        int unbiased = rawExp - 127;

        // Subnormal range for E5M2: smallest = 2^-16 (mant=01).
        if (unbiased < -16) { return (byte)(sign << 7); }
        if (unbiased < -14)
        {
            int shift = -14 - unbiased;           // 1..2
            int implicit24 = mant23 | 0x800000;
            int dropBits = 21 + shift;            // produce 2-bit mantissa
            int roundBit = 1 << (dropBits - 1);
            int lowerMask = roundBit - 1;
            int high = implicit24 >> dropBits;
            int lower = implicit24 & lowerMask;
            if ((implicit24 & roundBit) != 0 && (lower != 0 || (high & 1) != 0)) { high++; }
            if (high > 0x3)
            {
                return (byte)((sign << 7) | (1 << 2) | (high & 0x3));
            }
            return (byte)((sign << 7) | high);
        }

        int biased = unbiased + 15;
        int dropBits2 = 21;
        int roundBit2 = 1 << (dropBits2 - 1);
        int lowerMask2 = roundBit2 - 1;
        int high2 = mant23 >> dropBits2;
        int lower2 = mant23 & lowerMask2;
        if ((mant23 & roundBit2) != 0 && (lower2 != 0 || (high2 & 1) != 0))
        {
            high2++;
            if (high2 == 4) { high2 = 0; biased++; }
        }
        if (biased >= 31)
        {
            return (byte)((sign << 7) | 0x7C); // ±inf
        }
        return (byte)((sign << 7) | (biased << 2) | high2);
    }

    private async Task<string> ResolveModelNameAsync(
        EntityHandle modelArchitecture, RecompositionOptions options, CancellationToken ct)
    {
        IReadOnlyList<EntityHandle> nameTargets = await EntityReader.GetOutboundEdgeTargetsAsync(
            modelArchitecture, "has_architecture_name", ct);
        if (nameTargets.Count > 0 && _textReader is not null)
        {
            string? name = await _textReader.RecomposeTextAsync(nameTargets[0], options.MaxDepth, ct);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }
        return $"model_{modelArchitecture.Hash.ToHexString()[..8].ToLowerInvariant()}";
    }

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
