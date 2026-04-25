using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
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

namespace Hartonomous.Decomposers.Wiktionary;

/// <summary>
/// Streams raw-wiktextract-data.jsonl (20+ GB) line-by-line into the substrate.
/// <list type="bullet">
///   <item>Entity types: lemma, wikt_sense, inflected_form, text_composition, audio_recording.</item>
///   <item>Edges cover has_sense, has_gloss/example/etymology/pronunciation/hyphenation/wikidata/form,
///     inflection_of, translations, the eight semantic-relation kinds, and the structured etymology
///     templates (inh/der/bor/cog/cal/mention/link/etymon).</item>
///   <item>Cross-references to WordNet / UD / OMW are content-addressed: same normalized lemma
///     → same BLAKE3 hash → automatic deduplication with those earlier decomposers.</item>
///   <item>Entries are emitted atomically per-line. The current batch is submitted <em>before</em>
///     a new entry starts if already near capacity; within an entry, all handles live in one batch.</item>
/// </list>
/// Resume-on-crash is content-addressed: rerunning from the start is idempotent (same entry →
/// same hashes → <c>ON CONFLICT DO NOTHING</c>). Byte-offset checkpointing arrives with #38.
/// </summary>
public sealed partial class WiktionaryDecomposer : BaseDecomposer
{
    public override string ProvenanceCode => "wiktextract";
    public override string DisplayName => "Wiktionary (wiktextract JSONL)";
    public override IReadOnlyList<Phase> Phases => [Phase.Wiktionary];

    // Medium trust — community-curated. Distinctly below WordNet (95k) and UD (92k) but above
    // user-submitted content (1k baseline in provenance table). Kept in the 60-70k band so
    // arena updates at inference converge toward truth when Wiktionary corroborates or is
    // corroborated by higher-authority sources.
    private const double TrustPriorMu = 68000.0;

    /// <summary>
    /// Flush accumulated junction data every N entries to bound memory. The 20.4GB
    /// wiktextract file has ~10.5M entries; without periodic flushing the junction
    /// accumulators (POS, language, morph features, needIds) grow to multiple GB.
    /// </summary>
    private const long JunctionFlushInterval = 500_000;

    private const string WiktHasSense = "wikt_has_sense";
    private const string WiktHasGloss = "wikt_has_gloss";
    private const string WiktHasExample = "wikt_has_example";
    private const string WiktHasEtymology = "wikt_has_etymology";
    private const string WiktHasPronunciation = "wikt_has_pronunciation";
    private const string WiktHasHyphenation = "wikt_has_hyphenation";
    private const string WiktHasWikidata = "wikt_has_wikidata";
    private const string WiktHasAudio = "wikt_has_audio";
    private const string WiktHasForm = "wikt_has_form";
    private const string WiktInflectionOf = "wikt_inflection_of";
    private const string WiktTranslation = "wikt_translation";
    private const string WiktSynonym = "wikt_synonym";
    private const string WiktAntonym = "wikt_antonym";
    private const string WiktHypernym = "wikt_hypernym";
    private const string WiktHyponym = "wikt_hyponym";
    private const string WiktMeronym = "wikt_meronym";
    private const string WiktCoordinateTerm = "wikt_coordinate_term";
    private const string WiktDerived = "wikt_derived";
    private const string WiktRelated = "wikt_related";
    private const string WiktEtymInherited = "wikt_etym_inherited_from";
    private const string WiktEtymDerived = "wikt_etym_derived_from";
    private const string WiktEtymBorrowed = "wikt_etym_borrowed_from";
    private const string WiktEtymCognate = "wikt_etym_cognate_with";
    private const string WiktEtymCalque = "wikt_etym_calque_of";
    private const string WiktEtymMention = "wikt_etym_mention";
    private const string WiktEtymLink = "wikt_etym_link";
    private const string WiktEtymEtymon = "wikt_etym_etymon";

    private readonly string _jsonlPath;
    private readonly ICodepointProperties _codepointProperties;
    private readonly IReferenceDataReader? _referenceDataReader;
    private readonly IJunctionWriter? _junctionWriter;
    private readonly IReferenceDataWriter? _referenceDataWriter;

    public WiktionaryDecomposer(
        DecomposerConfig config,
        ILogger<WiktionaryDecomposer> logger,
        ICodepointProperties codepointProperties,
        IReferenceDataReader? referenceDataReader = null,
        IJunctionWriter? junctionWriter = null,
        IReferenceDataWriter? referenceDataWriter = null)
        : base(config, logger)
    {
        _jsonlPath = ResolveJsonlPath(config.SourceDirectory);
        _codepointProperties = codepointProperties;
        _referenceDataReader = referenceDataReader;
        _junctionWriter = junctionWriter;
        _referenceDataWriter = referenceDataWriter;
    }

    protected override IReadOnlyList<string> GetSourcePaths() => [_jsonlPath];

    private static string ResolveJsonlPath(string configured)
    {
        // Allow pointing at either the JSONL file directly or its containing dir.
        if (File.Exists(configured))
        {
            return configured;
        }
        return Path.Combine(configured, "raw-wiktextract-data.jsonl");
    }

    /// <summary>
    /// Number of JSONL entries buffered per chunk for parallel processing. Each chunk
    /// goes through parallel emit → serial batch submit → junction flush. 50K entries
    /// keeps per-chunk memory bounded while amortizing the junction flush overhead.
    /// </summary>
    private const int EntryChunkSize = 50_000;

    /// <summary>Mutable counters shared across parallel chunk processing.</summary>
    private long _entityCount;
    private long _edgeCount;
    private long _entriesProcessed;
    private int _batchNum;

    protected override async Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        await using WiktionaryReferenceTableWriter refWriter = new(_referenceDataReader!, _junctionWriter!, _referenceDataWriter!);

        await PrepareReferenceDataAsync(refWriter, ct);

        Dictionary<string, int> posMap = await refWriter.LoadPosMapAsync(ct);
        Dictionary<string, int> languageMap = await refWriter.LoadLanguageCodeMapAsync(ct);
        Dictionary<(string, string), int> morphFeatMap = await refWriter.LoadMorphFeatureMapAsync(ct);

        Log.ReferenceDataReady(Logger, posMap.Count, languageMap.Count, morphFeatMap.Count);

        _entityCount = 0;
        _edgeCount = 0;
        _entriesProcessed = 0;
        _batchNum = 0;
        long totalPosWritten = 0;
        long totalLangWritten = 0;
        long totalMorphWritten = 0;

        int maxWorkers = Math.Max(1, Environment.ProcessorCount - 1);

        // ── Stream entries from the JSONL file in chunks. ──
        List<WiktEntry> chunk = new(EntryChunkSize);

        foreach (WiktEntry entry in WiktionaryJsonlParser.Parse(_jsonlPath))
        {
            ct.ThrowIfCancellationRequested();

            if (!LanguageAllowed(entry.LangCode))
            {
                continue;
            }

            chunk.Add(entry);

            if (chunk.Count >= EntryChunkSize)
            {
                (int pos, int lang, int morph) = await ProcessChunkAsync(
                    pipeline, reporter, refWriter,
                    chunk, posMap, languageMap, morphFeatMap, maxWorkers, ct);
                totalPosWritten += pos;
                totalLangWritten += lang;
                totalMorphWritten += morph;
                chunk = new(EntryChunkSize);
            }
        }

        // Process remaining entries.
        if (chunk.Count > 0)
        {
            (int pos, int lang, int morph) = await ProcessChunkAsync(
                pipeline, reporter, refWriter,
                chunk, posMap, languageMap, morphFeatMap, maxWorkers, ct);
            totalPosWritten += pos;
            totalLangWritten += lang;
            totalMorphWritten += morph;
        }

        Log.StreamComplete(Logger, _entriesProcessed, _entityCount, _edgeCount);
        Log.JunctionsWritten(Logger, (int)totalPosWritten, (int)totalLangWritten, (int)totalMorphWritten);
    }

    /// <summary>
    /// Process a chunk of entries in parallel: workers emit entities/edges into thread-local
    /// batches, a serial consumer submits batches to the DB, then junctions are flushed.
    /// </summary>
    private async Task<(int Pos, int Lang, int Morph)> ProcessChunkAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        WiktionaryReferenceTableWriter refWriter,
        List<WiktEntry> entries,
        Dictionary<string, int> posMap,
        Dictionary<string, int> languageMap,
        Dictionary<(string, string), int> morphFeatMap,
        int maxWorkers,
        CancellationToken ct)
    {
        ConcurrentBag<(byte[] Hash, string Upos)> perLemmaUpos = [];
        ConcurrentBag<(byte[] Hash, int LangId)> perEntityLang = [];
        ConcurrentBag<(byte[] Hash, int MorphFeatureId)> perInflMorph = [];
        ConcurrentDictionary<byte[], byte> needIdsDict = new(ByteArrayEqualityComparer.Instance);

        // Bounded channel: workers produce batches, consumer submits to DB.
        Channel<IIngestionBatch> batchChannel =
            Channel.CreateBounded<IIngestionBatch>(
                new BoundedChannelOptions(Environment.ProcessorCount * 2)
                {
                    SingleReader = true,
                    FullMode = BoundedChannelFullMode.Wait,
                });

        // ── Consumer: serial batch submission. ──
        Task consumerTask = Task.Run(async () =>
        {
            await foreach (IIngestionBatch b in batchChannel.Reader.ReadAllAsync(ct))
            {
                int num = Interlocked.Increment(ref _batchNum);
                long ents = Interlocked.Read(ref _entityCount);
                long edgs = Interlocked.Read(ref _edgeCount);
                await ReportProgressAsync(pipeline, reporter, b, ents, edgs, num,
                    $"entries={Interlocked.Read(ref _entriesProcessed)}", ct);
            }
        }, ct);

        // ── Workers: parallel entry processing. ──
        await Parallel.ForEachAsync(entries, new ParallelOptions { MaxDegreeOfParallelism = maxWorkers, CancellationToken = ct },
            async (entry, innerCt) =>
            {
                // Thread-local accumulators to avoid contention.
                List<(byte[] Hash, string Upos)> localUpos = new(16);
                List<(byte[] Hash, int LangId)> localLang = new(16);
                List<(byte[] Hash, int MorphFeatureId)> localMorph = new(16);
                List<byte[]> localNeedIds = new(64);
                long localEntities = 0;
                long localEdges = 0;

                IIngestionBatch batch = pipeline.CreateBatch();

                EmitEntry(
                    batch, entry,
                    posMap, languageMap, morphFeatMap,
                    localUpos, localLang, localMorph, localNeedIds,
                    ref localEntities, ref localEdges);

                if (batch.EntityCount > 0 || batch.EdgeCount > 0)
                {
                    await batchChannel.Writer.WriteAsync(batch, innerCt);
                }

                // Merge thread-local accumulators into concurrent collections.
                foreach (var e in localUpos) { perLemmaUpos.Add(e); }
                foreach (var e in localLang) { perEntityLang.Add(e); }
                foreach (var e in localMorph) { perInflMorph.Add(e); }
                foreach (byte[] h in localNeedIds) { needIdsDict.TryAdd(h, 0); }
                Interlocked.Add(ref _entityCount, localEntities);
                Interlocked.Add(ref _edgeCount, localEdges);
                Interlocked.Increment(ref _entriesProcessed);

                long processed = Interlocked.Read(ref _entriesProcessed);
                if (processed % 100_000 == 0)
                {
                    long ents = Interlocked.Read(ref _entityCount);
                    long edgs = Interlocked.Read(ref _edgeCount);
                    Log.EntriesScanned(Logger, processed, ents, edgs);
                }
            });

        batchChannel.Writer.Complete();
        await consumerTask;

        // ── Flush junctions for this chunk. ──
        HashSet<byte[]> needIds = new(needIdsDict.Keys, ByteArrayEqualityComparer.Instance);
        if (needIds.Count == 0)
        {
            return (0, 0, 0);
        }

        (int pos, int lang, int morph) = await FlushJunctionsAsync(
            pipeline, refWriter, posMap, perLemmaUpos, perEntityLang, perInflMorph, needIds, ct);

        long entriesNow = Interlocked.Read(ref _entriesProcessed);
        Log.JunctionsFlushed(Logger, entriesNow, pos, lang, morph);

        return (pos, lang, morph);
    }

    private static async Task PrepareReferenceDataAsync(
        WiktionaryReferenceTableWriter refWriter,
        CancellationToken ct)
    {
        // The WiktMorphTagMap is a closed set — seed every (key, value) up front so runtime
        // tag translation always hits a populated row. Idempotent via ON CONFLICT.
        HashSet<(string, string)> allTagFeatures = [];
        foreach (string tag in AllKnownTags())
        {
            foreach ((string k, string v) in WiktMorphTagMap.Translate([tag]))
            {
                allTagFeatures.Add((k, v));
            }
        }
        await refWriter.PopulateMorphFeaturesAsync(allTagFeatures, ct);

        // Structural edge types (same-language).
        await refWriter.UpsertStructuralEdgeTypeAsync(WiktHasSense, "lemma", "wikt_sense", ct);
        await refWriter.UpsertStructuralEdgeTypeAsync(WiktHasGloss, "wikt_sense", "text_composition", ct);
        await refWriter.UpsertStructuralEdgeTypeAsync(WiktHasExample, "wikt_sense", "text_composition", ct);
        await refWriter.UpsertStructuralEdgeTypeAsync(WiktHasEtymology, "lemma", "text_composition", ct);
        await refWriter.UpsertStructuralEdgeTypeAsync(WiktHasPronunciation, "lemma", "text_composition", ct);
        await refWriter.UpsertStructuralEdgeTypeAsync(WiktHasHyphenation, "lemma", "text_composition", ct);
        await refWriter.UpsertStructuralEdgeTypeAsync(WiktHasWikidata, "wikt_sense", "text_composition", ct);
        await refWriter.UpsertStructuralEdgeTypeAsync(WiktHasAudio, "lemma", "audio_recording", ct);
        await refWriter.UpsertStructuralEdgeTypeAsync(WiktHasForm, "lemma", "inflected_form", ct);
        await refWriter.UpsertStructuralEdgeTypeAsync(WiktInflectionOf, "inflected_form", "lemma", ct);

        // Semantic relations — sense → lemma (relation target is a word, not another sense).
        await refWriter.UpsertStructuralEdgeTypeAsync(WiktSynonym, "wikt_sense", "lemma", ct);
        await refWriter.UpsertStructuralEdgeTypeAsync(WiktAntonym, "wikt_sense", "lemma", ct);
        await refWriter.UpsertStructuralEdgeTypeAsync(WiktHypernym, "wikt_sense", "lemma", ct);
        await refWriter.UpsertStructuralEdgeTypeAsync(WiktHyponym, "wikt_sense", "lemma", ct);
        await refWriter.UpsertStructuralEdgeTypeAsync(WiktMeronym, "wikt_sense", "lemma", ct);
        await refWriter.UpsertStructuralEdgeTypeAsync(WiktCoordinateTerm, "wikt_sense", "lemma", ct);
        await refWriter.UpsertStructuralEdgeTypeAsync(WiktDerived, "lemma", "lemma", ct);
        await refWriter.UpsertStructuralEdgeTypeAsync(WiktRelated, "lemma", "lemma", ct);

        // Cross-lingual edges — translation target + etymology template chains.
        await refWriter.UpsertCrossLingualEdgeTypeAsync(WiktTranslation, "wikt_sense", "lemma", ct);
        await refWriter.UpsertCrossLingualEdgeTypeAsync(WiktEtymInherited, "lemma", "lemma", ct);
        await refWriter.UpsertCrossLingualEdgeTypeAsync(WiktEtymDerived, "lemma", "lemma", ct);
        await refWriter.UpsertCrossLingualEdgeTypeAsync(WiktEtymBorrowed, "lemma", "lemma", ct);
        await refWriter.UpsertCrossLingualEdgeTypeAsync(WiktEtymCognate, "lemma", "lemma", ct);
        await refWriter.UpsertCrossLingualEdgeTypeAsync(WiktEtymCalque, "lemma", "lemma", ct);
        await refWriter.UpsertCrossLingualEdgeTypeAsync(WiktEtymMention, "lemma", "lemma", ct);
        await refWriter.UpsertCrossLingualEdgeTypeAsync(WiktEtymLink, "lemma", "lemma", ct);
        await refWriter.UpsertCrossLingualEdgeTypeAsync(WiktEtymEtymon, "lemma", "lemma", ct);
    }

    private static async Task<(int Pos, int Lang, int Morph)> FlushJunctionsAsync(
        IIngestionPipeline pipeline,
        BaseReferenceTableWriter refWriter,
        Dictionary<string, int> posMap,
        IEnumerable<(byte[] Hash, string Upos)> perLemmaUpos,
        IEnumerable<(byte[] Hash, int LangId)> perEntityLang,
        IEnumerable<(byte[] Hash, int MorphFeatureId)> perInflMorph,
        HashSet<byte[]> needIds,
        CancellationToken ct)
    {
        IReadOnlyDictionary<byte[], long> ids =
            await pipeline.ResolveEntityIdsAsync([.. needIds], ct);

        List<(long EntityId, int PosId)> posEntries = [];
        foreach ((byte[] hash, string upos) in perLemmaUpos)
        {
            if (ids.TryGetValue(hash, out long eid) && posMap.TryGetValue(upos, out int pid))
            {
                posEntries.Add((eid, pid));
            }
        }
        await refWriter.WriteEntityPosJunctionsAsync(posEntries, ct);

        List<(long EntityId, int LangId)> langEntries = [];
        foreach ((byte[] hash, int langId) in perEntityLang)
        {
            if (ids.TryGetValue(hash, out long eid))
            {
                langEntries.Add((eid, langId));
            }
        }
        await refWriter.WriteEntityLanguageJunctionsAsync(langEntries, ct);

        List<(long EntityId, int MfId)> morphEntries = [];
        foreach ((byte[] hash, int mfId) in perInflMorph)
        {
            if (ids.TryGetValue(hash, out long eid))
            {
                morphEntries.Add((eid, mfId));
            }
        }
        await refWriter.WriteEntityMorphFeatureJunctionsAsync(morphEntries, ct);

        return (posEntries.Count, langEntries.Count, morphEntries.Count);
    }

    private static string[] AllKnownTags()
    {
        // Enumerate the closed vocabulary WiktMorphTagMap knows about.
        string[] known = [
            "plural", "singular", "dual",
            "present", "past", "future", "imperfect",
            "participle", "past-participle", "present-participle",
            "gerund", "infinitive", "finite", "supine",
            "comparative", "superlative", "positive",
            "first-person", "second-person", "third-person",
            "masculine", "feminine", "neuter", "common-gender",
            "nominative", "accusative", "genitive", "dative",
            "ablative", "vocative", "locative", "instrumental",
            "indicative", "subjunctive", "imperative", "conditional", "optative",
            "active", "passive", "middle",
            "definite", "indefinite",
            "perfective", "imperfective", "progressive",
        ];
        return known;
    }

    private void EmitEntry(
        IIngestionBatch batch,
        WiktEntry entry,
        Dictionary<string, int> posMap,
        Dictionary<string, int> languageMap,
        Dictionary<(string, string), int> morphFeatMap,
        List<(byte[] Hash, string Upos)> perLemmaUpos,
        List<(byte[] Hash, int LangId)> perEntityLang,
        List<(byte[] Hash, int MorphFeatureId)> perInflMorph,
        List<byte[]> needIds,
        ref long entityCount,
        ref long edgeCount)
    {
        // ── Lemma ──
        string lemmaForm = entry.Word;
        (EntityHandle lemmaEntity, byte[] lemmaHash) =
            EmitLemmaMaybeCompound(batch, lemmaForm, ProvenanceCode);
        batch.AddSignificance(lemmaEntity, "source_authority", TrustPriorMu);
        EmitContourPhysicality(batch, lemmaEntity, lemmaForm);
        needIds.Add(lemmaHash);
        entityCount++;

        string? upos = WiktPosMap.ToUpos(entry.Pos);
        if (upos is not null && posMap.ContainsKey(upos))
        {
            perLemmaUpos.Add((lemmaHash, upos));
        }

        int? langId = languageMap.TryGetValue(entry.LangCode, out int lid) ? lid : null;
        if (langId is int lidFound)
        {
            perEntityLang.Add((lemmaHash, lidFound));
        }

        // ── Senses ──
        EntityHandle[] senseHandles = new EntityHandle[entry.Senses.Count];
        byte[][] senseHashes = new byte[entry.Senses.Count][];
        for (int si = 0; si < entry.Senses.Count; si++)
        {
            WiktSense sense = entry.Senses[si];
            string senseKey = BuildSenseKey(entry, si, sense);
            byte[] senseHash = ComputeHash(senseKey);
            senseHashes[si] = senseHash;
            needIds.Add(senseHash);

            EntityHandle senseEntity = batch.AddEntity(senseHash, "wikt_sense");
            senseHandles[si] = senseEntity;

            double senseMu = TrustPriorMu
                + (sense.Examples.Count * 100.0)
                + (sense.Wikidata.Count * 250.0);
            batch.AddSignificance(senseEntity, "lexical_disambiguation", senseMu);
            entityCount++;

            if (langId is int slid)
            {
                perEntityLang.Add((senseHash, slid));
            }

            batch.AddEdge(WiktHasSense, ProvenanceCode,
            [
                new EdgeMemberSpec(lemmaEntity, null, "source", 0),
                new EdgeMemberSpec(senseEntity, null, "target", 1),
            ]);
            edgeCount++;

            foreach (string gloss in sense.Glosses)
            {
                if (string.IsNullOrEmpty(gloss))
                {
                    continue;
                }
                EntityHandle glossEntity = EmitTextComposition(batch, gloss, needIds, ref entityCount);
                batch.AddEdge(WiktHasGloss, ProvenanceCode,
                [
                    new EdgeMemberSpec(senseEntity, null, "source", 0),
                    new EdgeMemberSpec(glossEntity, null, "target", 1),
                ]);
                edgeCount++;
            }

            foreach (WiktExample ex in sense.Examples)
            {
                if (string.IsNullOrEmpty(ex.Text))
                {
                    continue;
                }
                EntityHandle exEntity = EmitTextComposition(batch, ex.Text, needIds, ref entityCount);
                batch.AddEdge(WiktHasExample, ProvenanceCode,
                [
                    new EdgeMemberSpec(senseEntity, null, "source", 0),
                    new EdgeMemberSpec(exEntity, null, "target", 1),
                ]);
                edgeCount++;
            }

            foreach (string qid in sense.Wikidata)
            {
                if (string.IsNullOrEmpty(qid))
                {
                    continue;
                }
                EntityHandle qidEntity = EmitTextComposition(batch, qid, needIds, ref entityCount);
                batch.AddEdge(WiktHasWikidata, ProvenanceCode,
                [
                    new EdgeMemberSpec(senseEntity, null, "source", 0),
                    new EdgeMemberSpec(qidEntity, null, "target", 1),
                ]);
                edgeCount++;
            }
        }

        // ── Forms (inflected forms) ──
        foreach (WiktForm form in entry.Forms)
        {
            if (string.IsNullOrEmpty(form.Form))
            {
                continue;
            }

            string formText = form.Form;
            (EntityHandle formEntity, byte[] formHash) = EmitWordFormMerkle(batch, formText, "inflected_form");
            needIds.Add(formHash);

            batch.AddSignificance(formEntity, "source_authority", TrustPriorMu);
            EmitContourPhysicality(batch, formEntity, formText);
            entityCount++;

            if (langId is int flid)
            {
                perEntityLang.Add((formHash, flid));
            }

            batch.AddEdge(WiktHasForm, ProvenanceCode,
            [
                new EdgeMemberSpec(lemmaEntity, null, "source", 0),
                new EdgeMemberSpec(formEntity, null, "target", 1),
            ]);
            edgeCount++;

            batch.AddEdge(WiktInflectionOf, ProvenanceCode,
            [
                new EdgeMemberSpec(formEntity, null, "source", 0),
                new EdgeMemberSpec(lemmaEntity, null, "target", 1),
            ]);
            edgeCount++;

            foreach ((string k, string v) in WiktMorphTagMap.Translate(form.Tags))
            {
                if (morphFeatMap.TryGetValue((k, v), out int mfId))
                {
                    perInflMorph.Add((formHash, mfId));
                }
            }
        }

        // ── Sounds (pronunciations, audio) ──
        foreach (WiktSound sound in entry.Sounds)
        {
            if (!string.IsNullOrEmpty(sound.Ipa))
            {
                EntityHandle ipaEntity = EmitTextComposition(batch, sound.Ipa, needIds, ref entityCount);
                batch.AddEdge(WiktHasPronunciation, ProvenanceCode,
                [
                    new EdgeMemberSpec(lemmaEntity, null, "source", 0),
                    new EdgeMemberSpec(ipaEntity, null, "target", 1),
                ]);
                edgeCount++;
            }

            string? audioUrl = sound.OggUrl ?? sound.Mp3Url;
            if (!string.IsNullOrEmpty(audioUrl))
            {
                byte[] audioHash = ComputeHash(audioUrl);
                needIds.Add(audioHash);
                EntityHandle audioEntity = batch.AddEntity(audioHash, "audio_recording");
                batch.AddSignificance(audioEntity, "source_authority", TrustPriorMu);
                entityCount++;

                batch.AddEdge(WiktHasAudio, ProvenanceCode,
                [
                    new EdgeMemberSpec(lemmaEntity, null, "source", 0),
                    new EdgeMemberSpec(audioEntity, null, "target", 1),
                ]);
                edgeCount++;
            }
        }

        // ── Hyphenation ──
        foreach (WiktHyphenation h in entry.Hyphenations)
        {
            string joined = string.Join('-', h.Parts);
            EntityHandle hEntity = EmitTextComposition(batch, joined, needIds, ref entityCount);
            batch.AddEdge(WiktHasHyphenation, ProvenanceCode,
            [
                new EdgeMemberSpec(lemmaEntity, null, "source", 0),
                new EdgeMemberSpec(hEntity, null, "target", 1),
            ]);
            edgeCount++;
        }

        // ── Etymology text + templates ──
        if (!string.IsNullOrEmpty(entry.EtymologyText))
        {
            EntityHandle etymTextEntity = EmitTextComposition(batch, entry.EtymologyText, needIds, ref entityCount);
            batch.AddEdge(WiktHasEtymology, ProvenanceCode,
            [
                new EdgeMemberSpec(lemmaEntity, null, "source", 0),
                new EdgeMemberSpec(etymTextEntity, null, "target", 1),
            ]);
            edgeCount++;
        }

        foreach (WiktEtymologyTemplate tmpl in entry.EtymologyTemplates)
        {
            EmitEtymologyTemplate(batch, lemmaEntity, tmpl, needIds, languageMap, perEntityLang,
                ref entityCount, ref edgeCount);
        }

        // ── Translations ──
        foreach (WiktTranslation t in entry.Translations)
        {
            if (string.IsNullOrEmpty(t.Word))
            {
                continue;
            }

            // Drop cross-lingual edges whose target language is not in the filter.
            // Without this, English entries would emit phantom non-English lemma
            // entities (entity row created by hash but no junctions / no other edges).
            if (!LanguageAllowed(t.LangCode))
            {
                continue;
            }

            string targetLemma = t.Word;
            (EntityHandle targetEntity, byte[] targetHash) =
                EmitLemmaMaybeCompound(batch, targetLemma, ProvenanceCode);
            needIds.Add(targetHash);

            batch.AddSignificance(targetEntity, "source_authority", TrustPriorMu);
            EmitContourPhysicality(batch, targetEntity, targetLemma);
            entityCount++;

            if (t.LangCode is not null && languageMap.TryGetValue(t.LangCode, out int tgtLang))
            {
                perEntityLang.Add((targetHash, tgtLang));
            }

            // Anchor the translation at the best sense we can identify. If the translation
            // row carries a sense hint and matches one of the entry's glosses, use that
            // specific wikt_sense. Otherwise anchor at the first sense (or skip if the entry
            // has no senses at all, which is rare for translation-carrying entries).
            int senseIdx = ResolveTranslationSenseIndex(entry, t.Sense);
            if (senseIdx < 0 || entry.Senses.Count == 0)
            {
                continue;
            }

            batch.AddEdge(WiktTranslation, ProvenanceCode,
            [
                new EdgeMemberSpec(senseHandles[senseIdx], null, "source_sense", 0),
                new EdgeMemberSpec(targetEntity, null, "target_word", 1),
            ]);
            edgeCount++;
        }

        // ── Semantic relations (entry-level and per-sense) ──
        EmitRelations(batch, entry.Synonyms, WiktSynonym, lemmaEntity, senseHandles, needIds, ref entityCount, ref edgeCount);
        EmitRelations(batch, entry.Antonyms, WiktAntonym, lemmaEntity, senseHandles, needIds, ref entityCount, ref edgeCount);
        EmitRelations(batch, entry.Hypernyms, WiktHypernym, lemmaEntity, senseHandles, needIds, ref entityCount, ref edgeCount);
        EmitRelations(batch, entry.Hyponyms, WiktHyponym, lemmaEntity, senseHandles, needIds, ref entityCount, ref edgeCount);
        EmitRelations(batch, entry.Meronyms, WiktMeronym, lemmaEntity, senseHandles, needIds, ref entityCount, ref edgeCount);
        EmitRelations(batch, entry.CoordinateTerms, WiktCoordinateTerm, lemmaEntity, senseHandles, needIds, ref entityCount, ref edgeCount);
        EmitEntryRelations(batch, entry.Derived, WiktDerived, lemmaEntity, needIds, ref entityCount, ref edgeCount);
        EmitEntryRelations(batch, entry.Related, WiktRelated, lemmaEntity, needIds, ref entityCount, ref edgeCount);
    }

    private static string BuildSenseKey(WiktEntry entry, int senseIdx, WiktSense sense)
    {
        // Content-addressed: senseid if present, else gloss(es), else ordinal fallback.
        // The entry (word, lang_code, pos, etymology_number) composes the sense namespace
        // so "bank" meaning river edge (sense 0 of etymology 1) and "bank" meaning financial
        // institution (sense 0 of etymology 2) are distinct entities.
        string entryKey = $"{entry.Word}|{entry.LangCode}|{entry.Pos}|etym{entry.EtymologyNumber ?? 0}";

        if (sense.Senseid.Count > 0 && !string.IsNullOrEmpty(sense.Senseid[0]))
        {
            return $"wikt_sense:{entryKey}|{sense.Senseid[0]}";
        }
        if (sense.Glosses.Count > 0 && !string.IsNullOrEmpty(sense.Glosses[0]))
        {
            return $"wikt_sense:{entryKey}|gloss={sense.Glosses[0]}";
        }
        return $"wikt_sense:{entryKey}|ord={senseIdx}";
    }

    private static int ResolveTranslationSenseIndex(WiktEntry entry, string? senseHint)
    {
        if (entry.Senses.Count == 0)
        {
            return -1;
        }
        if (senseHint is null || senseHint.Length == 0)
        {
            return 0;
        }
        for (int i = 0; i < entry.Senses.Count; i++)
        {
            IReadOnlyList<string> glosses = entry.Senses[i].Glosses;
            for (int j = 0; j < glosses.Count; j++)
            {
                if (glosses[j].Contains(senseHint, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }
        return 0;
    }

    private void EmitRelations(
        IIngestionBatch batch,
        IReadOnlyList<WiktRelation> relations,
        string edgeType,
        EntityHandle lemmaFallback,
        EntityHandle[] senseHandles,
        List<byte[]> needIds,
        ref long entityCount,
        ref long edgeCount)
    {
        foreach (WiktRelation rel in relations)
        {
            if (string.IsNullOrEmpty(rel.Word))
            {
                continue;
            }

            string target = rel.Word;
            (EntityHandle targetEntity, byte[] targetHash) =
                EmitLemmaMaybeCompound(batch, target, ProvenanceCode);
            needIds.Add(targetHash);

            batch.AddSignificance(targetEntity, "source_authority", TrustPriorMu);
            EmitContourPhysicality(batch, targetEntity, target);
            entityCount++;

            // Per-sense relations carry SenseIndex; entry-level relations that attach to a
            // sense-targeted edge_type fall back to the first sense (or the lemma if no senses).
            EntityHandle source;
            if (rel.SenseIndex is int idx && idx >= 0 && idx < senseHandles.Length)
            {
                source = senseHandles[idx];
            }
            else if (senseHandles.Length > 0)
            {
                source = senseHandles[0];
            }
            else
            {
                source = lemmaFallback;
            }

            batch.AddEdge(edgeType, ProvenanceCode,
            [
                new EdgeMemberSpec(source, null, "source", 0),
                new EdgeMemberSpec(targetEntity, null, "target", 1),
            ]);
            edgeCount++;
        }
    }

    private void EmitEntryRelations(
        IIngestionBatch batch,
        IReadOnlyList<WiktRelation> relations,
        string edgeType,
        EntityHandle lemmaEntity,
        List<byte[]> needIds,
        ref long entityCount,
        ref long edgeCount)
    {
        foreach (WiktRelation rel in relations)
        {
            if (string.IsNullOrEmpty(rel.Word))
            {
                continue;
            }

            string target = rel.Word;
            (EntityHandle targetEntity, byte[] targetHash) =
                EmitLemmaMaybeCompound(batch, target, ProvenanceCode);
            needIds.Add(targetHash);

            batch.AddSignificance(targetEntity, "source_authority", TrustPriorMu);
            EmitContourPhysicality(batch, targetEntity, target);
            entityCount++;

            batch.AddEdge(edgeType, ProvenanceCode,
            [
                new EdgeMemberSpec(lemmaEntity, null, "source", 0),
                new EdgeMemberSpec(targetEntity, null, "target", 1),
            ]);
            edgeCount++;
        }
    }

    private void EmitEtymologyTemplate(
        IIngestionBatch batch,
        EntityHandle lemmaEntity,
        WiktEtymologyTemplate tmpl,
        List<byte[]> needIds,
        Dictionary<string, int> languageMap,
        List<(byte[] Hash, int LangId)> perEntityLang,
        ref long entityCount,
        ref long edgeCount)
    {
        string? edgeCode = tmpl.Name switch
        {
            "inh" or "inh+" or "inherited" => WiktEtymInherited,
            "der" or "der+" or "derived" => WiktEtymDerived,
            "bor" or "bor+" or "borrowed" => WiktEtymBorrowed,
            "cog" or "cognate" => WiktEtymCognate,
            "cal" or "calque" => WiktEtymCalque,
            "m" or "mention" => WiktEtymMention,
            "l" or "link" => WiktEtymLink,
            "etymon" => WiktEtymEtymon,
            _ => null,
        };
        if (edgeCode is null)
        {
            return;
        }

        // Arg conventions: "1" = target language code, "2" = source word, "3" = gloss.
        if (!tmpl.Args.TryGetValue("2", out string? sourceWord) || string.IsNullOrEmpty(sourceWord))
        {
            return;
        }

        string target = sourceWord;
        (EntityHandle targetEntity, byte[] targetHash) =
            EmitLemmaMaybeCompound(batch, target, ProvenanceCode);
        needIds.Add(targetHash);

        batch.AddSignificance(targetEntity, "source_authority", TrustPriorMu);
        EmitContourPhysicality(batch, targetEntity, target);
        entityCount++;

        if (tmpl.Args.TryGetValue("1", out string? targetLangCode) &&
            !string.IsNullOrEmpty(targetLangCode) &&
            languageMap.TryGetValue(targetLangCode, out int langId))
        {
            perEntityLang.Add((targetHash, langId));
        }

        batch.AddEdge(edgeCode, ProvenanceCode,
        [
            new EdgeMemberSpec(lemmaEntity, null, "source", 0),
            new EdgeMemberSpec(targetEntity, null, "target", 1),
        ]);
        edgeCount++;
    }

    private EntityHandle EmitTextComposition(
        IIngestionBatch batch,
        string text,
        List<byte[]> needIds,
        ref long entityCount)
    {
        (EntityHandle entity, byte[] hash) = TextSegmentationEmitter.EmitTextComposition(
            batch, text, _codepointProperties, "text_composition", TrustPriorMu);
        needIds.Add(hash);
        EmitContourPhysicality(batch, entity, text);
        entityCount++;
        return entity;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Wiktionary: reference data ready ({Pos} pos, {Lang} languages, {Morph} morph features)")]
        public static partial void ReferenceDataReady(ILogger logger, int pos, int lang, int morph);

        [LoggerMessage(Level = LogLevel.Information, Message = "Wiktionary: {Entries} entries scanned, {Entities} entities, {Edges} edges")]
        public static partial void EntriesScanned(ILogger logger, long entries, long entities, long edges);

        [LoggerMessage(Level = LogLevel.Information, Message = "Wiktionary stream complete: {Entries} entries, {Entities} entities, {Edges} edges")]
        public static partial void StreamComplete(ILogger logger, long entries, long entities, long edges);

        [LoggerMessage(Level = LogLevel.Information, Message = "Wiktionary junction flush at entry {Entries}: {Pos} pos, {Lang} language, {Morph} morph")]
        public static partial void JunctionsFlushed(ILogger logger, long entries, int pos, int lang, int morph);

        [LoggerMessage(Level = LogLevel.Information, Message = "Wiktionary junctions: {Pos} entity_pos, {Lang} entity_language, {Morph} entity_morph_feature")]
        public static partial void JunctionsWritten(ILogger logger, int pos, int lang, int morph);
    }
}
