using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Data;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Hartonomous.Core.Text.Segmentation;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Text;

/// <summary>
/// Decomposes arbitrary UTF-8 text into the substrate using the UAX #29
/// segmentation stack already built in <c>Hartonomous.Core.Text.Segmentation</c>.
///
/// Levels:
///   1. Codepoints — content-addressed via BLAKE3, dedup against UCD-seeded entities.
///   2. Grapheme clusters — UAX #29 extended grapheme clusters. Multi-codepoint
///      clusters are compositions; single-codepoint clusters ARE the codepoint entity.
///   3. Words — UAX #29 word boundaries. Each word is a composition of its grapheme clusters.
///   4. Sentences — UAX #29 sentence boundaries. Each sentence is a composition of its words.
///   5. Document — top-level composition of all sentences.
///
/// Physicality: POINTZM for single-codepoint atoms, LINESTRINGZM contour for
/// compositions (through constituent S3 positions). All via <see cref="PhysicalityEmitter"/>.
///
/// Provenance: <c>user_session</c>. Trust prior mu=1000 (session-scoped).
/// </summary>
public sealed partial class TextDecomposer : BaseDecomposer
{
    private const double SessionTrustMu = 1000.0;

    private readonly string _sourcePath;
    private readonly ICodepointProperties _codepointProperties;

    public override string ProvenanceCode => "user_session";
    public override string DisplayName => "Text Decomposer";
    public override IReadOnlyList<Phase> Phases => [Phase.TextDecomp];

    /// <summary>
    /// Composite handle of the last document this decomposer ingested. Set
    /// by <see cref="DecomposeCoreAsync"/> after a successful decomposition.
    /// Hash-as-PK addressing — integration tests read this to obtain the
    /// composite (entity_type_code, entity_hash) reference for downstream
    /// recompose / lookup, rather than scanning substrate.entity for a
    /// "most recent" surrogate id (which doesn't exist in the new schema).
    /// </summary>
    public EntityHandle? LastDocumentHandle { get; private set; }

    public TextDecomposer(
        DecomposerConfig config,
        ILogger<TextDecomposer> logger,
        ICodepointProperties codepointProperties,
        IReferenceDataReader? referenceDataReader = null,
        IJunctionWriter? junctionWriter = null,
        IReferenceDataWriter? referenceDataWriter = null)
        : base(config, logger)
    {
        _sourcePath = config.SourceDirectory;
        _codepointProperties = codepointProperties;
    }

    protected override IReadOnlyList<string> GetSourcePaths() => [_sourcePath];

    protected override async Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        byte[] utf8Bytes = await File.ReadAllBytesAsync(_sourcePath, ct);
        Log.FileRead(Logger, _sourcePath, utf8Bytes.Length);

        IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);
        // In-process via libhartonomous (no SQL roundtrip). Same UAX#29 +
        // BLAKE3 + 4D centroid pipeline as the PG-extension text_decompose
        // — same hashes, same physicality (Law #6).
        Hartonomous.Core.Text.TextDecomposeResult canonical =
            Hartonomous.Core.Text.SubstrateTextDecomposer.EmitStatic(
                batch, utf8Bytes,
                new Hartonomous.Core.Text.TextDecomposeOptions(
                    ProvenanceCode: ProvenanceCode,
                    TopEntityType: "text_composition",
                    TrustMu: SessionTrustMu));
        LastDocumentHandle = canonical.RootHandle;

        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await ReportProgressAsync(pipeline, reporter, batch,
                canonical.EntitiesEmitted, 0, batchNum: 1, _sourcePath, ct, "document");
        }

        Log.DecompositionComplete(Logger, canonical.EntitiesEmitted, 0, 1);
    }

    // IngestUtf8DocumentIntoBatch and its helpers removed - replaced by
    // Hartonomous.Core.Text.CanonicalTextDecomposer.Emit. See
    // docs/specs/text-decomposer-unification.md.


    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Text: read {Path} ({Bytes} bytes)")]
        public static partial void FileRead(ILogger logger, string path, int bytes);

        // Per-fragment counters: Trace because IngestUtf8DocumentIntoBatch
        // is the seed-uses-core entrypoint every text-bearing fragment routes
        // through (every Wiktionary gloss / WordNet definition / Tatoeba
        // sentence / safetensors config string), so each one fires per call.
        // CLAUDE.md logging rules — Trace per-entity, Debug per-batch,
        // Information per-phase — were inverted here. At Information level
        // a single seed phase would emit millions of log lines and balloon
        // SeedWiktionary.log past 1 GB.
        [LoggerMessage(Level = LogLevel.Trace,
            Message = "Text: {Count} codepoints decoded")]
        public static partial void CodepointsParsed(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Trace,
            Message = "Text: {Count} grapheme clusters (UAX #29)")]
        public static partial void GraphemeClustersSegmented(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Trace,
            Message = "Text: {Count} words (UAX #29)")]
        public static partial void WordsSegmented(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Trace,
            Message = "Text: {Count} sentences (UAX #29)")]
        public static partial void SentencesSegmented(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Trace,
            Message = "Text: {Count} word entities emitted")]
        public static partial void WordEntitiesEmitted(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Trace,
            Message = "Text: {Count} sentence entities emitted")]
        public static partial void SentenceEntitiesEmitted(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Text: complete — {Entities} entities, {Edges} edges, {Batches} batches")]
        public static partial void DecompositionComplete(ILogger logger, long entities, long edges, int batches);
    }
}
