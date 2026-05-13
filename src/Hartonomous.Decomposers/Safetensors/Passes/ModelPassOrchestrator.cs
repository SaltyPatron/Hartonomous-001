using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Data;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Text;
using Hartonomous.Decomposers.Safetensors.Packages;
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
    // Trust prior for tensor-name and arch-class-name documents. They are
    // model-derived strings asserted by the safetensors header — same tier
    // as ModelTextArtifactsPass (60_000.0).
    private const double ModelDerivedTrustMu = 60_000.0;

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
                try
                {
                    await session.FlushAsync(ct);
                }
                catch // BOUNDARY: best-effort flush during outer-exception handling; the pass already failed and the original throw below wins.
                {
                }
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
        byte[] packageHash = BuildPackageSignature(model);

        IIngestionBatch batch = _pipeline.CreateBatch(_provenanceCode);
        EntityHandle modelEntity = batch.AddEntity(new Hash32(archHash), "model_architecture");
        EntityHandle packageEntity = batch.AddEntity(new Hash32(packageHash), "model_package");
        batch.AddJunction("model_architecture_class", modelEntity, archClassId);
        batch.AddEntityModelSource(modelEntity, modelSourceId);
        batch.AddEntityModelSource(packageEntity, modelSourceId);

        // Architecture class name as a substrate document (seed-uses-core).
        // Two snapshots that share an architecture class collapse to ONE
        // document with TWO has_architecture_name edges.
        if (!string.IsNullOrEmpty(arch.ArchitectureClass))
        {
            byte[] archNameBytes = Encoding.UTF8.GetBytes(arch.ArchitectureClass);
            TextDecomposeResult archNameResult =
                SubstrateTextDecomposer.EmitStatic(
                    batch, archNameBytes,
                    new TextDecomposeOptions(
                        ProvenanceCode: _provenanceCode,
                        TopEntityType: "text_composition",
                        TrustMu: ModelDerivedTrustMu));
            batch.AddEdge("has_architecture_name", _provenanceCode,
            [
                new EdgeMemberSpec(modelEntity, "source", 0),
                new EdgeMemberSpec(archNameResult.RootHandle, "target", 1),
            ]);
        }

        List<SafetensorsTensorInfo> rawTensors = [];
        int tensorSourceCount;
        if (model.Reader is not null)
        {
            // Polymorphic donor path: enumerate via the IDonorPackageReader
            // (safetensors / pickle / multi-subdir) and bridge each TensorMetadata
            // to a SafetensorsTensorInfo whose donor:// FilePath routes
            // SafetensorsReader.StreamHash through the registered reader.
            IReadOnlyList<TensorMetadata> mds = model.Reader.EnumerateTensors();
            rawTensors.Capacity = mds.Count;
            foreach (TensorMetadata md in mds)
            {
                rawTensors.Add(DonorTensorBridge.ToSafetensorsTensorInfo(md, model.ReaderSlot));
            }
            tensorSourceCount = 1; // one logical reader, regardless of underlying shard count
        }
        else
        {
            // HuggingFace cache shape: each .safetensors file gets its header
            // read directly. FilePath in each emitted SafetensorsTensorInfo is
            // the on-disk path; OpenTensorStream uses File.OpenRead.
            foreach (string st in model.SafetensorsFiles)
            {
                rawTensors.AddRange(SafetensorsReader.ReadHeader(st));
            }
            tensorSourceCount = model.SafetensorsFiles.Count;
        }
        Log.TensorsFound(_logger, rawTensors.Count, tensorSourceCount);

        // Hash + classify each tensor. We track the EntityHandle returned by
        // batch.AddEntity directly — no cross-batch resolve, since the hash
        // IS the substrate FK.
        List<(SafetensorsTensorInfo Info, TensorClassification Classification, byte[] Hash, EntityHandle Entity, EntityHandle PackageTensor)> staged = [];
        int tensorIdx = 0;
        foreach (SafetensorsTensorInfo tensor in rawTensors)
        {
            ct.ThrowIfCancellationRequested();
            tensorIdx++;
            // Classification deferred to TupleResolver below — we hash + create
            // every tensor entity unconditionally; classification + tuple grouping
            // happens after the bootstrap loop and drives downstream TuplePass dispatch.
            // The Unknown filter previously here is gone: every tensor is content;
            // an unrecognized name just doesn't participate in any tuple.
            TensorClassification cls = new(
                PrimitiveKind.Unknown, ArchetypeTuple.Unknown, TupleSlot.Unknown,
                LayerIndex: null, HeadIndex: null, ExpertIndex: null,
                Modality: ModalityHint.Unknown, AdaptationOf: null);

            Stopwatch hashSw = Stopwatch.StartNew();
            byte[] tensorHash = HashTensorStreaming(tensor);
            hashSw.Stop();
            Log.TensorHashed(_logger, tensorIdx, tensor.Name, hashSw.ElapsedMilliseconds);

            EntityHandle tensorH = batch.AddEntity(new Hash32(tensorHash), "tensor");
            EntityHandle packageTensorH = batch.AddEntity(
                new Hash32(BuildPackageTensorSignature(packageHash, tensorIdx, tensor)),
                "model_package_tensor");
            batch.AddEntityModelSource(tensorH, modelSourceId);
            batch.AddEntityModelSource(packageTensorH, modelSourceId);
            // tensor_tensor_role junction will be re-emitted in §IX.3b math layer
            // once the classification dictionary from TupleResolver maps tensors
            // to (PrimitiveKind, ArchetypeTuple, TupleSlot) triples and the junction
            // is migrated to record the new vocabulary.
            batch.AddEdge("has_tensor", _provenanceCode,
            [
                new EdgeMemberSpec(modelEntity, "source", 0),
                new EdgeMemberSpec(tensorH, "target", 1),
            ]);
            batch.AddCompositionChild(packageEntity, tensorIdx, packageTensorH);
            batch.AddCompositionChild(packageTensorH, 1, tensorH);

            // Tensor name + dtype + shape as substrate documents (seed-uses-core).
            // Identical strings across models collapse to ONE document with N
            // edges. Recomposer reads these to reconstruct the safetensors
            // header on export — without them the substrate cannot be
            // round-tripped from UCD/UCA + AI model alone.
            EmitTensorMetadataDocuments(batch, tensorH, tensor, ct);
            staged.Add((tensor, cls, tensorHash, tensorH, packageTensorH));

            if (batch.EntityCount >= _batchSize || batch.EdgeCount >= _batchSize)
            {
                await _pipeline.SubmitBatchAsync(batch, ct);
                batch = _pipeline.CreateBatch(_provenanceCode);
                modelEntity = batch.AddEntity(new Hash32(archHash), "model_architecture");
                batch.AddEntityModelSource(modelEntity, modelSourceId);
            }
        }

        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await _pipeline.SubmitBatchAsync(batch, ct);
        }

        // Construct handles directly from the EntityHandles we already captured
        // when emitting entities. No resolve, no LookupId — hash is the FK.
        ModelArchitectureHandle archHandle = new(arch, archClassId, archHash,
            new EntityHandle(archHash, "model_architecture"));

        List<TensorHandle> tensorHandles = new(staged.Count);
        foreach (var s in staged)
        {
            tensorHandles.Add(new TensorHandle(s.Info, s.Classification, s.Hash, s.Entity, s.PackageTensor));
        }

        ModelSourceHandle sourceHandle = new(
            modelSourceId, model.PublisherSlug, model.ModelSlug, model.Revision, model.RevisionHex, model.ModelId,
            Path.GetDirectoryName(model.ConfigPath)!, packageEntity);
        string checkpointKey = $"model_source:{modelSourceId}";

        // Resolve tensor classifications + tuple groupings via TupleResolver
        // (per docs/01-tensor-primitive-spec.md §III). This produces the
        // ResolvedTuple list that TuplePass implementations dispatch on, plus
        // the per-tensor classification dictionary that PrimitivePasses use
        // (NormalizationPass for γ/β contour emission, etc.).
        TupleResolution.TupleResolver resolver = new();
        (var classifications, var tuples) = resolver.Resolve(arch.ArchitectureClass, tensorHandles);
        await PersistTensorResolverOutputAsync(classifications, tensorRoleMap, ct);

        return new ModelPassContext(
            Source: sourceHandle,
            Architecture: archHandle,
            Tensors: tensorHandles,
            Compute: _compute,
            TensorRoleMap: tensorRoleMap,
            CheckpointKey: checkpointKey,
            ProvenanceCode: _provenanceCode,
            TensorClassifications: classifications,
            ResolvedTuples: tuples);
    }

    // LookupId removed — hash-as-PK substrate eliminates surrogate-id resolution.

    private async Task PersistTensorResolverOutputAsync(
        IReadOnlyDictionary<TensorHandle, TensorClassification> classifications,
        Dictionary<string, int> tensorRoleMap,
        CancellationToken ct)
    {
        IIngestionBatch roleBatch = _pipeline.CreateBatch(_provenanceCode);
        int roleCount = 0;
        foreach ((TensorHandle tensor, TensorClassification classification) in classifications)
        {
            EntityHandle placementEntity = tensor.PackageTensorEntity ?? tensor.Entity;
            EmitTensorPlacementMetadata(roleBatch, placementEntity, tensor.Info, classification, ct);
            roleCount++;
            if (classification.FusedMembers is { Count: > 0 } fusedMembers)
            {
                foreach (FusedTensorMember fused in fusedMembers)
                {
                    AddTensorRole(roleBatch, placementEntity, classification with { Slot = fused.Slot }, tensorRoleMap);
                }
                continue;
            }

            AddTensorRole(roleBatch, placementEntity, classification, tensorRoleMap);
        }

        if (roleCount > 0)
        {
            await _pipeline.SubmitBatchAsync(roleBatch, ct);
        }
    }

    private static void AddTensorRole(
        IIngestionBatch batch,
        EntityHandle placementEntity,
        TensorClassification classification,
        Dictionary<string, int> tensorRoleMap)
    {
        string? roleCode = TensorRoleCode(classification);
        if (roleCode is null)
        {
            return;
        }
        if (!tensorRoleMap.TryGetValue(roleCode, out int roleId))
        {
            throw new InvalidOperationException(
                $"Tensor role '{roleCode}' is not present in substrate.tensor_role. "
                + "The canonical tensor_role seed must be loaded before safetensors ingestion.");
        }
        batch.AddJunction("tensor_tensor_role", placementEntity, roleId);
    }

    internal static string? TensorRoleCode(TensorClassification classification)
    {
        return classification.Slot switch
        {
            TupleSlot.Q => "attention_query",
            TupleSlot.K => "attention_key",
            TupleSlot.V => "attention_value",
            TupleSlot.O => "attention_output",
            TupleSlot.QNorm or TupleSlot.KNorm or TupleSlot.Scale or TupleSlot.Offset
                or TupleSlot.RunningMean or TupleSlot.RunningVar or TupleSlot.NumBatchesTracked => "layer_norm",
            TupleSlot.PosBiasTable or TupleSlot.PosBiasIndex => "position_embedding",
            TupleSlot.Intermediate or TupleSlot.Up => "ffn_up",
            TupleSlot.Output or TupleSlot.Down => "ffn_down",
            TupleSlot.Gate => "ffn_gate",
            TupleSlot.Base => null,
            TupleSlot.LoraA => "lora_a",
            TupleSlot.LoraB => "lora_b",
            TupleSlot.Router => "moe_router",
            TupleSlot.ExpertGate => "moe_expert_gate",
            TupleSlot.ExpertUp => "moe_expert_up",
            TupleSlot.ExpertDown => "moe_expert_down",
            TupleSlot.SharedExpertGate or TupleSlot.SharedExpertUp or TupleSlot.SharedExpertDown => "moe_shared_expert",
            TupleSlot.Conv1 or TupleSlot.Conv2 or TupleSlot.Conv3 or TupleSlot.ConvShortcut
                or TupleSlot.DepthwiseConv or TupleSlot.PointwiseConv1 or TupleSlot.PointwiseConv2
                or TupleSlot.PatchConv => "conv_kernel",
            TupleSlot.Table => classification.Modality switch
            {
                ModalityHint.Position => "position_embedding",
                ModalityHint.CodecCodeword => "vq_codebook",
                _ => "token_embedding",
            },
            TupleSlot.PatchNorm => "layer_norm",
            TupleSlot.ClassProj => "class_head",
            TupleSlot.BboxProj => "bbox_head",
            TupleSlot.ObjectQueries => "object_query",
            TupleSlot.LmHead => "logit_head",
            _ => null,
        };
    }

    private void EmitTensorPlacementMetadata(
        IIngestionBatch batch,
        EntityHandle placementEntity,
        SafetensorsTensorInfo tensorInfo,
        TensorClassification classification,
        CancellationToken ct)
    {
        EmitMetadataEdge(batch, placementEntity, "has_package_tensor_primitive", classification.Primitive.ToString(), ct);
        EmitMetadataEdge(batch, placementEntity, "has_package_tensor_tuple", classification.Tuple.ToString(), ct);
        string slotText = classification.FusedMembers is { Count: > 0 } fusedMembers
            ? string.Join(",", fusedMembers.Select(m => m.Slot.ToString()))
            : classification.Slot.ToString();
        EmitMetadataEdge(batch, placementEntity, "has_package_tensor_slot", slotText, ct);
        EmitMetadataEdge(batch, placementEntity, "has_package_tensor_modality", classification.Modality.ToString(), ct);
        if (classification.Primitive == PrimitiveKind.Linear
            && TensorMemberMaterializer.IsPointwiseLinearShape(tensorInfo.Shape))
        {
            EmitMetadataEdge(batch, placementEntity, "has_package_tensor_linearized_shape",
                FormatShape(TensorMemberMaterializer.LinearizedShape(tensorInfo.Shape)), ct);
        }
        if (classification.FusedMembers is { Count: > 0 } fused)
        {
            foreach (FusedTensorMember member in fused)
            {
                FusedTensorSlice slice = member.Slice;
                EmitMetadataEdge(batch, placementEntity, "has_package_tensor_fused_slice",
                    string.Concat(
                        member.Slot.ToString(),
                        ":axis=", slice.Axis.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ";offset=", slice.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ";length=", slice.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ct);
            }
        }
        if (classification.LayerIndex is int layerIndex)
        {
            EmitMetadataEdge(batch, placementEntity, "has_package_tensor_layer_index",
                layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        }
        if (classification.HeadIndex is int headIndex)
        {
            EmitMetadataEdge(batch, placementEntity, "has_package_tensor_head_index",
                headIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        }
        if (classification.ExpertIndex is int expertIndex)
        {
            EmitMetadataEdge(batch, placementEntity, "has_package_tensor_expert_index",
                expertIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        }
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

    private byte[] BuildPackageSignature(DiscoveredModel model)
    {
        string packageFormat = model.Reader?.PackageFormat ?? "safetensors";
        string packageRoot = model.Reader?.PackageRoot
            ?? Path.GetDirectoryName(model.ConfigPath)
            ?? string.Empty;
        ICanonicalSignatureBuilder builder = new CanonicalSignatureBuilder(_compute.Common, "mpkg")
            .WriteUtf8(model.PublisherSlug)
            .WriteUtf8(model.ModelSlug)
            .WriteUtf8(model.RevisionHex)
            .WriteUtf8(packageFormat)
            .WriteUtf8(packageRoot)
            .WriteInt32LE(model.SafetensorsFiles.Count);
        foreach (string path in model.SafetensorsFiles.Order(StringComparer.Ordinal))
        {
            builder.WriteUtf8(path);
        }
        return builder.Finalize();
    }

    private byte[] BuildPackageTensorSignature(byte[] packageHash, int orderIndex, SafetensorsTensorInfo tensor)
        => new CanonicalSignatureBuilder(_compute.Common, "pten")
            .WriteHash(packageHash)
            .WriteInt32LE(orderIndex)
            .WriteUtf8(tensor.Name)
            .WriteUtf8(DtypeToWireFormat(tensor.Dtype))
            .WriteUtf8(FormatShape(tensor.Shape))
            .WriteUtf8(tensor.FilePath)
            .WriteInt64LE(tensor.BeginByte)
            .WriteInt64LE(tensor.EndByte)
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

    /// <summary>
    /// Routes a tensor's three header strings (name, dtype, shape) through
    /// the text decomposer's full DAG and emits has_tensor_name / has_dtype /
    /// has_shape edges. The substrate then carries everything the recomposer
    /// needs to reconstruct the safetensors header on export — without these
    /// the substrate is not round-trip-self-sufficient.
    /// </summary>
    private void EmitTensorMetadataDocuments(
        IIngestionBatch batch, EntityHandle tensorH, SafetensorsTensorInfo tensor, CancellationToken ct)
    {
        EmitMetadataEdge(batch, tensorH, "has_tensor_name", tensor.Name, ct);
        EmitMetadataEdge(batch, tensorH, "has_dtype", DtypeToWireFormat(tensor.Dtype), ct);
        EmitMetadataEdge(batch, tensorH, "has_shape", FormatShape(tensor.Shape), ct);
    }

    private void EmitMetadataEdge(
        IIngestionBatch batch, EntityHandle source, string edgeCode, string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        TextDecomposeResult result =
            SubstrateTextDecomposer.EmitStatic(
                batch, bytes,
                new TextDecomposeOptions(
                    ProvenanceCode: _provenanceCode,
                    TopEntityType: "text_composition",
                    TrustMu: ModelDerivedTrustMu));
        batch.AddEdge(edgeCode, _provenanceCode,
        [
            new EdgeMemberSpec(source, "source", 0),
            new EdgeMemberSpec(result.RootHandle, "target", 1),
        ]);
    }

    /// <summary>Mirrors <c>SafetensorsReader.ParseDtype</c>'s wire-format strings.</summary>
    private static string DtypeToWireFormat(SafetensorsDtype dtype) => dtype switch
    {
        SafetensorsDtype.F32 => "F32",
        SafetensorsDtype.F64 => "F64",
        SafetensorsDtype.F16 => "F16",
        SafetensorsDtype.BF16 => "BF16",
        SafetensorsDtype.I8 => "I8",
        SafetensorsDtype.I16 => "I16",
        SafetensorsDtype.I32 => "I32",
        SafetensorsDtype.I64 => "I64",
        SafetensorsDtype.U8 => "U8",
        SafetensorsDtype.U16 => "U16",
        SafetensorsDtype.U32 => "U32",
        SafetensorsDtype.U64 => "U64",
        SafetensorsDtype.Bool => "BOOL",
        SafetensorsDtype.F8E4M3 => "F8_E4M3",
        SafetensorsDtype.F8E5M2 => "F8_E5M2",
        _ => throw new NotSupportedException($"Unhandled SafetensorsDtype {dtype}"),
    };

    /// <summary>
    /// Canonical shape encoding: comma-separated decimals in square brackets,
    /// matching <see cref="SafetensorsRecomposer"/>'s <c>ParseShape</c>. Same
    /// shape across models collapses to ONE substrate document.
    /// </summary>
    private static string FormatShape(long[] shape)
    {
        if (shape is null || shape.Length == 0)
        {
            return "[]";
        }
        StringBuilder sb = new();
        sb.Append('[');
        for (int i = 0; i < shape.Length; i++)
        {
            if (i > 0) { sb.Append(','); }
            sb.Append(shape[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
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
