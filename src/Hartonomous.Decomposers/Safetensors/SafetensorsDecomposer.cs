using System.Diagnostics;
using System.Text;
using Hartonomous.Core;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors;

public sealed partial class SafetensorsDecomposer : BaseDecomposer
{
    public override string ProvenanceCode => "huggingface_model";
    public override string DisplayName => "Safetensors model decomposer (two-track ingestion)";
    public override IReadOnlyList<Phase> Phases => [Phase.ModelDecomp];

    private const double ModelDerivedTrustMu = 60000.0;
    private const int FireflyBatchSize = 50_000;
    private const int MaxFireflyRows = 50_000;
    private const string HuggingFaceRegistryCode = "huggingface";
    private const string HuggingFaceRegistryDisplay = "Hugging Face Hub";

    private readonly string _hubRoot;
    private readonly string _connectionString;

    public SafetensorsDecomposer(DecomposerConfig config, ILogger<SafetensorsDecomposer> logger)
        : base(config, logger)
    {
        _hubRoot = config.SourceDirectory;
        _connectionString = config.ConnectionString;
    }

    protected override IReadOnlyList<string> GetSourcePaths() => [_hubRoot];

    protected override async Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        List<DiscoveredModel> models = DiscoverModels(_hubRoot);
        Log.ModelsDiscovered(Logger, models.Count, _hubRoot);
        if (models.Count == 0)
        {
            return;
        }

        SafetensorsReferenceTableWriter refWriter = new(_connectionString);
        try
        {
            int hfRegistryId = await refWriter.EnsureModelRegistryAsync(
                HuggingFaceRegistryCode, HuggingFaceRegistryDisplay, ct);

            int modelIdx = 0;
            foreach (DiscoveredModel model in models)
            {
                ct.ThrowIfCancellationRequested();
                modelIdx++;
                await DecomposeModelAsync(model, hfRegistryId, modelIdx, models.Count,
                    pipeline, reporter, refWriter, ct);
            }
        }
        finally
        {
            await refWriter.DisposeAsync();
        }
    }

    private async Task DecomposeModelAsync(
        DiscoveredModel model,
        int registryId,
        int modelIdx,
        int totalModels,
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        SafetensorsReferenceTableWriter refWriter,
        CancellationToken ct)
    {
        string revisionPrefix = model.RevisionHex.Length >= 8 ? model.RevisionHex[..8] : model.RevisionHex;
        Log.ModelStart(Logger, model.ModelId, model.PublisherSlug, revisionPrefix);

        // 1. Resolve typed model identity: publisher → model_source. Per-model identity
        //    lives here — nowhere else. The category-tier provenance code "huggingface_model"
        //    is all the edge carries; everything specific rides the typed tables.
        int publisherId = await refWriter.EnsureModelPublisherAsync(
            registryId, model.PublisherSlug, model.PublisherSlug, ct);
        long modelSourceId = await refWriter.EnsureModelSourceAsync(
            registryId, publisherId, model.ModelSlug, model.Revision, ct);

        // 2. Parse architecture from config.json. Placement (publisher, revision) is
        //    already pinned via model_source — it must not leak into the architecture
        //    record because that record feeds the content-only architecture hash.
        ModelArchitecture arch = ArchitectureDetector.DetectFromConfig(model.ConfigPath, model.ModelId);
        Log.ArchitectureDetected(Logger, arch.ArchitectureClass, arch.HiddenSize, arch.NumLayers, arch.NumAttentionHeads);

        // 3. Ensure architecture_class exists.
        int archClassId = await refWriter.EnsureArchitectureClassAsync(arch.ArchitectureClass, ct);

        // 4. Load tensor role map.
        Dictionary<string, int> tensorRoleMap = await refWriter.LoadTensorRoleMapAsync(ct);

        // 5. Read all tensor headers (possibly across shards).
        List<SafetensorsTensorInfo> tensors = [];
        foreach (string stPath in model.SafetensorsFiles)
        {
            List<SafetensorsTensorInfo> infos = SafetensorsReader.ReadHeader(stPath);
            tensors.AddRange(infos);
        }
        Log.TensorsFound(Logger, tensors.Count, model.SafetensorsFiles.Count);

        // 6. Create model_architecture entity + architecture_class junction + model_source link.
        //    Entity hash is content-only: canonical descriptor of the architectural
        //    configuration (class, type, dimensions). Publisher / model slug / revision
        //    are PLACEMENT and live on the model_source row + entity_model_source junction.
        //    Two models with identical architectural parameters resolve to the same entity
        //    and gain one junction row each.
        IIngestionBatch batch = pipeline.CreateBatch();
        byte[] modelHash = ComputeHash(BuildArchitectureContentDescriptor(arch));
        EntityHandle modelEntity = batch.AddEntity(modelHash, "model_architecture");
        batch.AddJunction("model_architecture_class", modelEntity, archClassId);
        batch.AddSignificance(modelEntity, "model_trust", ModelDerivedTrustMu);
        batch.AddEntityModelSource(modelEntity, modelSourceId);

        long entityCount = 1;
        long edgeCount = 0;
        long fireflyCount = 0;
        int batchNum = 0;
        int tensorIdx = 0;

        // 7. Walk tensors — Track 1 (embeddings) → fireflies, Track 2 (weights) → tensor entities.
        foreach (SafetensorsTensorInfo tensor in tensors)
        {
            ct.ThrowIfCancellationRequested();
            tensorIdx++;

            TensorClassification cls = TensorClassifier.Classify(tensor.Name, arch.ArchitectureClass);
            if (cls.Role == TensorRole.Unknown)
            {
                Log.TensorSkippedUnknown(Logger, tensorIdx, tensors.Count, tensor.Name);
                continue;
            }

            // Decide up front whether this tensor needs Track 1 projection. If so, use a
            // single streaming pass that hashes AND decodes into one f64 buffer (the only
            // tensor-sized allocation). Otherwise just stream-hash the bytes — never
            // materializes the full tensor in managed memory.
            bool doTrack1 = cls.Role.IsTrack1() && tensor.Shape.Length == 2;
            int track1Rows = 0;
            int track1Cols = 0;
            double[]? flatBuffer = null;
            if (doTrack1)
            {
                long rowsLong = tensor.Shape[0];
                if (rowsLong < 4 || rowsLong > MaxFireflyRows)
                {
                    Log.SkippingFireflies(Logger, tensor.Name, rowsLong);
                    doTrack1 = false;
                }
                else
                {
                    track1Rows = (int)rowsLong;
                    track1Cols = (int)tensor.Shape[1];
                    flatBuffer = new double[(long)track1Rows * track1Cols];
                }
            }

            if (Logger.IsEnabled(LogLevel.Information))
            {
                string roleCode = cls.Role.ToCode();
                string trackLabel = doTrack1 ? "track1" : "track2";
                string shapeStr = FormatShape(tensor.Shape);
                string dtypeStr = tensor.Dtype.ToString();
                Log.TensorStart(Logger, tensorIdx, tensors.Count, tensor.Name,
                    roleCode, trackLabel, shapeStr, dtypeStr);
            }

            // Tensor entity hash is content-only: shape + dtype + raw tensor bytes.
            // Tensor name is PLACEMENT (the slot inside *this* model) and rides on the
            // has_tensor edge below. Two tensors with identical shape, dtype, and bytes
            // are the same entity regardless of where they were stored.
            Stopwatch hashSw = Stopwatch.StartNew();
            byte[] tensorHash = doTrack1
                ? HashTensorStreamingAndDecode(tensor, flatBuffer!)
                : HashTensorStreaming(tensor);
            hashSw.Stop();
            Log.TensorHashed(Logger, tensorIdx, tensor.Name, hashSw.ElapsedMilliseconds);
            EntityHandle tensorEntity = batch.AddEntity(tensorHash, "tensor");
            batch.AddSignificance(tensorEntity, "model_trust", ModelDerivedTrustMu);
            batch.AddEntityModelSource(tensorEntity, modelSourceId);
            entityCount++;

            if (tensorRoleMap.TryGetValue(cls.Role.ToCode(), out int roleId))
            {
                batch.AddJunction("tensor_tensor_role", tensorEntity, roleId);
            }

            batch.AddEdge("has_tensor", ProvenanceCode,
            [
                new EdgeMemberSpec(modelEntity, null, "source", 0),
                new EdgeMemberSpec(tensorEntity, null, "target", 1),
            ]);
            edgeCount++;

            if (batch.EntityCount >= BatchSize || batch.EdgeCount >= BatchSize)
            {
                batchNum++;
                await SubmitBatchAsync(pipeline, reporter, batch, model.ModelId,
                    entityCount, edgeCount, batchNum, ct);
                batch = pipeline.CreateBatch();
                // Re-add the model entity so subsequent edges can reference it by handle.
                modelEntity = batch.AddEntity(modelHash, "model_architecture");
                batch.AddEntityModelSource(modelEntity, modelSourceId);
            }

            if (doTrack1)
            {
                int rows = track1Rows;
                int cols = track1Cols;

                Log.Track1ProjectStart(Logger, tensorIdx, tensor.Name, rows, cols);
                Stopwatch projSw = Stopwatch.StartNew();
                (double[] x, double[] y, double[] z, double[] mMag) =
                    ProjectFlatBufferToFireflies(flatBuffer!, rows, cols, tensorIdx, tensor.Name);
                flatBuffer = null;
                projSw.Stop();
                Log.Track1ProjectComplete(Logger, tensorIdx, tensor.Name, rows, projSw.ElapsedMilliseconds);

                for (int i = 0; i < rows; i++)
                {
                    // Firefly entity hash is content-only: the 4D coordinate + magnitude
                    // IS the firefly. Row ordinal, tensor name, model identity are all
                    // PLACEMENT and never enter the hash. Identical 4D positions across
                    // runs and models resolve to the same entity.
                    byte[] fireflyHash = ComputeFireflyContentHash(x[i], y[i], z[i], mMag[i]);
                    EntityHandle firefly = batch.AddEntity(fireflyHash, "bpe_token");
                    byte[] wkb = PointZMToWkb(x[i], y[i], z[i], mMag[i]);
                    batch.AddPhysicality(firefly, "embedding_firefly", wkb);
                    batch.AddSignificance(firefly, "model_trust", ModelDerivedTrustMu);
                    batch.AddEntityModelSource(firefly, modelSourceId);

                    batch.AddEdge("has_token_id", ProvenanceCode,
                    [
                        new EdgeMemberSpec(tensorEntity, null, "source", 0),
                        new EdgeMemberSpec(firefly, null, "target", 1),
                    ]);

                    entityCount++;
                    fireflyCount++;
                    edgeCount++;

                    if (batch.EntityCount >= FireflyBatchSize)
                    {
                        batchNum++;
                        await SubmitBatchAsync(pipeline, reporter, batch, model.ModelId,
                            entityCount, edgeCount, batchNum, ct);
                        batch = pipeline.CreateBatch();
                        modelEntity = batch.AddEntity(modelHash, "model_architecture");
                        batch.AddEntityModelSource(modelEntity, modelSourceId);
                        tensorEntity = batch.AddEntity(tensorHash, "tensor");
                        batch.AddEntityModelSource(tensorEntity, modelSourceId);
                    }
                }
            }
        }

        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            batchNum++;
            await SubmitBatchAsync(pipeline, reporter, batch, model.ModelId,
                entityCount, edgeCount, batchNum, ct);
        }

        Log.ModelComplete(Logger, model.ModelId, entityCount, edgeCount, fireflyCount, batchNum,
            modelIdx, totalModels);
    }

    /// <summary>
    /// Compute per-row magnitudes over a flat row-major embedding buffer, then project to 3D
    /// via the Laplacian eigenmap. Magnitudes are taken BEFORE projection normalizes the rows
    /// in place. The flat buffer is consumed by the projector (callers must not reuse it).
    /// </summary>
    private (double[] X, double[] Y, double[] Z, double[] Magnitude) ProjectFlatBufferToFireflies(
        double[] flat, int rows, int cols, int tensorIdx, string tensorName)
    {
        double[] magnitude = new double[rows];
        for (int i = 0; i < rows; i++)
        {
            long off = (long)i * cols;
            double norm = 0;
            for (int j = 0; j < cols; j++)
            {
                double v = flat[off + j];
                norm += v * v;
            }
            magnitude[i] = Math.Sqrt(norm);
        }

        Stopwatch stageSw = Stopwatch.StartNew();
        void OnStage(string msg)
        {
            Log.Track1Stage(Logger, tensorIdx, tensorName, msg, stageSw.ElapsedMilliseconds);
            stageSw.Restart();
        }
        (double[] x, double[] y, double[] z) = LaplacianEigenmap.Project(flat, rows, cols, onStage: OnStage);
        return (x, y, z, magnitude);
    }

    private async Task SubmitBatchAsync(
        IIngestionPipeline pipeline, IProgressReporter reporter, IIngestionBatch batch,
        string modelId, long entityCount, long edgeCount, int batchNum, CancellationToken ct)
    {
        int batchEntities = batch.EntityCount;
        int batchEdges = batch.EdgeCount;
        Log.BatchSubmitStart(Logger, batchNum, batchEntities, batchEdges);
        Stopwatch sw = Stopwatch.StartNew();
        await SubmitAndReportAsync(pipeline, reporter, batch,
            new ProgressSnapshot
            {
                DecomposerCode = ProvenanceCode,
                CurrentPhase = "ingestion",
                EntitiesCreated = entityCount,
                EdgesCreated = edgeCount,
                CurrentFile = modelId,
                CurrentBatch = batchNum,
            }, ct);
        sw.Stop();
        Log.BatchCommitted(Logger, batchNum, batchEntities, batchEdges, entityCount, edgeCount, sw.ElapsedMilliseconds);
    }

    private static string FormatShape(long[] shape)
    {
        if (shape.Length == 0)
        {
            return "[]";
        }
        StringBuilder sb = new();
        sb.Append('[');
        for (int i = 0; i < shape.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('x');
            }
            sb.Append(shape[i]);
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Canonical content descriptor for a model_architecture entity.
    /// Includes only architectural parameters — no publisher, model slug, or revision.
    /// Field order is fixed; two calls with equal <see cref="ModelArchitecture"/> values
    /// produce the same bytes.
    /// </summary>
    internal static string BuildArchitectureContentDescriptor(ModelArchitecture arch)
        => $"model_architecture|class={arch.ArchitectureClass}|type={arch.ModelType}"
           + $"|hidden={arch.HiddenSize}|layers={arch.NumLayers}|heads={arch.NumAttentionHeads}"
           + $"|vocab={arch.VocabSize}|intermediate={arch.IntermediateSize}"
           + $"|maxpos={arch.MaxPositionEmbeddings}";

    /// <summary>
    /// Streaming content hash: canonical (dtype, shape) prefix followed by the raw
    /// little-endian tensor bytes, fed into BLAKE3 incrementally in 1 MiB chunks so the
    /// full tensor is never materialized in managed memory. Bytes are hashed without
    /// decoding so the hash is stable under downstream dtype widening.
    /// </summary>
    internal static byte[] HashTensorStreaming(SafetensorsTensorInfo tensor)
    {
        Blake3Hasher hasher = Blake3Hasher.Create();
        FeedTensorDescriptor(ref hasher, tensor);
        SafetensorsReader.StreamHash(tensor, ref hasher);
        return hasher.Finalize();
    }

    /// <summary>
    /// Single-pass hash + decode: reads the tensor's raw bytes once, feeds them into
    /// the hasher AND decodes them into <paramref name="flatResult"/> as f64. Halves I/O
    /// and buffer allocation versus the hash-then-decode path and enables hashing of
    /// tensors larger than 2 GiB (no intermediate managed buffer required).
    /// </summary>
    internal static byte[] HashTensorStreamingAndDecode(SafetensorsTensorInfo tensor, double[] flatResult)
    {
        Blake3Hasher hasher = Blake3Hasher.Create();
        FeedTensorDescriptor(ref hasher, tensor);
        SafetensorsReader.StreamHashAndDecode(tensor, ref hasher, flatResult);
        return hasher.Finalize();
    }

    private static void FeedTensorDescriptor(ref Blake3Hasher hasher, SafetensorsTensorInfo tensor)
    {
        string desc = $"tensor|dtype={tensor.Dtype}|shape={string.Join("x", tensor.Shape)}|data=";
        byte[] descBytes = Encoding.UTF8.GetBytes(desc);
        hasher.Update(descBytes);
    }

    /// <summary>
    /// Content hash of a firefly entity: a domain-tag prefix + 32 bytes of the four
    /// f64 coordinates. The 4D point IS the firefly's content — identical coordinates
    /// across runs or models resolve to the same entity.
    /// </summary>
    internal static byte[] ComputeFireflyContentHash(double x, double y, double z, double magnitude)
    {
        ReadOnlySpan<byte> prefix = "firefly\0"u8;
        Span<byte> buffer = stackalloc byte[8 + 32];
        prefix.CopyTo(buffer);
        BitConverter.TryWriteBytes(buffer[8..], x);
        BitConverter.TryWriteBytes(buffer[16..], y);
        BitConverter.TryWriteBytes(buffer[24..], z);
        BitConverter.TryWriteBytes(buffer[32..], magnitude);
        return ComputeHash(buffer);
    }

    /// <summary>
    /// Resolves the source path against three valid HuggingFace cache shapes:
    ///   (1) a hub root containing many <c>models--{publisher}--{name}/</c> dirs → iterate all,
    ///   (2) a single <c>models--{publisher}--{name}/</c> dir → iterate its snapshots,
    ///   (3) a single <c>snapshots/{revision}/</c> dir with <c>config.json</c> + <c>*.safetensors</c> → ingest just that snapshot.
    /// Lets smoke tests point straight at a single model in the user's real cache without copying files.
    /// </summary>
    internal static List<DiscoveredModel> DiscoverModels(string sourcePath)
    {
        List<DiscoveredModel> models = [];
        if (!Directory.Exists(sourcePath))
        {
            return models;
        }

        if (LooksLikeSnapshotDir(sourcePath))
        {
            DiscoveredModel? single = TryBuildModelFromSnapshotDir(sourcePath);
            if (single is not null)
            {
                models.Add(single);
            }
            return models;
        }

        if (LooksLikeModelDir(sourcePath))
        {
            CollectFromModelDir(sourcePath, models);
            return models;
        }

        foreach (string modelDir in Directory.EnumerateDirectories(sourcePath, "models--*"))
        {
            CollectFromModelDir(modelDir, models);
        }
        return models;
    }

    private static bool LooksLikeSnapshotDir(string dir)
    {
        if (!File.Exists(Path.Combine(dir, "config.json")))
        {
            return false;
        }
        foreach (string _ in Directory.EnumerateFiles(dir, "*.safetensors"))
        {
            return true;
        }
        return false;
    }

    private static bool LooksLikeModelDir(string dir)
    {
        return Path.GetFileName(dir).StartsWith("models--", StringComparison.Ordinal)
            && Directory.Exists(Path.Combine(dir, "snapshots"));
    }

    private static void CollectFromModelDir(string modelDir, List<DiscoveredModel> sink)
    {
        if (!TryParseModelDirName(Path.GetFileName(modelDir), out string publisherSlug, out string modelSlug))
        {
            return;
        }

        string snapshotsDir = Path.Combine(modelDir, "snapshots");
        if (!Directory.Exists(snapshotsDir))
        {
            return;
        }

        foreach (string snapshotDir in Directory.EnumerateDirectories(snapshotsDir))
        {
            DiscoveredModel? m = TryBuildModel(snapshotDir, publisherSlug, modelSlug);
            if (m is not null)
            {
                sink.Add(m);
            }
        }
    }

    private static DiscoveredModel? TryBuildModelFromSnapshotDir(string snapshotDir)
    {
        // Walk up: snapshotDir / "snapshots" / "models--{publisher}--{name}" / hubRoot
        string? snapshotsParent = Path.GetDirectoryName(snapshotDir);
        if (snapshotsParent is null || !string.Equals(Path.GetFileName(snapshotsParent), "snapshots", StringComparison.Ordinal))
        {
            return null;
        }
        string? modelDir = Path.GetDirectoryName(snapshotsParent);
        if (modelDir is null || !TryParseModelDirName(Path.GetFileName(modelDir), out string publisherSlug, out string modelSlug))
        {
            return null;
        }
        return TryBuildModel(snapshotDir, publisherSlug, modelSlug);
    }

    private static DiscoveredModel? TryBuildModel(string snapshotDir, string publisherSlug, string modelSlug)
    {
        string snapshotHex = Path.GetFileName(snapshotDir);
        byte[]? revision = ParseRevisionHex(snapshotHex);
        if (revision is null)
        {
            return null;
        }

        string configPath = Path.Combine(snapshotDir, "config.json");
        if (!File.Exists(configPath))
        {
            return null;
        }

        List<string> stFiles = [];
        foreach (string st in Directory.EnumerateFiles(snapshotDir, "*.safetensors"))
        {
            stFiles.Add(st);
        }
        if (stFiles.Count == 0)
        {
            return null;
        }

        return new DiscoveredModel(
            ModelId: $"{publisherSlug}/{modelSlug}",
            PublisherSlug: publisherSlug,
            ModelSlug: modelSlug,
            Revision: revision,
            RevisionHex: snapshotHex,
            ConfigPath: configPath,
            SafetensorsFiles: stFiles);
    }

    private static bool TryParseModelDirName(string dirName, out string publisherSlug, out string modelSlug)
    {
        publisherSlug = string.Empty;
        modelSlug = string.Empty;
        string[] parts = dirName.Split("--", 2, StringSplitOptions.None);
        if (parts.Length < 2 || parts[0] != "models")
        {
            return false;
        }

        string remainder = parts[1];
        int dashIdx = remainder.IndexOf("--", StringComparison.Ordinal);
        if (dashIdx > 0)
        {
            publisherSlug = remainder[..dashIdx];
            modelSlug = remainder[(dashIdx + 2)..];
        }
        else
        {
            // Legacy / namespace-free models — allow, but mark publisher as the model itself.
            publisherSlug = remainder;
            modelSlug = remainder;
        }
        return true;
    }

    /// <summary>
    /// HuggingFace snapshot hashes are git-sha1 hex (40 chars → 20 bytes). Allow 64-char
    /// BLAKE3 hex as well (32 bytes) for non-HF registries. Any other length is rejected —
    /// the model_source.revision CHECK constraint enforces (20, 32) only.
    /// </summary>
    private static byte[]? ParseRevisionHex(string hex)
    {
        if (hex.Length != 40 && hex.Length != 64)
        {
            return null;
        }

        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            int hi = HexDigit(hex[i * 2]);
            int lo = HexDigit(hex[(i * 2) + 1]);
            if (hi < 0 || lo < 0)
            {
                return null;
            }
            bytes[i] = (byte)((hi << 4) | lo);
        }
        return bytes;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private static byte[] PointZMToWkb(double x, double y, double z, double m)
    {
        byte[] wkb = new byte[37];
        wkb[0] = 1; // little-endian
        BitConverter.TryWriteBytes(wkb.AsSpan(1), 0xC0000001u); // PointZM
        BitConverter.TryWriteBytes(wkb.AsSpan(5), x);
        BitConverter.TryWriteBytes(wkb.AsSpan(13), y);
        BitConverter.TryWriteBytes(wkb.AsSpan(21), z);
        BitConverter.TryWriteBytes(wkb.AsSpan(29), m);
        return wkb;
    }

    internal sealed record DiscoveredModel(
        string ModelId,
        string PublisherSlug,
        string ModelSlug,
        byte[] Revision,
        string RevisionHex,
        string ConfigPath,
        IReadOnlyList<string> SafetensorsFiles);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Discovered {Count} models under {Root}")]
        public static partial void ModelsDiscovered(ILogger logger, int count, string root);

        [LoggerMessage(Level = LogLevel.Information, Message = "Model start: {ModelId} (publisher={Publisher}, rev={RevisionPrefix})")]
        public static partial void ModelStart(ILogger logger, string modelId, string publisher, string revisionPrefix);

        [LoggerMessage(Level = LogLevel.Information, Message = "Architecture: {Class} (hidden={Hidden}, layers={Layers}, heads={Heads})")]
        public static partial void ArchitectureDetected(ILogger logger, string @class, int hidden, int layers, int heads);

        [LoggerMessage(Level = LogLevel.Information, Message = "{Count} tensors across {Shards} safetensors shards")]
        public static partial void TensorsFound(ILogger logger, int count, int shards);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping fireflies for {Tensor} (rows={Rows} out of supported range)")]
        public static partial void SkippingFireflies(ILogger logger, string tensor, long rows);

        [LoggerMessage(Level = LogLevel.Information, Message = "[{Idx}/{Total}] tensor {Name} role={Role} {Track} shape={Shape} dtype={Dtype}")]
        public static partial void TensorStart(ILogger logger, int idx, int total, string name, string role, string track, string shape, string dtype);

        [LoggerMessage(Level = LogLevel.Debug, Message = "[{Idx}] tensor {Name} unknown role, skipped ({Total} total)")]
        public static partial void TensorSkippedUnknown(ILogger logger, int idx, int total, string name);

        [LoggerMessage(Level = LogLevel.Information, Message = "[{Idx}] tensor {Name} hashed in {ElapsedMs}ms")]
        public static partial void TensorHashed(ILogger logger, int idx, string name, long elapsedMs);

        [LoggerMessage(Level = LogLevel.Information, Message = "[{Idx}] tensor {Name} Track 1 projection starting (rows={Rows}, cols={Cols})")]
        public static partial void Track1ProjectStart(ILogger logger, int idx, string name, int rows, int cols);

        [LoggerMessage(Level = LogLevel.Information, Message = "[{Idx}] tensor {Name} Track 1 projection complete ({Rows} fireflies in {ElapsedMs}ms)")]
        public static partial void Track1ProjectComplete(ILogger logger, int idx, string name, int rows, long elapsedMs);

        [LoggerMessage(Level = LogLevel.Information, Message = "[{Idx}] tensor {Name} stage: {Stage} (+{ElapsedMs}ms)")]
        public static partial void Track1Stage(ILogger logger, int idx, string name, string stage, long elapsedMs);

        [LoggerMessage(Level = LogLevel.Information, Message = "Batch {BatchNum} submitting: {BatchEntities} entities, {BatchEdges} edges")]
        public static partial void BatchSubmitStart(ILogger logger, int batchNum, int batchEntities, int batchEdges);

        [LoggerMessage(Level = LogLevel.Information, Message = "Batch {BatchNum} committed: +{BatchEntities}E +{BatchEdges}Ed → totals {TotalEntities}E {TotalEdges}Ed ({ElapsedMs}ms)")]
        public static partial void BatchCommitted(ILogger logger, int batchNum, int batchEntities, int batchEdges, long totalEntities, long totalEdges, long elapsedMs);

        [LoggerMessage(Level = LogLevel.Information, Message = "Model {ModelId} complete: {Entities} entities, {Edges} edges, {Fireflies} fireflies in {Batches} batches ({Idx}/{Total})")]
        public static partial void ModelComplete(ILogger logger, string modelId, long entities, long edges, long fireflies, int batches, int idx, int total);
    }
}
