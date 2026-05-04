using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Data;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Hartonomous.Decomposers.Safetensors.Packages;
using Hartonomous.Decomposers.Safetensors.Passes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Hartonomous.Decomposers.Safetensors;

/// <summary>
/// Safetensors / HuggingFace-hub decomposer. Discovers models under the hub root,
/// pins per-model identity into the typed <c>model_registry</c> /
/// <c>model_publisher</c> / <c>model_source</c> tables, and then hands each
/// model to <see cref="ModelPassOrchestrator"/> which drives the full
/// <see cref="IModelAnalysisPass"/> DAG with checkpoint/resume.
///
/// Per docs/specs/decomposers/safetensors.md and
/// docs/specs/decomposers/analysis-passes.md.
/// </summary>
public sealed partial class SafetensorsDecomposer : BaseDecomposer
{
    public override string ProvenanceCode => "huggingface_model";
    public override string DisplayName => "Safetensors model decomposer (pass DAG + checkpoint/resume)";
    public override IReadOnlyList<Phase> Phases => [Phase.ModelDecomp];

    private const string HuggingFaceRegistryCode = "huggingface";
    private const string HuggingFaceRegistryDisplay = "Hugging Face Hub";

    private readonly string _hubRoot;
    private readonly IReadOnlyCollection<string>? _modelFilter;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IModelPassCheckpointStore? _checkpointStore;
    private readonly IReferenceDataReader? _referenceDataReader;
    private readonly IJunctionWriter? _junctionWriter;
    private readonly IReferenceDataWriter? _referenceDataWriter;
    private readonly Hartonomous.Core.Text.Segmentation.ICodepointProperties? _codepointProperties;
    private readonly NpgsqlDataSource? _alignmentDataSource;
    private readonly Hartonomous.Core.Text.SubstrateTextDecomposer? _substrateTextDecomposer;

    public SafetensorsDecomposer(
        DecomposerConfig config,
        ILogger<SafetensorsDecomposer> logger,
        ILoggerFactory? loggerFactory = null,
        IModelPassCheckpointStore? checkpointStore = null,
        IReferenceDataReader? referenceDataReader = null,
        IJunctionWriter? junctionWriter = null,
        IReferenceDataWriter? referenceDataWriter = null,
        Hartonomous.Core.Text.Segmentation.ICodepointProperties? codepointProperties = null,
        NpgsqlDataSource? alignmentDataSource = null,
        Hartonomous.Core.Text.SubstrateTextDecomposer? substrateTextDecomposer = null)
        : base(config, logger)
    {
        _hubRoot = config.SourceDirectory;
        _modelFilter = config.ModelFilter;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _checkpointStore = checkpointStore;
        _referenceDataReader = referenceDataReader;
        _junctionWriter = junctionWriter;
        _referenceDataWriter = referenceDataWriter;
        _codepointProperties = codepointProperties;
        _alignmentDataSource = alignmentDataSource;
        _substrateTextDecomposer = substrateTextDecomposer;
    }

    protected override IReadOnlyList<string> GetSourcePaths() => [_hubRoot];

    protected override async Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        List<DiscoveredModel> models = DiscoverModels(_hubRoot, _loggerFactory);

        // Apply ModelFilter: when set, only process models whose ModelId
        // ("publisher/name") is in the allowlist. Lets the dependency chain
        // run on the full data root while ModelDecomp targets a specific
        // subset (e.g., MiniLM only, without paying the cost of decomposing
        // every 33B-parameter model in the hub).
        if (_modelFilter is { Count: > 0 })
        {
            HashSet<string> allowed = new(_modelFilter, StringComparer.OrdinalIgnoreCase);
            int beforeCount = models.Count;
            models = models.Where(m => allowed.Contains(m.ModelId)).ToList();
            Log.ModelsFiltered(Logger, beforeCount, models.Count, allowed.Count);
        }

        Log.ModelsDiscovered(Logger, models.Count, _hubRoot);
        if (models.Count == 0)
        {
            return;
        }

        // Identity resolution (publisher / model / source) flows through the same
        // injected services as the ingestion pipeline; the BaseReferenceTableWriter no
        // longer opens its own NpgsqlDataSource (audit A.3 — single connection pool).
        SafetensorsReferenceTableWriter refWriter = new(_referenceDataReader!, _junctionWriter!, _referenceDataWriter!);

        try
        {
            int hfRegistryId = await refWriter.EnsureModelRegistryAsync(
                HuggingFaceRegistryCode, HuggingFaceRegistryDisplay, ct);

            IModelPassCheckpointStore checkpointStore = _checkpointStore
                ?? throw new InvalidOperationException(
                    $"No {nameof(IModelPassCheckpointStore)} was injected. "
                    + "Pass one via the constructor from the composition root.");
            IReadOnlyList<IModelAnalysisPass> passes = BuildPassSet();
            ModelPassOrchestrator orchestrator = new(
                compute: ComputeFacade.Instance,
                checkpointStore: checkpointStore,
                pipeline: pipeline,
                reporter: reporter,
                refWriter: refWriter,
                passes: passes,
                logger: _loggerFactory.CreateLogger<ModelPassOrchestrator>(),
                batchSize: BatchSize,
                provenanceCode: ProvenanceCode,
                codepointProperties: _codepointProperties);

            int modelIdx = 0;
            foreach (DiscoveredModel model in models)
            {
                ct.ThrowIfCancellationRequested();
                modelIdx++;

                // Per-model identity pinning: publisher + model_source. Placement
                // lives here, nowhere inside any entity content hash.
                int publisherId = await refWriter.EnsureModelPublisherAsync(
                    hfRegistryId, model.PublisherSlug, model.PublisherSlug, ct);
                long modelSourceId = await refWriter.EnsureModelSourceAsync(
                    hfRegistryId, publisherId, model.ModelSlug, model.Revision, ct);

                try
                {
                    await orchestrator.RunAsync(model, modelSourceId, modelIdx, models.Count, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) // BOUNDARY: per-model failure isolation — orchestrator persisted an in-flight checkpoint; remaining models must still process.
                {
                    Log.ModelFailed(Logger, ex, model.ModelId, modelIdx, models.Count);
                }
            }
        }
        finally
        {
            await refWriter.DisposeAsync();
            // Release polymorphic donor reader slots and dispose the readers we own.
            foreach (DiscoveredModel m in models)
            {
                if (m.Reader is null)
                {
                    continue;
                }
                if (m.ReaderSlot != 0)
                {
                    DonorReaderRegistry.Release(m.ReaderSlot);
                }
                try
                {
                    await m.Reader.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Log.ReaderDisposeFailed(Logger, ex, m.ModelId);
                }
            }
        }
    }

    private List<IModelAnalysisPass> BuildPassSet()
    {
        List<IModelAnalysisPass> passes =
        [
            new EmbeddingFireflyPass(_loggerFactory.CreateLogger<EmbeddingFireflyPass>()),
            new SparsityAnalysisPass(_loggerFactory.CreateLogger<SparsityAnalysisPass>()),
            new WeightDistributionPass(_loggerFactory.CreateLogger<WeightDistributionPass>()),
            new ActivationRangePass(_loggerFactory.CreateLogger<ActivationRangePass>()),
            new MoERoutingStatsPass(_loggerFactory.CreateLogger<MoERoutingStatsPass>()),
            new SvdPass(_loggerFactory.CreateLogger<SvdPass>()),
            new EigenvaluePass(_loggerFactory.CreateLogger<EigenvaluePass>()),
            new AttentionArchetypePass(_loggerFactory.CreateLogger<AttentionArchetypePass>()),
            new LayerSimilarityPass(_loggerFactory.CreateLogger<LayerSimilarityPass>()),
            new CodecAnalysisPass(_loggerFactory.CreateLogger<CodecAnalysisPass>()),
            new TokenizerMappingPass(_loggerFactory.CreateLogger<TokenizerMappingPass>()),
            new VocabCoveragePass(_loggerFactory.CreateLogger<VocabCoveragePass>()),
            new OneDTensorPass(_loggerFactory.CreateLogger<OneDTensorPass>()),
            new FfnNeuronPass(_loggerFactory.CreateLogger<FfnNeuronPass>()),
            new EmbeddingPositionPass(_loggerFactory.CreateLogger<EmbeddingPositionPass>()),
            new LogitHeadPass(_loggerFactory.CreateLogger<LogitHeadPass>()),
            new LayerNormPass(_loggerFactory.CreateLogger<LayerNormPass>()),
            new AttentionComponentPass(_loggerFactory.CreateLogger<AttentionComponentPass>()),
            new MoeRouteDirectionPass(_loggerFactory.CreateLogger<MoeRouteDirectionPass>()),
            new MoeExpertNeuronPass(_loggerFactory.CreateLogger<MoeExpertNeuronPass>()),
            new RopeFreqPass(_loggerFactory.CreateLogger<RopeFreqPass>()),
            new ObjectQueryPass(_loggerFactory.CreateLogger<ObjectQueryPass>()),
            new ClassHeadPass(_loggerFactory.CreateLogger<ClassHeadPass>()),
            new BboxHeadPass(_loggerFactory.CreateLogger<BboxHeadPass>()),
            new VisionFeaturePass(_loggerFactory.CreateLogger<VisionFeaturePass>()),
            new ModalityBasisPass(_loggerFactory.CreateLogger<ModalityBasisPass>()),
            new LoraComponentPass(_loggerFactory.CreateLogger<LoraComponentPass>()),
            new ConvFilterPass(_loggerFactory.CreateLogger<ConvFilterPass>()),
            new DiffusionComponentPass(_loggerFactory.CreateLogger<DiffusionComponentPass>()),
            new ConformerComponentPass(_loggerFactory.CreateLogger<ConformerComponentPass>()),
            new AudioCodecFilterPass(_loggerFactory.CreateLogger<AudioCodecFilterPass>()),
            new EmbeddingAlignmentPass(_loggerFactory.CreateLogger<EmbeddingAlignmentPass>(), _alignmentDataSource),
            new GrammarExtractionPass(_loggerFactory.CreateLogger<GrammarExtractionPass>(), _alignmentDataSource),
            new FfnEdgeDecompositionPass(_loggerFactory.CreateLogger<FfnEdgeDecompositionPass>()),
        ];

        if (_codepointProperties is not null && _substrateTextDecomposer is not null)
        {
            passes.Add(new ModelTextArtifactsPass(
                _loggerFactory.CreateLogger<ModelTextArtifactsPass>(),
                _codepointProperties,
                _substrateTextDecomposer));
        }

        return passes;
    }

    /// <summary>
    /// Resolves the source path against the four supported donor layouts:
    ///   (1) a hub root containing many <c>models--{publisher}--{name}/</c> dirs → iterate all (HF cache),
    ///   (2) a single <c>models--{publisher}--{name}/</c> dir → iterate its snapshots (HF cache),
    ///   (3) a single <c>snapshots/{revision}/</c> dir with <c>config.json</c> + <c>*.safetensors</c> → ingest just that snapshot (HF cache),
    ///   (4) ANY other directory the polymorphic <see cref="DonorPackageReaderFactory"/> recognizes —
    ///       bare safetensors, .pt / .pth / .bin (PyTorch pickle), multi-subdir (FLUX-style with
    ///       <c>model_index.json</c>). For these the discovered model carries an
    ///       <see cref="IDonorPackageReader"/> and the reader is registered with
    ///       <see cref="DonorReaderRegistry"/> so static SafetensorsReader streaming helpers
    ///       can route tensor-byte reads through it via the donor:// URI scheme.
    /// AWQ / GGUF packages are rejected by the factory and skipped here.
    /// </summary>
    internal static List<DiscoveredModel> DiscoverModels(string sourcePath)
        => DiscoverModels(sourcePath, NullLoggerFactory.Instance);

    internal static List<DiscoveredModel> DiscoverModels(string sourcePath, ILoggerFactory loggerFactory)
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
            else
            {
                TryAddPolymorphic(sourcePath, models, loggerFactory);
            }
            return models;
        }

        if (LooksLikeModelDir(sourcePath))
        {
            CollectFromModelDir(sourcePath, models);
            return models;
        }

        // HF-cache discovery first (well-known shape).
        HashSet<string> consumed = new(StringComparer.OrdinalIgnoreCase);
        foreach (string modelDir in Directory.EnumerateDirectories(sourcePath, "models--*"))
        {
            CollectFromModelDir(modelDir, models);
            consumed.Add(modelDir);
        }

        // Polymorphic discovery for everything else under the hub root.
        foreach (string anyDir in Directory.EnumerateDirectories(sourcePath))
        {
            if (consumed.Contains(anyDir))
            {
                continue;
            }
            string name = Path.GetFileName(anyDir);
            if (name.StartsWith("datasets--", StringComparison.Ordinal))
            {
                continue;
            }
            TryAddPolymorphic(anyDir, models, loggerFactory);
        }

        return models;
    }

    private static void TryAddPolymorphic(string packageRoot, List<DiscoveredModel> sink, ILoggerFactory loggerFactory)
    {
        // HF-style cache layouts that didn't match the canonical "models--*"
        // prefix (e.g. "xmodels--", "ymodels--" for sort-prefixed directories)
        // still contain snapshots/<sha>/<files>. Resolve to the latest snapshot
        // before handing to the polymorphic factory.
        string effectiveRoot = ResolveSnapshotChildIfPresent(packageRoot);
        IDonorPackageReader reader;
        try
        {
            reader = DonorPackageReaderFactory.Open(effectiveRoot, loggerFactory);
        }
        catch (NotSupportedException)
        {
            return;
        }
        catch (InvalidDataException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (FileNotFoundException)
        {
            return;
        }

        DiscoveredModel? built = TryBuildPolymorphicDiscoveredModel(packageRoot, effectiveRoot, reader);
        if (built is null)
        {
            try { reader.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            return;
        }
        sink.Add(built);
    }

    private static DiscoveredModel? TryBuildPolymorphicDiscoveredModel(
        string identityRoot, string effectiveRoot, IDonorPackageReader reader)
    {
        (string publisherSlug, string modelSlug) = ParsePolymorphicIdentity(identityRoot);
        string modelId = $"{publisherSlug}/{modelSlug}";

        // Revision = BLAKE3 of the canonical absolute effective root (32 bytes,
        // hex 64 chars — satisfies model_source.revision CHECK of 20 or 32
        // bytes). Deterministic across runs on the same machine.
        string canonicalPath = Path.GetFullPath(effectiveRoot).Replace('\\', '/');
        byte[] revision = Blake3.Hash(System.Text.Encoding.UTF8.GetBytes(canonicalPath));
        string revisionHex = Convert.ToHexString(revision).ToLowerInvariant();

        // ConfigPath: prefer a real config.json at the effective root; for
        // multi-subdir packages fall back to the first child component's
        // config.json so ArchitectureDetector has something to parse.
        string configPath = Path.Combine(effectiveRoot, "config.json");
        if (!File.Exists(configPath))
        {
            string? firstSubConfig = FindFirstNestedConfig(effectiveRoot);
            if (firstSubConfig is null)
            {
                return null;
            }
            configPath = firstSubConfig;
        }

        int slot = DonorReaderRegistry.Register(reader);
        // SafetensorsFiles intentionally empty — the reader is the source of truth.
        return new DiscoveredModel(
            ModelId: modelId,
            PublisherSlug: publisherSlug,
            ModelSlug: modelSlug,
            Revision: revision,
            RevisionHex: revisionHex,
            ConfigPath: configPath,
            SafetensorsFiles: Array.Empty<string>(),
            Reader: reader,
            ReaderSlot: slot);
    }

    private static (string Publisher, string Model) ParsePolymorphicIdentity(string packageRoot)
    {
        string name = Path.GetFileName(packageRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        // Strip leading single-letter sort prefixes (x/y/z/w) commonly used to
        // re-order HF cache directories on disk: "xmodels--..." → "models--...".
        if (name.Length > 0 && name[0] is 'x' or 'y' or 'z' or 'w' && name.Length > 1)
        {
            string maybe = name[1..];
            if (maybe.StartsWith("models--", StringComparison.Ordinal))
            {
                name = maybe;
            }
        }
        if (name.StartsWith("models--", StringComparison.Ordinal))
        {
            string body = name["models--".Length..];
            int sep = body.IndexOf("--", StringComparison.Ordinal);
            if (sep > 0)
            {
                return (body[..sep], body[(sep + 2)..]);
            }
            return ("unknown", body);
        }
        return ("local", name);
    }

    private static string? FindFirstNestedConfig(string packageRoot)
    {
        foreach (string subdir in Directory.EnumerateDirectories(packageRoot))
        {
            string candidate = Path.Combine(subdir, "config.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string ResolveSnapshotChildIfPresent(string packageRoot)
    {
        string snapshotsDir = Path.Combine(packageRoot, "snapshots");
        if (!Directory.Exists(snapshotsDir))
        {
            return packageRoot;
        }
        string? latest = Directory.EnumerateDirectories(snapshotsDir)
            .OrderByDescending(d => d, StringComparer.Ordinal)
            .FirstOrDefault();
        return latest ?? packageRoot;
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

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Discovered {Count} models under {Root}")]
        public static partial void ModelsDiscovered(ILogger logger, int count, string root);

        [LoggerMessage(Level = LogLevel.Information, Message = "ModelFilter applied: {Before} discovered → {After} match the {AllowedCount}-entry allowlist")]
        public static partial void ModelsFiltered(ILogger logger, int before, int after, int allowedCount);

        [LoggerMessage(Level = LogLevel.Error, Message = "Model FAILED {ModelId} ({Idx}/{Total}) — isolated, continuing with remaining models")]
        public static partial void ModelFailed(ILogger logger, Exception ex, string modelId, int idx, int total);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Donor reader dispose failed for {ModelId} — registry slot released regardless")]
        public static partial void ReaderDisposeFailed(ILogger logger, Exception ex, string modelId);
    }
}
