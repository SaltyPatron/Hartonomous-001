using Hartonomous.Core.Compute;
using Hartonomous.Core.Data;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
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
    private readonly ILoggerFactory _loggerFactory;
    private readonly IModelPassCheckpointStore? _checkpointStore;
    private readonly IReferenceDataReader? _referenceDataReader;
    private readonly IJunctionWriter? _junctionWriter;
    private readonly IReferenceDataWriter? _referenceDataWriter;
    private readonly Hartonomous.Core.Text.Segmentation.ICodepointProperties? _codepointProperties;

    public SafetensorsDecomposer(
        DecomposerConfig config,
        ILogger<SafetensorsDecomposer> logger,
        ILoggerFactory? loggerFactory = null,
        IModelPassCheckpointStore? checkpointStore = null,
        IReferenceDataReader? referenceDataReader = null,
        IJunctionWriter? junctionWriter = null,
        IReferenceDataWriter? referenceDataWriter = null,
        Hartonomous.Core.Text.Segmentation.ICodepointProperties? codepointProperties = null)
        : base(config, logger)
    {
        _hubRoot = config.SourceDirectory;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _checkpointStore = checkpointStore;
        _referenceDataReader = referenceDataReader;
        _junctionWriter = junctionWriter;
        _referenceDataWriter = referenceDataWriter;
        _codepointProperties = codepointProperties;
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
                provenanceCode: ProvenanceCode);

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
        ];

        if (_codepointProperties is not null)
        {
            passes.Add(new ModelTextArtifactsPass(
                _loggerFactory.CreateLogger<ModelTextArtifactsPass>(),
                _codepointProperties));
        }

        return passes;
    }

    /// <summary>
    /// Resolves the source path against three valid HuggingFace cache shapes:
    ///   (1) a hub root containing many <c>models--{publisher}--{name}/</c> dirs → iterate all,
    ///   (2) a single <c>models--{publisher}--{name}/</c> dir → iterate its snapshots,
    ///   (3) a single <c>snapshots/{revision}/</c> dir with <c>config.json</c> + <c>*.safetensors</c> → ingest just that snapshot.
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

        [LoggerMessage(Level = LogLevel.Error, Message = "Model FAILED {ModelId} ({Idx}/{Total}) — isolated, continuing with remaining models")]
        public static partial void ModelFailed(ILogger logger, Exception ex, string modelId, int idx, int total);
    }
}
