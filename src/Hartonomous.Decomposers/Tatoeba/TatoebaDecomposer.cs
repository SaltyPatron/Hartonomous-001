using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core;
using Hartonomous.Core.Data;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Hartonomous.Core.Text.Segmentation;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Tatoeba;

/// <summary>
/// Streams the Tatoeba exports (sentences.csv, links.csv, sentences_with_audio.csv) into
/// the substrate. Three passes, each bounded in memory.
/// <list type="number">
///   <item><b>Pass 1 — sentences.</b> tatoeba_sentence + text_composition + has_text +
///     entity_language. Sentence identity is content-addressed against the Tatoeba ID
///     (a stable external identifier used across every Tatoeba export + audio reference),
///     mirroring the <c>ud_sentence</c> pattern used by <see cref="Hartonomous.Decomposers.Ud.UdDecomposer"/>.
///     The text itself is a separately content-addressed <c>text_composition</c>, so
///     identical sentence strings with different Tatoeba IDs share one text entity.</item>
///   <item><b>Pass 2 — translation links.</b> translation_link edges between two
///     tatoeba_sentence entities. The decomposer re-emits both sentence hashes on each
///     link batch; the ingestion pipeline's <c>ON CONFLICT (hash, entity_type_id) DO NOTHING</c>
///     dedupe means these collapse onto the pass-1 entities.</item>
///   <item><b>Pass 3 — audio.</b> audio_recording entities + recording_of edges to the
///     attested sentence + has_contributor edges to a text_composition holding the
///     contributor handle. Waveform geometry, FFT/MFCC/pitch/onset analysis edges, and
///     forced-alignment edges to specific tokens are the responsibility of the audio
///     analysis-pass module (tasks #36/#66) — the audio entities created here are the
///     substrate anchors those passes attach to.</item>
/// </list>
/// All three passes are resume-idempotent: re-running from scratch produces the same
/// final substrate state because every emitted entity and edge is content-addressed +
/// ON CONFLICT DO NOTHING.
/// </summary>
public sealed partial class TatoebaDecomposer : BaseDecomposer
{
    public override string ProvenanceCode => "tatoeba";
    public override string DisplayName => "Tatoeba";
    public override IReadOnlyList<Phase> Phases => [Phase.Tatoeba];

    // Tatoeba is community_contributed per substrate.provenance (migration 0015 set this
    // tier to 50000 after the 2000→100000 rescale). Sentences get the flat trust prior;
    // per-sentence corroboration (translation count, audio presence) boosts mu via
    // Glicko-2 Flow 4.2/4.3 at ingest. Emission stays deterministic because content-
    // addressed hashing means the same sentence content yields the same entity row,
    // so corroboration arrives as separate hash collisions on the same identity.
    private const double TrustPriorMu = 50000.0;

    private const string EdgeHasText = "has_text";
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
        ILogger<TatoebaDecomposer> logger,
        ICodepointProperties codepointProperties,
        IReferenceDataReader? referenceDataReader = null,
        IJunctionWriter? junctionWriter = null,
        IReferenceDataWriter? referenceDataWriter = null)
        : base(config, logger)
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
        string audioPath = Path.Combine(_rootDir, "audio", "sentences_with_audio.csv");

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

        // Map Tatoeba integer IDs to content hashes so pass 2 (links) and pass 3 (audio)
        // can resolve sentences by ID without hashing source-specific identifiers.
        Dictionary<int, byte[]> sentenceIdToHash = new(8_000_000);

        // ── Pass 1: sentences ──
        // Each batch carries: tatoeba_sentence + text_composition + has_text edge +
        // entity_language junction — all using EntityHandles in the same batch.
        // No phase-wide ResolveEntityIdsAsync; the pipeline's ON CONFLICT (hash,
        // entity_type_id) DO NOTHING dedupes repeated emissions across passes.
        IIngestionBatch batch = pipeline.CreateBatch();
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
                batch = pipeline.CreateBatch();
            }

            EmitSentence(batch, row, _codepointProperties, languageMap, sentenceIdToHash,
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
        // Both endpoints are tatoeba_sentence entities the prior pass already
        // committed. Re-emit both AddEntity calls in this batch; ON CONFLICT
        // dedupe gives us in-batch handles that map to the existing substrate
        // rows. translation_link edge uses those handles inline.
        batch = pipeline.CreateBatch();
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
                batch = pipeline.CreateBatch();
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
        if (File.Exists(audioPath))
        {
            batch = pipeline.CreateBatch();
            foreach (TatoebaAudioRow ar in TatoebaCsvReader.ReadAudio(audioPath))
            {
                ct.ThrowIfCancellationRequested();

                if (batch.EntityCount >= BatchSize || batch.EdgeCount >= BatchSize)
                {
                    batchNum++;
                    await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount,
                        batchNum, "audio", ct);
                    batch = pipeline.CreateBatch();
                }

                EmitAudio(batch, ar, _codepointProperties, sentenceIdToHash, ref entityCount, ref edgeCount);
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
        Log.Pass3Complete(Logger, pass3Count, entityCount, edgeCount);
    }

    private static void EmitSentence(
        IIngestionBatch batch,
        TatoebaSentenceRow row,
        ICodepointProperties codepointProperties,
        Dictionary<string, int> languageMap,
        Dictionary<int, byte[]> sentenceIdToHash,
        ref long entityCount,
        ref long edgeCount)
    {
        // Sentence text decomposes via the canonical Merkle path:
        //   codepoint → grapheme_cluster → word_form (UAX #29) + raw_span → text_composition.
        // Routes through TextSegmentationEmitter so the word_form layer is preserved
        // and identical word_forms across Tatoeba, WordNet, UD, Wiktionary, and the
        // runtime TextDecomposer collapse to the same content-addressed entity (Law #1).
        // Empty-text rows fall back to a stable Tatoeba-ID-derived hash so the
        // sentence remains addressable for translation_link / recording_of.
        byte[] sentHash;
        EntityHandle sentEntity;
        if (!string.IsNullOrEmpty(row.Text))
        {
            (EntityHandle textEntity, byte[] textHash) =
                TextSegmentationEmitter.EmitTextComposition(
                    batch, row.Text, codepointProperties, "text_composition", TrustPriorMu);
            sentHash = textHash;
            EmitContourPhysicality(batch, textEntity, row.Text);
            entityCount++;

            sentEntity = batch.AddEntity(sentHash, "tatoeba_sentence");
            batch.AddSignificance(sentEntity, "source_authority", TrustPriorMu);
            entityCount++;

            batch.AddEdge(EdgeHasText, "tatoeba",
            [
                new EdgeMemberSpec(sentEntity, null, "source", 0),
                new EdgeMemberSpec(textEntity, null, "target", 1),
            ]);
            edgeCount++;
        }
        else
        {
            sentHash = ComputeHash($"tatoeba_empty:{row.SentenceId}");
            sentEntity = batch.AddEntity(sentHash, "tatoeba_sentence");
            batch.AddSignificance(sentEntity, "source_authority", TrustPriorMu);
            entityCount++;
        }

        sentenceIdToHash[row.SentenceId] = sentHash;

        // entity_language junction inline. Pipeline resolves the in-batch
        // EntityHandle to substrate.entity.id at flush; no phase-wide hash list.
        if (languageMap.TryGetValue(row.Lang, out int langId))
        {
            batch.AddJunction("entity_language", sentEntity, langId);
        }
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

        EntityHandle src = batch.AddEntity(srcHash, "tatoeba_sentence");
        EntityHandle tgt = batch.AddEntity(tgtHash, "tatoeba_sentence");

        batch.AddEdge(EdgeTranslationLink, "tatoeba",
        [
            new EdgeMemberSpec(src, null, "source", 0),
            new EdgeMemberSpec(tgt, null, "target", 1),
        ]);
        edgeCount++;
    }

    private static void EmitAudio(
        IIngestionBatch batch,
        TatoebaAudioRow row,
        ICodepointProperties codepointProperties,
        Dictionary<int, byte[]> sentenceIdToHash,
        ref long entityCount,
        ref long edgeCount)
    {
        byte[] audioHash = ComputeHash($"tatoeba_audio:{row.AudioId}");
        EntityHandle audioEntity = batch.AddEntity(audioHash, "audio_recording");
        batch.AddSignificance(audioEntity, "source_authority", TrustPriorMu);
        entityCount++;

        if (!sentenceIdToHash.TryGetValue(row.SentenceId, out byte[]? sentHash))
        {
            return; // Sentence not seen in pass 1.
        }
        EntityHandle sentEntity = batch.AddEntity(sentHash, "tatoeba_sentence");

        batch.AddEdge(EdgeRecordingOf, "tatoeba",
        [
            new EdgeMemberSpec(audioEntity, null, "source", 0),
            new EdgeMemberSpec(sentEntity, null, "target", 1),
        ]);
        edgeCount++;

        if (!string.IsNullOrEmpty(row.Contributor))
        {
            // Contributor handle decomposes via the canonical Merkle path so the
            // text_composition converges with any other Merkle-hashed occurrence
            // of the same handle (e.g. mention in a Wiktionary citation).
            (EntityHandle contribEntity, byte[] _) =
                TextSegmentationEmitter.EmitTextComposition(
                    batch, row.Contributor, codepointProperties, "text_composition", TrustPriorMu);
            EmitContourPhysicality(batch, contribEntity, row.Contributor);
            entityCount++;

            batch.AddEdge(EdgeHasContributor, "tatoeba",
            [
                new EdgeMemberSpec(audioEntity, null, "source", 0),
                new EdgeMemberSpec(contribEntity, null, "target", 1),
            ]);
            edgeCount++;
        }
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

        [LoggerMessage(Level = LogLevel.Information, Message = "Tatoeba pass 3: {Count} audio manifest rows scanned")]
        public static partial void AudioScanned(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Tatoeba pass 3 complete: {Rows} audio rows, {Entities} entities, {Edges} edges")]
        public static partial void Pass3Complete(ILogger logger, long rows, long entities, long edges);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Tatoeba audio manifest missing at {Path} — pass 3 skipped")]
        public static partial void AudioManifestMissing(ILogger logger, string path);
    }
}
