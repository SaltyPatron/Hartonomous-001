using System.IO;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text.Segmentation;
using Hartonomous.Decomposers.Text;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Funnels a model package's on-disk text artifacts through the text decomposer's
/// segmentation stack so the model's tokenizer / config / chat template / etc.
/// enter the substrate as full text DAGs that dedup against the rest of the
/// substrate by content (codepoint → grapheme → word_form → text_composition →
/// document, all Merkle-hashed).
///
/// Without this pass the safetensors decomposer only ingests tensor weights —
/// the model package is literally un-recomposable: there is no tokenizer to
/// tokenize prompts with, no config to instantiate a target architecture from,
/// no chat template to format conversations with.
///
/// Each artifact's resulting <c>document</c> entity is linked to the
/// model_architecture entity via a typed structural edge (migration 0041):
/// <c>has_config_artifact</c>, <c>has_tokenizer_artifact</c>,
/// <c>has_tokenizer_config_artifact</c>, <c>has_special_tokens_artifact</c>,
/// <c>has_merges_artifact</c>, <c>has_chat_template_artifact</c>,
/// <c>has_generation_config_artifact</c>, <c>has_readme_artifact</c>.
///
/// Same tokenizer.json shipped in two different model snapshots collapses to
/// ONE substrate document entity with TWO has_tokenizer_artifact edges —
/// content-addressed dedup is automatic.
/// </summary>
internal sealed partial class ModelTextArtifactsPass : IModelAnalysisPass
{
    public string PassId => "model.text_artifacts";
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<string> AppliesToArchitectures => [];

    private const double ModelDerivedTrustMu = 60_000.0;

    // Filename → edge type code. Order is the deterministic processing order
    // for Law #6 (same model package + same set of artifacts → same insertion
    // order → identical substrate state).
    private static readonly (string FileName, string EdgeCode)[] Artifacts =
    [
        ("config.json",                 "has_config_artifact"),
        ("tokenizer.json",              "has_tokenizer_artifact"),
        ("tokenizer_config.json",       "has_tokenizer_config_artifact"),
        ("special_tokens_map.json",     "has_special_tokens_artifact"),
        ("merges.txt",                  "has_merges_artifact"),
        ("chat_template.jinja",         "has_chat_template_artifact"),
        ("generation_config.json",      "has_generation_config_artifact"),
        ("README.md",                   "has_readme_artifact"),
    ];

    private readonly ILogger _logger;
    private readonly ICodepointProperties _codepointProperties;
    private readonly SubstrateTextDecomposer _substrateTextDecomposer;

    public ModelTextArtifactsPass(
        ILogger logger,
        ICodepointProperties codepointProperties,
        SubstrateTextDecomposer substrateTextDecomposer)
    {
        _logger = logger;
        _codepointProperties = codepointProperties;
        _substrateTextDecomposer = substrateTextDecomposer;
    }

    public async Task RunAsync(ModelPassContext context, IPassSession session, CancellationToken ct)
    {
        string snapshotDir = context.Source.ModelDirectory;
        if (!Directory.Exists(snapshotDir))
        {
            Log.SnapshotDirMissing(_logger, context.Source.ModelId, snapshotDir);
            return;
        }

        long totalEntities = 0;
        int artifactsIngested = 0;

        foreach ((string fileName, string edgeCode) in Artifacts)
        {
            ct.ThrowIfCancellationRequested();

            string path = Path.Combine(snapshotDir, fileName);
            if (!File.Exists(path))
            {
                Log.ArtifactAbsent(_logger, context.Source.ModelId, fileName);
                continue;
            }

            byte[] utf8Bytes = await File.ReadAllBytesAsync(path, ct);
            if (utf8Bytes.Length == 0)
            {
                Log.ArtifactEmpty(_logger, context.Source.ModelId, fileName);
                continue;
            }

            Log.ArtifactStart(_logger, context.Source.ModelId, fileName, utf8Bytes.Length);

            // Substrate-side text decomposition. The C extension writes the
            // codepoint/grapheme/word/composition DAG directly to substrate
            // core tables in one SPI call. We get back the root hash and
            // register it on the batch so the has_*_artifact edge below can
            // FK to it. Per AP-9 the model_source linkage is placement
            // metadata — passed via p_model_source_id, NOT in the entity hash.
            // entity_model_source.model_source_id is INT in schema; the C# side
            // carries it as long for downstream API compatibility.
            int? modelSourceId = checked((int)context.Source.ModelSourceId);
            Hartonomous.Core.Text.TextDecomposeResult result = await _substrateTextDecomposer.EmitAsync(
                utf8Bytes,
                new Hartonomous.Core.Text.TextDecomposeOptions(
                    ProvenanceCode: context.ProvenanceCode,
                    TopEntityType: "text_composition",
                    TrustMu: ModelDerivedTrustMu),
                modelSourceId,
                ct);

            EntityHandle artifactHandle = session.Batch.AddEntity(result.RootHash, "text_composition");

            session.Batch.AddEdge(edgeCode, context.ProvenanceCode,
            [
                new EdgeMemberSpec(session.ModelEntity, "source", 0),
                new EdgeMemberSpec(artifactHandle, "target", 1),
            ]);

            artifactsIngested++;
            totalEntities += result.EntitiesEmitted;
            Log.ArtifactComplete(_logger, context.Source.ModelId, fileName, result.EntitiesEmitted);

            // Per-artifact flush boundary: tokenizer.json on a large-vocab
            // model produces 60K-450K entities. The substrate-side path
            // already inserts each chunk with ON CONFLICT DO NOTHING, but
            // we still flush per artifact to keep the C# pipeline's edge
            // / edge_member channels from queueing too far ahead of the
            // text DAG that's already in substrate.
            await session.FlushAsync(ct);
        }

        Log.PassSummary(_logger, context.Source.ModelId, artifactsIngested, totalEntities);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "[text-artifacts {ModelId}] snapshot dir missing: {SnapshotDir}")]
        public static partial void SnapshotDirMissing(ILogger logger, string modelId, string snapshotDir);

        [LoggerMessage(Level = LogLevel.Debug, Message = "[text-artifacts {ModelId}] {FileName} not present; skipped")]
        public static partial void ArtifactAbsent(ILogger logger, string modelId, string fileName);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[text-artifacts {ModelId}] {FileName} is empty; skipped")]
        public static partial void ArtifactEmpty(ILogger logger, string modelId, string fileName);

        [LoggerMessage(Level = LogLevel.Information, Message = "[text-artifacts {ModelId}] {FileName} starting ({Bytes} bytes)")]
        public static partial void ArtifactStart(ILogger logger, string modelId, string fileName, int bytes);

        [LoggerMessage(Level = LogLevel.Information, Message = "[text-artifacts {ModelId}] {FileName} complete — {Entities} entities into substrate text DAG")]
        public static partial void ArtifactComplete(ILogger logger, string modelId, string fileName, long entities);

        [LoggerMessage(Level = LogLevel.Information, Message = "[text-artifacts {ModelId}] pass complete — {Artifacts} artifacts, {TotalEntities} total entities")]
        public static partial void PassSummary(ILogger logger, string modelId, int artifacts, long totalEntities);
    }
}
