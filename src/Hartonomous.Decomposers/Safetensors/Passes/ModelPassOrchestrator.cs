using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Runs the registered <see cref="IModelAnalysisPass"/> set against one model:
///
///   1. Bootstrap — detect architecture, hash every tensor, create
///      <c>model_architecture</c> + <c>tensor</c> entities and the <c>has_tensor</c>
///      edges that bind them. Resolves entity ids and assembles the
///      <see cref="ModelPassContext"/>.
///   2. Topologically sort passes by <see cref="IModelAnalysisPass.Dependencies"/>
///      and filter by <see cref="IModelAnalysisPass.AppliesToArchitectures"/>.
///   3. Skip passes already recorded as completed in
///      <c>substrate.model_pass_checkpoint</c> for this <c>model_source_id</c>.
///   4. For each remaining pass: open a <see cref="PassSession"/>, run, flush,
///      mark completed; on failure mark in-flight with the error text and rethrow.
///
/// Failure isolation is the caller's responsibility: catch the throw at the
/// per-model boundary in the decomposer.
/// </summary>
internal sealed partial class ModelPassOrchestrator
{
    private readonly IComputeFacade _compute;
    private readonly IModelPassCheckpointStore _checkpointStore;
    private readonly IIngestionPipeline _pipeline;
    private readonly IProgressReporter _reporter;
    private readonly SafetensorsReferenceTableWriter _refWriter;
    private readonly IReadOnlyList<IModelAnalysisPass> _passes;
    private readonly ILogger _logger;
    private readonly int _batchSize;
    private readonly string _provenanceCode;

    public ModelPassOrchestrator(
        IComputeFacade compute,
        IModelPassCheckpointStore checkpointStore,
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        SafetensorsReferenceTableWriter refWriter,
        IReadOnlyList<IModelAnalysisPass> passes,
        ILogger logger,
        int batchSize,
        string provenanceCode)
    {
        _compute = compute;
        _checkpointStore = checkpointStore;
        _pipeline = pipeline;
        _reporter = reporter;
        _refWriter = refWriter;
        _passes = passes;
        _logger = logger;
        _batchSize = batchSize;
        _provenanceCode = provenanceCode;
    }

    public async Task RunAsync(
        DiscoveredModel model,
        long modelSourceId,
        int modelIdx,
        int totalModels,
        CancellationToken ct)
    {
        Stopwatch bootstrapSw = Stopwatch.StartNew();
        ModelPassContext context = await BootstrapAsync(model, modelSourceId, ct);
        bootstrapSw.Stop();
        Log.Bootstrapped(_logger, model.ModelId, context.Tensors.Count, bootstrapSw.ElapsedMilliseconds);

        IReadOnlySet<string> completed = await _checkpointStore.LoadCompletedPassIdsAsync(modelSourceId, ct);
        IReadOnlyList<IModelAnalysisPass> ordered = OrderPasses(_passes, context.Architecture.Architecture.ArchitectureClass);

        int passIdx = 0;
        foreach (IModelAnalysisPass pass in ordered)
        {
            ct.ThrowIfCancellationRequested();
            passIdx++;
            if (completed.Contains(pass.PassId))
            {
                Log.PassSkippedComplete(_logger, model.ModelId, pass.PassId, passIdx, ordered.Count);
                continue;
            }

            Log.PassStart(_logger, model.ModelId, pass.PassId, passIdx, ordered.Count);
            PassSession session = new(_pipeline, _reporter, context, _logger, pass.PassId);
            Stopwatch passSw = Stopwatch.StartNew();
            try
            {
                await pass.RunAsync(context, session, ct);
                await session.FlushAsync(ct);
                passSw.Stop();
                await _checkpointStore.MarkCompletedAsync(
                    modelSourceId, pass.PassId, session.EntitiesCreated, session.EdgesCreated, ct);
                Log.PassComplete(_logger, model.ModelId, pass.PassId,
                    session.EntitiesCreated, session.EdgesCreated, passSw.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                passSw.Stop();
                // Best-effort flush of whatever the pass managed to add before the throw.
                try { await session.FlushAsync(ct); } catch { /* the original exception is more important */ }
                await _checkpointStore.MarkInFlightAsync(
                    modelSourceId, pass.PassId, session.EntitiesCreated, session.EdgesCreated,
                    lastError: ex.ToString(), ct);
                Log.PassFailed(_logger, ex, model.ModelId, pass.PassId, passSw.ElapsedMilliseconds);
                throw;
            }
        }

        Log.ModelComplete(_logger, model.ModelId, ordered.Count, modelIdx, totalModels);
    }

    private async Task<ModelPassContext> BootstrapAsync(
        DiscoveredModel model, long modelSourceId, CancellationToken ct)
    {
        ModelArchitecture arch = ArchitectureDetector.DetectFromConfig(model.ConfigPath, model.ModelId);
        Log.ArchitectureDetected(_logger, arch.ArchitectureClass, arch.HiddenSize, arch.NumLayers, arch.NumAttentionHeads);

        int archClassId = await _refWriter.EnsureArchitectureClassAsync(arch.ArchitectureClass, ct);
        Dictionary<string, int> tensorRoleMap = await _refWriter.LoadTensorRoleMapAsync(ct);

        byte[] archHash = BuildArchitectureSignature(arch);

        IIngestionBatch batch = _pipeline.CreateBatch();
        EntityHandle modelEntity = batch.AddEntity(archHash, "model_architecture");
        batch.AddJunction("model_architecture_class", modelEntity, archClassId);
        batch.AddEntityModelSource(modelEntity, modelSourceId);

        List<SafetensorsTensorInfo> rawTensors = [];
        foreach (string st in model.SafetensorsFiles)
        {
            rawTensors.AddRange(SafetensorsReader.ReadHeader(st));
        }
        Log.TensorsFound(_logger, rawTensors.Count, model.SafetensorsFiles.Count);

        // Hash + classify each tensor; queue the (info, hash, classification) triples
        // so we can resolve their entity_ids in one shot after the bootstrap batches commit.
        List<(SafetensorsTensorInfo Info, TensorClassification Classification, byte[] Hash)> staged = [];
        int tensorIdx = 0;
        foreach (SafetensorsTensorInfo tensor in rawTensors)
        {
            ct.ThrowIfCancellationRequested();
            tensorIdx++;
            TensorClassification cls = TensorClassifier.Classify(tensor.Name, arch.ArchitectureClass);
            if (cls.Role == TensorRole.Unknown)
            {
                Log.TensorSkippedUnknown(_logger, tensorIdx, rawTensors.Count, tensor.Name);
                continue;
            }

            Stopwatch hashSw = Stopwatch.StartNew();
            byte[] tensorHash = HashTensorStreaming(tensor);
            hashSw.Stop();
            Log.TensorHashed(_logger, tensorIdx, tensor.Name, hashSw.ElapsedMilliseconds);

            EntityHandle tensorH = batch.AddEntity(tensorHash, "tensor");
            batch.AddEntityModelSource(tensorH, modelSourceId);
            if (tensorRoleMap.TryGetValue(cls.Role.ToCode(), out int roleId))
            {
                batch.AddJunction("tensor_tensor_role", tensorH, roleId);
            }
            batch.AddEdge("has_tensor", _provenanceCode,
            [
                new EdgeMemberSpec(modelEntity, null, "source", 0),
                new EdgeMemberSpec(tensorH, null, "target", 1),
            ]);
            staged.Add((tensor, cls, tensorHash));

            if (batch.EntityCount >= _batchSize || batch.EdgeCount >= _batchSize)
            {
                await _pipeline.SubmitBatchAsync(batch, ct);
                batch = _pipeline.CreateBatch();
                modelEntity = batch.AddEntity(archHash, "model_architecture");
                batch.AddEntityModelSource(modelEntity, modelSourceId);
            }
        }

        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await _pipeline.SubmitBatchAsync(batch, ct);
        }

        List<byte[]> hashesToResolve = new(staged.Count + 1) { archHash };
        foreach (var s in staged)
        {
            hashesToResolve.Add(s.Hash);
        }
        IReadOnlyDictionary<byte[], long> idMap = await _pipeline.ResolveEntityIdsAsync(hashesToResolve, ct);

        long modelEntityId = LookupId(idMap, archHash, "model_architecture");
        ModelArchitectureHandle archHandle = new(arch, archClassId, archHash, modelEntityId);

        List<TensorHandle> tensorHandles = new(staged.Count);
        foreach (var s in staged)
        {
            long id = LookupId(idMap, s.Hash, $"tensor:{s.Info.Name}");
            tensorHandles.Add(new TensorHandle(s.Info, s.Classification, s.Hash, id));
        }

        ModelSourceHandle sourceHandle = new(
            modelSourceId, model.PublisherSlug, model.ModelSlug, model.Revision, model.RevisionHex, model.ModelId);
        string checkpointKey = $"model_source:{modelSourceId}";

        return new ModelPassContext(
            Source: sourceHandle,
            Architecture: archHandle,
            Tensors: tensorHandles,
            Compute: _compute,
            TensorRoleMap: tensorRoleMap,
            CheckpointKey: checkpointKey,
            ProvenanceCode: _provenanceCode);
    }

    private static long LookupId(IReadOnlyDictionary<byte[], long> map, byte[] hash, string label)
    {
        if (map.TryGetValue(hash, out long id))
        {
            return id;
        }
        throw new InvalidOperationException(
            $"Bootstrap could not resolve substrate entity_id for {label} (hash {Convert.ToHexString(hash)}).");
    }

    /// <summary>
    /// Topological sort over <see cref="IModelAnalysisPass.Dependencies"/>, filtered
    /// to passes whose <see cref="IModelAnalysisPass.AppliesToArchitectures"/> matches
    /// the model's architecture class. A dependency that filters out is ignored —
    /// passes that depend on a skipped pass are themselves skipped if their dep
    /// would have produced their input. (Spec rule: dependencies are SAME-MODEL only.)
    /// </summary>
    internal static IReadOnlyList<IModelAnalysisPass> OrderPasses(
        IReadOnlyList<IModelAnalysisPass> passes, string architectureClass)
    {
        Dictionary<string, IModelAnalysisPass> all = passes.ToDictionary(p => p.PassId, StringComparer.Ordinal);
        HashSet<string> applicable = new(StringComparer.Ordinal);
        foreach (IModelAnalysisPass p in passes)
        {
            if (p.AppliesToArchitectures.Count == 0 ||
                p.AppliesToArchitectures.Contains(architectureClass, StringComparer.Ordinal))
            {
                applicable.Add(p.PassId);
            }
        }

        Dictionary<string, int> indeg = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> dependents = new(StringComparer.Ordinal);
        foreach (string id in applicable)
        {
            indeg[id] = 0;
            dependents[id] = [];
        }
        foreach (string id in applicable)
        {
            foreach (string dep in all[id].Dependencies)
            {
                if (!applicable.Contains(dep))
                {
                    continue;
                }
                indeg[id]++;
                dependents[dep].Add(id);
            }
        }

        // Stable order: sort initial frontier and ties by PassId so the resulting
        // execution order is deterministic across runs (Law #6).
        SortedSet<string> ready = new(StringComparer.Ordinal);
        foreach ((string id, int d) in indeg)
        {
            if (d == 0)
            {
                ready.Add(id);
            }
        }

        List<IModelAnalysisPass> ordered = new(applicable.Count);
        while (ready.Count > 0)
        {
            string next = ready.Min!;
            ready.Remove(next);
            ordered.Add(all[next]);
            foreach (string child in dependents[next])
            {
                if (--indeg[child] == 0)
                {
                    ready.Add(child);
                }
            }
        }

        if (ordered.Count != applicable.Count)
        {
            IEnumerable<string> stuck = applicable.Where(id => indeg[id] > 0);
            throw new InvalidOperationException(
                $"Pass DAG has a cycle or unresolved dependency among applicable passes: {string.Join(", ", stuck)}");
        }

        return ordered;
    }

    private byte[] BuildArchitectureSignature(ModelArchitecture arch)
        => new CanonicalSignatureBuilder(_compute.Common, "arch")
            .WriteUtf8(arch.ArchitectureClass)
            .WriteUtf8(arch.ModelType)
            .WriteInt32LE(arch.HiddenSize)
            .WriteInt32LE(arch.NumLayers)
            .WriteInt32LE(arch.NumAttentionHeads)
            .WriteInt32LE(arch.VocabSize)
            .WriteInt32LE(arch.IntermediateSize)
            .WriteInt32LE(arch.MaxPositionEmbeddings)
            .Finalize();

    /// <summary>
    /// Canonical content hash of a tensor: kind tag "tens" + dtype int + rank +
    /// shape (each int64 LE) + raw tensor bytes streamed via the Blake3 hasher.
    /// Replaces the previous string-interpolated descriptor — the canonical
    /// builder rule (no string.Join, no $"...") applies to every entity hash.
    /// </summary>
    private byte[] HashTensorStreaming(SafetensorsTensorInfo tensor)
    {
        Blake3Hasher hasher = _compute.Common.CreateBlake3Hasher();
        FeedTensorPrefix(hasher, tensor);
        SafetensorsReader.StreamHash(tensor, hasher);
        return hasher.Finalize();
    }

    /// <summary>Single-pass hash + decode for Track-1 tensors that need their bytes as f64.</summary>
    internal byte[] HashTensorStreamingAndDecode(SafetensorsTensorInfo tensor, double[] flatResult)
    {
        Blake3Hasher hasher = _compute.Common.CreateBlake3Hasher();
        FeedTensorPrefix(hasher, tensor);
        SafetensorsReader.StreamHashAndDecode(tensor, hasher, flatResult);
        return hasher.Finalize();
    }

    private static void FeedTensorPrefix(Blake3Hasher hasher, SafetensorsTensorInfo tensor)
    {
        Span<byte> tag = stackalloc byte[4];
        Encoding.ASCII.GetBytes("tens", tag);
        hasher.Update(tag);

        Span<byte> int4 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(int4, (int)tensor.Dtype);
        hasher.Update(int4);
        BinaryPrimitives.WriteInt32LittleEndian(int4, tensor.Shape.Length);
        hasher.Update(int4);

        Span<byte> int8 = stackalloc byte[8];
        for (int i = 0; i < tensor.Shape.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(int8, tensor.Shape[i]);
            hasher.Update(int8);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Bootstrap {ModelId}: {TensorCount} tensors in {ElapsedMs}ms")]
        public static partial void Bootstrapped(ILogger logger, string modelId, int tensorCount, long elapsedMs);

        [LoggerMessage(Level = LogLevel.Information, Message = "Architecture: {Class} (hidden={Hidden}, layers={Layers}, heads={Heads})")]
        public static partial void ArchitectureDetected(ILogger logger, string @class, int hidden, int layers, int heads);

        [LoggerMessage(Level = LogLevel.Information, Message = "{Count} tensors across {Shards} safetensors shards")]
        public static partial void TensorsFound(ILogger logger, int count, int shards);

        [LoggerMessage(Level = LogLevel.Debug, Message = "[{Idx}/{Total}] tensor {Name} unknown role, skipped")]
        public static partial void TensorSkippedUnknown(ILogger logger, int idx, int total, string name);

        [LoggerMessage(Level = LogLevel.Information, Message = "[{Idx}] tensor {Name} hashed in {ElapsedMs}ms")]
        public static partial void TensorHashed(ILogger logger, int idx, string name, long elapsedMs);

        [LoggerMessage(Level = LogLevel.Information, Message = "Pass start {ModelId} :: {PassId} ({Idx}/{Total})")]
        public static partial void PassStart(ILogger logger, string modelId, string passId, int idx, int total);

        [LoggerMessage(Level = LogLevel.Information, Message = "Pass complete {ModelId} :: {PassId} → {Entities}E {Edges}Ed in {ElapsedMs}ms")]
        public static partial void PassComplete(ILogger logger, string modelId, string passId, long entities, long edges, long elapsedMs);

        [LoggerMessage(Level = LogLevel.Information, Message = "Pass skipped (already complete) {ModelId} :: {PassId} ({Idx}/{Total})")]
        public static partial void PassSkippedComplete(ILogger logger, string modelId, string passId, int idx, int total);

        [LoggerMessage(Level = LogLevel.Error, Message = "Pass FAILED {ModelId} :: {PassId} after {ElapsedMs}ms")]
        public static partial void PassFailed(ILogger logger, Exception ex, string modelId, string passId, long elapsedMs);

        [LoggerMessage(Level = LogLevel.Information, Message = "Model complete {ModelId}: {PassCount} passes ({Idx}/{Total})")]
        public static partial void ModelComplete(ILogger logger, string modelId, int passCount, int idx, int total);
    }
}
