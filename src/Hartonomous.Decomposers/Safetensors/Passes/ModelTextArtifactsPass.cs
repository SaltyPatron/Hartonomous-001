using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Geometry;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Funnels a model package's on-disk text artifacts through the shared native
/// text decomposer so the model's tokenizer / config / chat template / etc.
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
    private readonly SubstrateTextDecomposer _substrateTextDecomposer;

    public ModelTextArtifactsPass(
        ILogger logger,
        SubstrateTextDecomposer substrateTextDecomposer)
    {
        _logger = logger;
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

            // In-process text decomposition. libhartonomous walks UAX#29 +
            // BLAKE3 + 4D centroids and fires a callback per record; the
            // callback populates session.Batch. One P/Invoke per artifact;
            // no SQL roundtrip. AP-9: model_source linkage is placement
            // metadata; we attach it AFTER the root entity exists in the batch.
            long modelSourceId = context.Source.ModelSourceId;
            Hartonomous.Core.Text.TextDecomposeResult result;
            if (string.Equals(fileName, "tokenizer.json", StringComparison.Ordinal))
            {
                // tokenizer.json is STRUCTURED DATA — running UAX#29 on the
                // whole 7+ MB blob (which is mostly { "id": 151643, "content":
                // "...", "single_word": false, ... } scaffolding) takes 20+
                // minutes per model and produces text_composition entities for
                // every JSON brace / quote / comma. Instead: hash the raw
                // bytes as the artifact root (so cross-model dedup still
                // works on identical tokenizers), parse the JSON, and emit
                // one word_form per vocab entry via the per-string text path.
                result = EmitTokenizerJsonArtifact(session, context, utf8Bytes, ct);
            }
            else
            {
                result = _substrateTextDecomposer.Emit(
                    session.Batch,
                    utf8Bytes,
                    new Hartonomous.Core.Text.TextDecomposeOptions(
                        ProvenanceCode: context.ProvenanceCode,
                        TopEntityType: "text_composition",
                        TrustMu: ModelDerivedTrustMu));
            }

            EntityHandle artifactHandle = result.RootHandle;
            session.Batch.AddEntityModelSource(artifactHandle, modelSourceId);

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

    /// <summary>
    /// Structural tokenizer.json emit. Hashes the raw bytes for the artifact
    /// root (content-addressed dedup across model snapshots that ship the
    /// same tokenizer), then parses the JSON and emits one word_form per
    /// vocab entry + one per added_token via the per-string text path.
    /// Avoids running UAX#29 over JSON scaffolding (braces, commas,
    /// numeric vocab ids) and gives the substrate the actual tokenizer
    /// vocabulary as content-addressed entities.
    /// </summary>
    private Hartonomous.Core.Text.TextDecomposeResult EmitTokenizerJsonArtifact(
        IPassSession session,
        ModelPassContext context,
        byte[] utf8Bytes,
        CancellationToken ct)
    {
        Hash32 rootHash = Blake3.Hash32(utf8Bytes);
        EntityHandle rootHandle = new(rootHash, "tokenizer_model");
        session.Batch.AddEntity(rootHash, "tokenizer_model");
        long entitiesEmitted = 1;
        long vocabEntries = 0;

        using JsonDocument doc = JsonDocument.Parse(utf8Bytes);
        JsonElement root = doc.RootElement;

        // model.vocab — { token_string: vocab_id }
        if (root.TryGetProperty("model", out JsonElement model) &&
            model.TryGetProperty("vocab", out JsonElement vocab) &&
            vocab.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty entry in vocab.EnumerateObject())
            {
                ct.ThrowIfCancellationRequested();
                string tokenString = entry.Name;
                if (tokenString.Length == 0)
                {
                    continue;
                }
                // Per-vocab-entry text decomposition: small input, fast path.
                Hartonomous.Core.Text.TextDecomposeResult tokResult = _substrateTextDecomposer.Emit(
                    session.Batch,
                    System.Text.Encoding.UTF8.GetBytes(tokenString),
                    new Hartonomous.Core.Text.TextDecomposeOptions(
                        ProvenanceCode: context.ProvenanceCode,
                        TopEntityType: "word_form",
                        TrustMu: ModelDerivedTrustMu));
                entitiesEmitted += tokResult.EntitiesEmitted;
                vocabEntries++;
            }
        }

        // added_tokens — special tokens with kind / id fields. The
        // app-tier tokenizer_special_token table already carries family-
        // level rows; per-model overrides live in substrate.tokenizer_special_token
        // with the tokenizer_model entity hash as the key. Emit the
        // token strings as word_forms so they collapse with the vocab
        // entries by content.
        if (root.TryGetProperty("added_tokens", out JsonElement added) &&
            added.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in added.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                if (!entry.TryGetProperty("content", out JsonElement contentEl) ||
                    contentEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                string tokenString = contentEl.GetString() ?? string.Empty;
                if (tokenString.Length == 0)
                {
                    continue;
                }
                Hartonomous.Core.Text.TextDecomposeResult tokResult = _substrateTextDecomposer.Emit(
                    session.Batch,
                    System.Text.Encoding.UTF8.GetBytes(tokenString),
                    new Hartonomous.Core.Text.TextDecomposeOptions(
                        ProvenanceCode: context.ProvenanceCode,
                        TopEntityType: "word_form",
                        TrustMu: ModelDerivedTrustMu));
                entitiesEmitted += tokResult.EntitiesEmitted;
                vocabEntries++;
            }
        }

        Log.TokenizerJsonStructured(_logger, context.Source.ModelId, vocabEntries, entitiesEmitted);

        return new Hartonomous.Core.Text.TextDecomposeResult(
            RootHandle: rootHandle,
            RootHash: rootHash,
            EntitiesEmitted: entitiesEmitted,
            CompositionChildrenEmitted: 0,
            PhysicalityRowsEmitted: 0,
            SignificanceRowsEmitted: 0,
            RootCentroid: (0, 0, 0, 0));
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

        [LoggerMessage(Level = LogLevel.Information, Message = "[text-artifacts {ModelId}] tokenizer.json structural parse — {VocabEntries} vocab entries, {Entities} entities emitted")]
        public static partial void TokenizerJsonStructured(ILogger logger, string modelId, long vocabEntries, long entities);

        [LoggerMessage(Level = LogLevel.Information, Message = "[text-artifacts {ModelId}] pass complete — {Artifacts} artifacts, {TotalEntities} total entities")]
        public static partial void PassSummary(ILogger logger, string modelId, int artifacts, long totalEntities);
    }
}
