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

    public async Task<SafetensorsFile> RecomposeModelSourceAsync(
        long modelSourceId,
        RecompositionOptions options,
        CancellationToken ct)
    {
        if (_query is null)
        {
            throw new InvalidOperationException(
                $"{nameof(RecomposeModelSourceAsync)} requires an {nameof(ISubstrateQuery)} implementation.");
        }

        IReadOnlyList<PackageTensorHandle> packageTensors =
            await _query.QueryTensorsForModelSourceAsync(modelSourceId, ct);
        if (packageTensors.Count == 0)
        {
            return new SafetensorsFile(new Dictionary<string, TensorData>(StringComparer.Ordinal),
                $"model_source_{modelSourceId}");
        }

        Dictionary<string, TensorData> tensors = new(packageTensors.Count, StringComparer.Ordinal);
        foreach (PackageTensorHandle packageTensor in packageTensors)
        {
            ct.ThrowIfCancellationRequested();
            EntityHandle tensor = packageTensor.Tensor;
            (string name, string dtype, int[] shape) = await ReadTensorMetadataAsync(tensor, options, ct);
            byte[] bytes = await AssembleTensorBytesAsync(tensor, dtype, shape, options, ct);
            tensors[name] = new TensorData(dtype, shape, bytes);
        }

        string modelName = await ResolveModelNameAsync(packageTensors[0].Package, options, ct);
        return new SafetensorsFile(tensors, modelName);
    }

    public override Modality OutputModality => Modality.ModelWeights;

    public override async Task<SafetensorsFile> RecomposeAsync(
        EntityHandle entity,
        RecompositionOptions options,
        CancellationToken ct)
    {
        IReadOnlyList<EntityHandle> tensorHandles = entity.EntityTypeCode == "model_package"
            ? await ReadPackageTensorSequenceAsync(entity, ct)
            : await EntityReader.GetOutboundEdgeTargetsAsync(entity, "has_tensor", ct);

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

    private async Task<IReadOnlyList<EntityHandle>> ReadPackageTensorSequenceAsync(
        EntityHandle package,
        CancellationToken ct)
    {
        IReadOnlyList<(EntityHandle Child, int Position)> children =
            await EntityReader.GetCompositionChildrenAsync(package, ct);
        List<(EntityHandle Tensor, int Position)> ordered = new(children.Count);
        foreach ((EntityHandle child, int position) in children)
        {
            if (string.Equals(child.EntityTypeCode, "tensor", StringComparison.Ordinal))
            {
                ordered.Add((child, position));
            }
            else if (string.Equals(child.EntityTypeCode, "model_package_tensor", StringComparison.Ordinal))
            {
                IReadOnlyList<(EntityHandle Child, int Position)> occurrenceChildren =
                    await EntityReader.GetCompositionChildrenAsync(child, ct);
                foreach ((EntityHandle occurrenceChild, int occurrencePosition) in occurrenceChildren)
                {
                    if (occurrencePosition == 1
                        && string.Equals(occurrenceChild.EntityTypeCode, "tensor", StringComparison.Ordinal))
                    {
                        ordered.Add((occurrenceChild, position));
                        break;
                    }
                }
            }
        }
        ordered.Sort(static (left, right) => left.Position.CompareTo(right.Position));
        EntityHandle[] tensors = new EntityHandle[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
        {
            tensors[i] = ordered[i].Tensor;
        }
        return tensors;
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

        List<TensorEntry> entries = new(file.Tensors.Count);
        foreach (KeyValuePair<string, TensorData> kv in file.Tensors)
        {
            entries.Add(new TensorEntry(kv.Key, kv.Value.Data.Length));
        }
        IReadOnlyList<ShardPlan> plans =
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

        foreach (ShardPlan plan in plans)
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
        int bytesPerElement = SafetensorsDtypePacker.BytesPerElement(dtype);
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
                tensor, "entity", ct);
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
            SafetensorsDtypePacker.PackToWire(sliced, dtype, buffer);
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
                    childHandle, "entity", ct);
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
            SafetensorsDtypePacker.PackToWire(accum, dtype, buffer);
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
                rankComponents[rank], "entity", ct);
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
        SafetensorsDtypePacker.PackToWire(accum, dtype, buffer);
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
                unit, "entity", ct);
            if (contour is not null && contour.Length >= minLength)
            {
                return contour;
            }
        }
        return null;
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

}
