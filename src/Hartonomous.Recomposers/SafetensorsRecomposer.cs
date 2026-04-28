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
            byte[] bytes = await AssembleTensorBytesAsync(tensor, dtype, shape, ct);
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
            byte[] bytes = await AssembleTensorBytesAsync(tensor, dtype, shape, ct);
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
        await SafetensorsWriter.WriteAsync(file, output, ct);
    }

    private async Task<(string Name, string Dtype, int[] Shape)> ReadTensorMetadataAsync(
        EntityHandle tensor, RecompositionOptions options, CancellationToken ct)
    {
        string name = $"tensor_{Convert.ToHexString(tensor.Hash)[..8].ToLowerInvariant()}";
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
    private async Task<byte[]> AssembleTensorBytesAsync(
        EntityHandle tensor, string dtype, int[] shape, CancellationToken ct)
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
        PackToWire(accum, dtype, buffer);
        return buffer;
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
            default:
                break;
        }
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
        return $"model_{Convert.ToHexString(modelArchitecture.Hash)[..8].ToLowerInvariant()}";
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
