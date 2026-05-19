using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Data;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Hartonomous.Core.Text.Segmentation;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Tatoeba;

/// <summary>
/// Streams the Tatoeba sentence, translation-link, and audio exports into the
/// substrate. Source-local sentence/audio IDs are phase-local join keys only.
/// <list type="number">
///   <item><b>Pass 1 — sentences.</b> text_composition + entity_language.
///     Sentence identity is the canonical text root hash; Tatoeba IDs are source
///     placement metadata used only to connect links during this pass. Identical
///     sentence strings with different Tatoeba IDs share one text entity.</item>
///   <item><b>Pass 2 — translation links.</b> translation_link edges between two
///     text_composition entities. The decomposer re-emits both sentence hashes on each
///     link batch; the ingestion pipeline's <c>ON CONFLICT (hash, entity_type_id) DO NOTHING</c>
///     dedupe means these collapse onto the pass-1 entities.</item>
///   <item><b>Pass 3 — audio.</b> audio_recording entities are hashed from the
///     actual MP3 bytes, then linked to their attested sentence and contributor.
///     Audio IDs are used only to locate files from the manifest.</item>
/// </list>
/// All passes are resume-idempotent: re-running from scratch produces the same
/// final substrate state because every emitted entity and edge is content-addressed +
/// ON CONFLICT DO NOTHING.
/// </summary>
public sealed partial class TatoebaDecomposer : TextIngestingDecomposer
{
    public override string ProvenanceCode => TatoebaProvenanceCode;
    private const string TatoebaProvenanceCode = "tatoeba";
    public override string DisplayName => "Tatoeba";
    public override IReadOnlyList<Phase> Phases => [Phase.Tatoeba];

    // Tatoeba is community_contributed per substrate.provenance (migration 0015 set this
    // tier to 50000 after the 2000→100000 rescale). Sentences get the flat trust prior;
    // per-sentence corroboration (translation count, audio presence) boosts mu via
    // Glicko-2 Flow 4.2/4.3 at ingest. Emission stays deterministic because content-
    // addressed hashing means the same sentence content yields the same entity row,
    // so corroboration arrives as separate hash collisions on the same identity.
    private const double TrustPriorMuConst = 50000.0;
    protected override double TrustPriorMu => TrustPriorMuConst;
    protected override ICodepointProperties CodepointProperties => _codepointProperties;

    private const string EdgeTranslationLink = "translation_link";
    private const string EdgeRecordingOf = "recording_of";
    private const string EdgeHasContributor = "has_contributor";

    private readonly string _rootDir;
    private readonly ICodepointProperties _codepointProperties;
    private readonly IReferenceDataReader? _referenceDataReader;
    private readonly IJunctionWriter? _junctionWriter;
    private readonly IReferenceDataWriter? _referenceDataWriter;

    public TatoebaDecomposer(
        DecomposerConfig config,
        Hartonomous.Core.Text.SubstrateTextDecomposer substrateTextDecomposer,
        ILogger<TatoebaDecomposer> logger,
        ICodepointProperties codepointProperties,
        IReferenceDataReader? referenceDataReader = null,
        IJunctionWriter? junctionWriter = null,
        IReferenceDataWriter? referenceDataWriter = null)
        : base(config, substrateTextDecomposer, logger)
    {
        _rootDir = config.SourceDirectory;
        _codepointProperties = codepointProperties;
        _referenceDataReader = referenceDataReader;
        _junctionWriter = junctionWriter;
        _referenceDataWriter = referenceDataWriter;
    }

    protected override IReadOnlyList<string> GetSourcePaths() => [_rootDir];

    protected override async Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        string sentencesPath = Path.Combine(_rootDir, "sentences.csv");
        string linksPath = Path.Combine(_rootDir, "links.csv");
        string audioRoot = Path.Combine(_rootDir, "audio");
        string audioPath = Path.Combine(audioRoot, "sentences_with_audio.csv");

        if (!File.Exists(sentencesPath))
        {
            throw new FileNotFoundException(
                $"Tatoeba sentences.csv not found under {_rootDir}", sentencesPath);
        }

        await using TatoebaReferenceTableWriter refWriter = new(_referenceDataReader!, _junctionWriter!, _referenceDataWriter!);
        Dictionary<string, int> languageMap = await refWriter.LoadLanguageCodeMapAsync(ct);
        Log.ReferenceDataReady(Logger, languageMap.Count);

        long entityCount = 0;
        long edgeCount = 0;
        int batchNum = 0;

        // Map Tatoeba integer IDs to content hashes so later passes can resolve
        // links/audio without persisting source-specific identifiers.
        Dictionary<int, byte[]> sentenceIdToHash = new(8_000_000);

        // ── Pass 1: sentences ──
        // Each batch carries: text_composition + entity_language junction — all
        // using EntityHandles in the same batch.
        // No phase-wide ResolveEntityIdsAsync; the pipeline's ON CONFLICT (hash,
        // entity_type_id) DO NOTHING dedupes repeated emissions across passes.
        IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);
        long pass1Count = 0;
        long pass1Filtered = 0;
        foreach (TatoebaSentenceRow row in TatoebaCsvReader.ReadSentences(sentencesPath))
        {
            ct.ThrowIfCancellationRequested();

            if (!LanguageAllowed(row.Lang))
            {
                pass1Filtered++;
                continue;
            }

            if (batch.EntityCount >= BatchSize || batch.EdgeCount >= BatchSize)
            {
                batchNum++;
                await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount,
                    batchNum, "sentences", ct);
                batch = pipeline.CreateBatch(ProvenanceCode);
            }

            EmitSentence(batch, row, languageMap, sentenceIdToHash,
                ref entityCount, ref edgeCount);
            pass1Count++;
            if (pass1Count % 500_000 == 0)
            {
                Log.SentencesScanned(Logger, pass1Count);
            }
        }

        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            batchNum++;
            await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount,
                batchNum, "sentences", ct);
        }
        Log.Pass1Complete(Logger, pass1Count, entityCount, edgeCount);

        // ── Pass 2: translation links ──
        // Both endpoints are text_composition entities the prior pass already
        // committed. Re-emit both AddEntity calls in this batch; ON CONFLICT
        // dedupe gives us in-batch handles that map to the existing substrate
        // rows. translation_link edge uses those handles inline.
        batch = pipeline.CreateBatch(ProvenanceCode);
        long pass2Count = 0;
        long pass2Skipped = 0;
        foreach (TatoebaLinkRow link in TatoebaCsvReader.ReadLinks(linksPath))
        {
            ct.ThrowIfCancellationRequested();

            if (link.SourceId == link.TargetId)
            {
                pass2Skipped++;
                continue;
            }

            if (batch.EntityCount >= BatchSize || batch.EdgeCount >= BatchSize)
            {
                batchNum++;
                await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount,
                    batchNum, "links", ct);
                batch = pipeline.CreateBatch(ProvenanceCode);
            }

            EmitLink(batch, link, sentenceIdToHash, ref edgeCount);
            pass2Count++;
            if (pass2Count % 1_000_000 == 0)
            {
                Log.LinksScanned(Logger, pass2Count);
            }
        }

        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            batchNum++;
            await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount,
                batchNum, "links", ct);
        }
        Log.Pass2Complete(Logger, pass2Count, pass2Skipped, edgeCount);

        // ── Pass 3: audio ──
        long pass3Count = 0;
        long pass3MissingAudio = 0;
        if (File.Exists(audioPath))
        {
            using TatoebaAudioIndex audioIndex = TatoebaAudioIndex.Build(audioRoot);
            Log.AudioFilesIndexed(Logger, audioIndex.Count);

            batch = pipeline.CreateBatch(ProvenanceCode);
            foreach (TatoebaAudioRow ar in TatoebaCsvReader.ReadAudio(audioPath))
            {
                ct.ThrowIfCancellationRequested();

                if (batch.EntityCount >= BatchSize || batch.EdgeCount >= BatchSize)
                {
                    batchNum++;
                    await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount,
                        batchNum, "audio", ct);
                    batch = pipeline.CreateBatch(ProvenanceCode);
                }

                (bool emitted, long emittedEntities, long emittedEdges) =
                    await EmitAudioAsync(batch, ar, sentenceIdToHash, audioIndex, ct).ConfigureAwait(false);
                if (!emitted)
                {
                    pass3MissingAudio++;
                }
                entityCount += emittedEntities;
                edgeCount += emittedEdges;
                pass3Count++;
                if (pass3Count % 250_000 == 0)
                {
                    Log.AudioScanned(Logger, pass3Count);
                }
            }

            if (batch.EntityCount > 0 || batch.EdgeCount > 0)
            {
                batchNum++;
                await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount,
                    batchNum, "audio", ct);
            }
        }
        else
        {
            Log.AudioManifestMissing(Logger, audioPath);
        }
        Log.Pass3Complete(Logger, pass3Count, pass3MissingAudio, entityCount, edgeCount);
    }

    private void EmitSentence(
        IIngestionBatch batch,
        TatoebaSentenceRow row,
        Dictionary<string, int> languageMap,
        Dictionary<int, byte[]> sentenceIdToHash,
        ref long entityCount,
        ref long edgeCount)
    {
        // Sentence text routes through SubstrateTextDecomposer (via IngestText) —
        // one SPI call to substrate.text_decompose does the full
        // codepoint → grapheme_cluster → word_form (UAX #29) → text_composition
        // walk in C against the embedded UCD blob, batches BLAKE3 natively,
        // and writes directly to substrate. Same content from Tatoeba +
        // WordNet examples + Wiktionary citations + user prompts collapses
        // to ONE text_composition entity.
        // Empty-text rows collapse through the same text path. Sentence IDs are
        // placement/source metadata and must not enter the content hash.
        EntityHandle sentEntity = IngestText(batch, row.Text);
        byte[] sentHash = sentEntity.Hash.ToByteArray();
        entityCount++;

        sentenceIdToHash[row.SentenceId] = sentHash;

        // entity_language junction inline, keyed by the text_composition hash;
        // no phase-wide hash list. Retained as denormalized analytics cache
        // per the AP-8 unified-Glicko-surface correction.
        if (languageMap.TryGetValue(row.Lang, out int langId))
        {
            batch.AddJunction("entity_language", sentEntity, langId);
        }

        // Cross-link attestation (Step I of ancient-launching-papert plan): emit
        // has_language edge on the unified substrate.edge_significance surface so
        // cross-source language-coverage consensus accumulates. language_name
        // entity is content-addressed by BLAKE3 over the ISO 639-3 3-letter code
        // — matches CrossLinkAttestation.EmitLanguageAttestation so UD/OMW/
        // WordNet/Wiktionary land on the same language_name entity per-code.
        //
        // Gate 1 Reopening item #32: 5-arg AddEdge with EdgeArenaRouter rating
        // events so per-sentence language attestation fires one Glicko event
        // per routed arena (source_authority for has_language). Substitutes
        // the prior 3-arg form that produced no Glicko games.
        Hartonomous.Core.Compute.Common.Hash32 langHash =
            Hartonomous.Core.Compute.Common.Blake3.Hash32(System.Text.Encoding.UTF8.GetBytes(row.Lang));
        EntityHandle langHandle = batch.AddEntity(langHash, "language_name");
        batch.AddEdge("has_language", ProvenanceCode,
        [
            new EdgeMemberSpec(sentEntity, "source", 0),
            new EdgeMemberSpec(langHandle, "target", 1),
        ],
        ReadOnlySpan<EdgeSignificanceSpec>.Empty,
        EdgeArenaRouter.EventsFor("has_language"));
        edgeCount++;
    }

    private static void EmitLink(
        IIngestionBatch batch,
        TatoebaLinkRow link,
        Dictionary<int, byte[]> sentenceIdToHash,
        ref long edgeCount)
    {
        if (!sentenceIdToHash.TryGetValue(link.SourceId, out byte[]? srcHash) ||
            !sentenceIdToHash.TryGetValue(link.TargetId, out byte[]? tgtHash))
        {
            return; // Source or target sentence not seen in pass 1.
        }

        // P1i (AP-19 amplification fix): both endpoints are text_composition
        // entities the prior pass already emitted into the substrate. Construct
        // EntityHandle directly from the cached hashes rather than re-calling
        // AddEntity, which would queue a redundant entity emission for the
        // pipeline to flush + dedup via ON CONFLICT. With ~12M sentences and
        // ~25M translation links, the redundant emissions amount to ~50M
        // wasted COPY rows per full Tatoeba ingest (30:1+ amplification per
        // 2026-05-08 telemetry). EntityHandle is a value type that just
        // packages (hash, entityTypeCode); the edge_member emission below
        // uses only the hash. No substrate state changes.
        EntityHandle src = new EntityHandle(new Hash32(srcHash), "text_composition");
        EntityHandle tgt = new EntityHandle(new Hash32(tgtHash), "text_composition");

        batch.AddEdge(EdgeTranslationLink, "tatoeba",
        [
            new EdgeMemberSpec(src, "source", 0),
            new EdgeMemberSpec(tgt, "target", 1),
        ],
        ReadOnlySpan<EdgeSignificanceSpec>.Empty,
        EdgeArenaRouter.EventsFor(EdgeTranslationLink));
        edgeCount++;
    }

    private async ValueTask<(bool Emitted, long Entities, long Edges)> EmitAudioAsync(
        IIngestionBatch batch,
        TatoebaAudioRow row,
        Dictionary<int, byte[]> sentenceIdToHash,
        TatoebaAudioIndex audioIndex,
        CancellationToken ct)
    {
        if (!sentenceIdToHash.TryGetValue(row.SentenceId, out byte[]? sentHash))
        {
            return (false, 0, 0);
        }
        Stream? audioStream = audioIndex.OpenRead(row.AudioId);
        if (audioStream is null)
        {
            return (false, 0, 0);
        }

        long entityCount = 0;
        long edgeCount = 0;
        await using (audioStream.ConfigureAwait(false))
        {
            byte[] audioHash = await HashStreamAsync(audioStream, ct).ConfigureAwait(false);
            EntityHandle audioEntity = batch.AddEntity(new Hash32(audioHash), "audio_recording");
            batch.AddSignificance(audioEntity, "source_authority", TrustPriorMu);
            entityCount++;

            // P1i: same AP-19 fix as EmitLink — sentence text_composition was
            // already emitted in pass 1 + cached in sentenceIdToHash. Construct
            // EntityHandle directly rather than re-emitting via AddEntity.
            EntityHandle sentEntity = new EntityHandle(new Hash32(sentHash), "text_composition");

            batch.AddEdge(EdgeRecordingOf, ProvenanceCode,
            [
                new EdgeMemberSpec(audioEntity, "source", 0),
                new EdgeMemberSpec(sentEntity, "target", 1),
            ],
            ReadOnlySpan<EdgeSignificanceSpec>.Empty,
            EdgeArenaRouter.EventsFor(EdgeRecordingOf));
            edgeCount++;

            if (!string.IsNullOrEmpty(row.Contributor))
            {
                EntityHandle contribEntity = IngestText(batch, row.Contributor);
                entityCount++;

                batch.AddEdge(EdgeHasContributor, ProvenanceCode,
                [
                    new EdgeMemberSpec(audioEntity, "source", 0),
                    new EdgeMemberSpec(contribEntity, "target", 1),
                ],
                ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                EdgeArenaRouter.EventsFor(EdgeHasContributor));
                edgeCount++;
            }
        }

        return (true, entityCount, edgeCount);
    }

    private static async ValueTask<byte[]> HashStreamAsync(Stream stream, CancellationToken ct)
    {
        Blake3Hasher hasher = Blake3Hasher.Create();
        byte[] buffer = new byte[1024 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            hasher.Update(buffer.AsSpan(0, read));
        }
        return hasher.Finalize();
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Tatoeba: reference data ready ({Lang} languages)")]
        public static partial void ReferenceDataReady(ILogger logger, int lang);

        [LoggerMessage(Level = LogLevel.Information, Message = "Tatoeba pass 1: {Count} sentences scanned")]
        public static partial void SentencesScanned(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Tatoeba pass 1 complete: {Sentences} sentences, {Entities} entities, {Edges} edges")]
        public static partial void Pass1Complete(ILogger logger, long sentences, long entities, long edges);

        [LoggerMessage(Level = LogLevel.Information, Message = "Tatoeba pass 2: {Count} links scanned")]
        public static partial void LinksScanned(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Tatoeba pass 2 complete: {Links} translation links, {Skipped} self-loops skipped, {Edges} total edges")]
        public static partial void Pass2Complete(ILogger logger, long links, long skipped, long edges);

        [LoggerMessage(Level = LogLevel.Information, Message = "Tatoeba audio files indexed: {Count} MP3 files")]
        public static partial void AudioFilesIndexed(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Tatoeba pass 3: {Count} audio manifest rows scanned")]
        public static partial void AudioScanned(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Tatoeba pass 3 complete: {Rows} audio rows, {MissingAudio} missing/skipped, {Entities} entities, {Edges} edges")]
        public static partial void Pass3Complete(ILogger logger, long rows, long missingAudio, long entities, long edges);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Tatoeba audio manifest missing at {Path} — pass 3 skipped")]
        public static partial void AudioManifestMissing(ILogger logger, string path);

    }
}
