using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Data;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Errors;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Hartonomous.Core.Text.Segmentation;
using Hartonomous.Core.Text;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Wiktionary;

/// <summary>
/// Streams the wiktextract JSONL into the substrate with the full semantic surface:
/// <list type="bullet">
///   <item>lemma entities + gloss/example text compositions</item>
///   <item>has_sense, has_gloss, has_example edges</item>
///   <item>has_etymology, has_pronunciation, has_hyphenation, has_wikidata edges
///     (lemma → document; text routed through TextDecomposer)</item>
///   <item>has_form edges (lemma → word_form), inflection_of edges
///     (word_form → lemma)</item>
///   <item>translation_of edges (lemma → foreign-language lemma) — produces
///     foreign lemma entities by Merkle identity, with entity_language junction
///     attached per WiktTranslation.LangCode</item>
///   <item>Eight semantic-relation edge types: synonym, antonym, hypernym, hyponym,
///     meronym, coordinate_term, derived, related (lemma → lemma, with shared
///     hypernym/hyponym/meronym/antonym codes converging with WordNet)</item>
///   <item>Eight etymology-template edge types: etym_inherited_from /
///     etym_derived_from / etym_borrowed_from / etym_cognate_with /
///     etym_calque_of / etym_mention / etym_link / etym_etymon</item>
///   <item>entity_pos / entity_language junctions on every lemma, source_authority
///     significance on every emitted entity</item>
/// </list>
/// Hash-FK shape end-to-end: no Channel.CreateBounded and no decomposer-owned
/// ResolveEntityIdsAsync. The decomposer fans JSONL line chunks through the
/// shared ParallelChunkProcessor; the central pipeline owns batching,
/// transactions, substrate diffing, channels, and drain parallelism.
///
/// Cross-lingual translations and etymon links emit target-language lemma
/// entities with their own entity_language junctions. When a caller supplies a
/// LanguageFilter it applies only to SOURCE entries; the unfiltered default
/// ingests every language present in the selected wiktextract source.
/// </summary>
public sealed partial class WiktionaryDecomposer : TextIngestingDecomposer
{
    public override string ProvenanceCode => "wiktextract";
    public override string DisplayName => "Wiktionary (wiktextract JSONL)";
    public override IReadOnlyList<Phase> Phases => [Phase.Wiktionary];

    protected override double TrustPriorMu => 68000.0;
    protected override ICodepointProperties CodepointProperties => _codepointProperties;

    private const int LineChunkSize = 4096;

    private readonly string _jsonlPath;
    private readonly string _configuredSource;
    private readonly ICodepointProperties _codepointProperties;
    private readonly IReferenceDataReader? _referenceDataReader;
    private readonly IJunctionWriter? _junctionWriter;
    private readonly IReferenceDataWriter? _referenceDataWriter;

    public WiktionaryDecomposer(
        DecomposerConfig config,
        Hartonomous.Core.Text.SubstrateTextDecomposer substrateTextDecomposer,
        ILogger<WiktionaryDecomposer> logger,
        ICodepointProperties codepointProperties,
        IReferenceDataReader? referenceDataReader = null,
        IJunctionWriter? junctionWriter = null,
        IReferenceDataWriter? referenceDataWriter = null)
        : base(config, substrateTextDecomposer, logger, textCacheCapacity: 1_000_000)
    {
        _configuredSource = config.SourceDirectory;
        _jsonlPath = ResolveJsonlPath(config.SourceDirectory, config.LanguageFilter);
        _codepointProperties = codepointProperties;
        _referenceDataReader = referenceDataReader;
        _junctionWriter = junctionWriter;
        _referenceDataWriter = referenceDataWriter;
    }

    protected override IReadOnlyList<string> GetSourcePaths() => [_jsonlPath];

    public override Task ValidateSourceAsync(CancellationToken ct)
    {
        if (!File.Exists(_jsonlPath))
        {
            string candidates = string.Join(", ", CandidateJsonlPaths(_configuredSource, LanguageFilter));
            throw new SourceValidationException(
                $"[wiktextract] Wiktionary JSONL source not found. Expected one of: {candidates}. "
                + "Move the wiktextract .jsonl into /vault/Data/Wiktionary or pass --source / configure Hartonomous:Decomposers:Wiktionary:SourcePath explicitly.");
        }

        return Task.CompletedTask;
    }

    private static string ResolveJsonlPath(string configured, IReadOnlyCollection<string>? langCodeFilter)
    {
        foreach (string candidate in CandidateJsonlPaths(configured, langCodeFilter))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(configured, "raw-wiktextract-data.jsonl");
    }

    private static IEnumerable<string> CandidateJsonlPaths(string configured, IReadOnlyCollection<string>? langCodeFilter)
    {
        if (Path.GetExtension(configured).Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            yield return configured;
        }

        string raw = Path.Combine(configured, "raw-wiktextract-data.jsonl");
        string english = Path.Combine(configured, "kaikki.org-dictionary-English.jsonl");
        if (IsEnglishOnlyFilter(langCodeFilter))
        {
            yield return english;
            yield return raw;
        }
        else
        {
            yield return raw;
            yield return english;
        }
    }

    private static bool IsEnglishOnlyFilter(IReadOnlyCollection<string>? langCodeFilter)
    {
        if (langCodeFilter is null || langCodeFilter.Count == 0)
        {
            return false;
        }

        foreach (string code in langCodeFilter)
        {
            if (!string.Equals(code, "en", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(code, "eng", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    protected override async Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        WiktionaryReferenceTableWriter refWriter =
            new(_referenceDataReader!, _junctionWriter!, _referenceDataWriter!);
        try
        {
            Dictionary<string, int> posIdMap = await refWriter.LoadPosMapAsync(ct);
            Dictionary<string, int> langIdMap = await refWriter.LoadLanguageCodeMapAsync(ct);

            // Build a BCP47 + ISO-639-form-aware resolver from the user filter
            // and install it as the LanguageAllowed predicate. From this point
            // on, every LanguageAllowed call (source-entry filter on line 273,
            // translation-target filter on line 517-538) routes through the
            // resolver instead of simple equality. Skips the 25× cross-lingual
            // sprawl when the user picks a narrow set like {en, zh, ja, ko, es,
            // it, fr, ru}; transparently passes through (always-true) when no
            // filter is configured.
            Dictionary<string, int> aliasMap = await refWriter.LoadLanguageAliasMapAsync(ct);
            LanguageFilterResolver langResolver = LanguageFilterResolver.Create(LanguageFilter, aliasMap);
            UseLanguageResolver(langResolver.IsAllowed);
            if (langResolver.IsFiltered)
            {
                Log.LanguageFilterActive(Logger, langResolver.AllowedLanguageCount);
            }

            long entryCount = 0;
            long entityCount = 0;
            long edgeCount = 0;
            int checkpointNum = 0;
            int batchNum = 0;
            ConcurrentDictionary<string, long> edgeCountByType =
                new(Environment.ProcessorCount, 64, StringComparer.Ordinal);

            using WiktionaryJsonlStreamingReader reader =
                new(_jsonlPath, LanguageFilter);

            long nextProgressBytes = 128L * 1024L * 1024L;
            long maxBytesRead = 0;
            object progressGate = new();
            ConcurrentDictionary<long, TaskCompletionSource> chunkCompletion = new();

            TaskCompletionSource CompletionFor(long chunkIndex)
                => chunkCompletion.GetOrAdd(
                    chunkIndex,
                    static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

            Task PreviousChunkCompletion(long chunkIndex)
                => chunkIndex == 0 ? Task.CompletedTask : CompletionFor(chunkIndex - 1).Task;

            async ValueTask ReportProgressCheckpointAsync(WiktionaryJsonlLineChunk chunk, bool force = false)
            {
                long bytesRead = UpdateMax(ref maxBytesRead, chunk.BytesReadAfterChunk);
                bool shouldReport = false;
                int checkpointNow = 0;
                lock (progressGate)
                {
                    if (force || bytesRead >= nextProgressBytes)
                    {
                        checkpointNow = ++checkpointNum;
                        nextProgressBytes = bytesRead + (128L * 1024L * 1024L);
                        shouldReport = true;
                    }
                }

                if (!shouldReport)
                {
                    return;
                }

                double pct = chunk.TotalBytes > 0
                    ? 100.0 * (double)bytesRead / (double)chunk.TotalBytes
                    : 0.0;
                long entriesSnapshot = Interlocked.Read(ref entryCount);
                if (Logger.IsEnabled(LogLevel.Information))
                {
                    Log.Progress(Logger, pct, bytesRead, chunk.TotalBytes, entriesSnapshot, checkpointNow);
                }
                await reporter.ReportAsync(new ProgressSnapshot
                {
                    DecomposerCode = ProvenanceCode,
                    CurrentPhase = "ingestion",
                    EntitiesCreated = Interlocked.Read(ref entityCount),
                    EdgesCreated = Interlocked.Read(ref edgeCount),
                    CurrentFile = "wiktionary",
                    CurrentBatch = Volatile.Read(ref batchNum),
                }, ct).ConfigureAwait(false);
            }

            await ParallelChunkProcessor.RunAsync(
                reader.ReadChunks(LineChunkSize),
                async (chunk, taskCt) =>
            {
                List<IIngestionBatch> completedBatches = [];
                IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);

                void FlushBatch()
                {
                    if (batch.EntityCount == 0 && batch.EdgeCount == 0)
                    {
                        return;
                    }

                    completedBatches.Add(batch);
                    batch = pipeline.CreateBatch(ProvenanceCode);
                }

                void FlushBatchIfFull()
                {
                    if (batch.EntityCount + batch.EdgeCount >= BatchSize)
                    {
                        FlushBatch();
                    }
                }

                try
                {
                    // #32 — rating-event coverage. Every emitted edge carries
                    // the canonical EdgeArenaRouter event array so per-arena
                    // Glicko-2 significance accumulates inline at emit, not
                    // via end-of-phase priming (AP-37). Per AP-1 the arena
                    // list is whatever EdgeArenaRouter resolves for the
                    // edge_type — domain arenas plus the two universal arenas
                    // (source_authority + corroboration_strength).
                    void AddEdgeByCode(string edgeCode, EdgeMemberSpec[] members)
                    {
                        batch.AddEdge(
                            edgeCode,
                            ProvenanceCode,
                            members,
                            ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                            EdgeArenaRouter.EventsFor(edgeCode));
                        edgeCountByType.AddOrUpdate(edgeCode, 1, static (_, current) => current + 1);
                        Interlocked.Increment(ref edgeCount);
                    }

                    // #35 — AP-19 chunked entity-hash pre-probe. Parse the
                    // chunk's entries first, collect every candidate word_form
                    // / lemma hash (entry word, inflection forms, foreign
                    // translation targets, etymology source lemmas, relation
                    // targets), bulk-probe substrate.entity ONCE per chunk,
                    // then emit. Per-chunk pre-dedupe collapses the 30:1
                    // amplification observed in 2026-05-08 telemetry: common
                    // lexicon entities like "the" / "of" / "be" land as
                    // handle-only refs (no full canonical-text-DAG re-walk)
                    // for the 99.x% of chunks where the entity is already
                    // resident. Same shape as Iso639Decomposer's
                    // EmitLanguageNameChunkAsync helper.
                    List<WiktEntry?> chunkParsed = new(chunk.Lines.Count);
                    HashSet<HashKey> chunkCandidates = new();
                    foreach (string line in chunk.Lines)
                    {
                        taskCt.ThrowIfCancellationRequested();
                        WiktEntry? entry = WiktionaryJsonlParser.ParseLine(line);
                        chunkParsed.Add(entry);
                        if (entry is null) { continue; }
                        Interlocked.Increment(ref entryCount);
                        if (!LanguageAllowed(entry.LangCode)) { continue; }
                        if (string.IsNullOrEmpty(entry.Word) || entry.Senses.Count == 0) { continue; }
                        CollectCandidateWordFormHashes(entry, chunkCandidates);
                    }

                    HashSet<HashKey> chunkExisting;
                    if (chunkCandidates.Count > 0)
                    {
                        List<Hash32> probeList = new(chunkCandidates.Count);
                        foreach (HashKey k in chunkCandidates)
                        {
                            probeList.Add(k.Hash);
                        }
                        chunkExisting = await pipeline.GetExistingEntityHashesAsync(probeList, taskCt)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        chunkExisting = new HashSet<HashKey>();
                    }

                    foreach (WiktEntry? maybeEntry in chunkParsed)
                    {
                        taskCt.ThrowIfCancellationRequested();
                        if (maybeEntry is null) { continue; }
                        WiktEntry entry = maybeEntry;
                        if (!LanguageAllowed(entry.LangCode)) { continue; }
                        if (string.IsNullOrEmpty(entry.Word) || entry.Senses.Count == 0) { continue; }

                        Interlocked.Add(ref entityCount,
                            EmitEntry(batch, entry, posIdMap, langIdMap, AddEdgeByCode, chunkExisting));

                        FlushBatchIfFull();

                        long entriesNow = Interlocked.Read(ref entryCount);
                        if (entriesNow % 250_000 == 0)
                        {
                            if (Logger.IsEnabled(LogLevel.Information))
                            {
                                long entitiesSnapshot = Interlocked.Read(ref entityCount);
                                long edgesSnapshot = Interlocked.Read(ref edgeCount);
                                Log.EntriesProcessed(
                                    Logger,
                                    entriesNow,
                                    entitiesSnapshot,
                                    edgesSnapshot);
                            }
                        }
                    }

                    FlushBatch();
                    await PreviousChunkCompletion(chunk.Index).WaitAsync(taskCt).ConfigureAwait(false);

                    foreach (IIngestionBatch readyBatch in completedBatches)
                    {
                        int batchNow = Interlocked.Increment(ref batchNum);
                        await ReportProgressAsync(
                            pipeline,
                            reporter,
                            readyBatch,
                            Interlocked.Read(ref entityCount),
                            Interlocked.Read(ref edgeCount),
                            batchNow,
                            "wiktionary",
                            taskCt).ConfigureAwait(false);
                    }

                    await ReportProgressCheckpointAsync(chunk).ConfigureAwait(false);
                    CompletionFor(chunk.Index).TrySetResult();
                    chunkCompletion.TryRemove(chunk.Index - 1, out _);
                }
                catch (Exception ex)
                {
                    CompletionFor(chunk.Index).TrySetException(ex);
                    throw;
                }
            },
            ParallelChunkProcessor.DefaultDegreeOfParallelism(),
            ct).ConfigureAwait(false);

            await pipeline.DrainPendingAsync(ct).ConfigureAwait(false);
            await ReportProgressCheckpointAsync(
                new WiktionaryJsonlLineChunk(-1, [], Volatile.Read(ref maxBytesRead), reader.TotalBytes),
                force: true).ConfigureAwait(false);

            foreach (KeyValuePair<string, long> kv in edgeCountByType)
            {
                Log.EdgesByType(Logger, kv.Key, kv.Value);
            }
            if (Logger.IsEnabled(LogLevel.Information))
            {
                long entriesSnapshot = Interlocked.Read(ref entryCount);
                long entitiesSnapshot = Interlocked.Read(ref entityCount);
                long edgesSnapshot = Interlocked.Read(ref edgeCount);
                Log.DecompositionComplete(
                    Logger,
                    entriesSnapshot,
                    entitiesSnapshot,
                    edgesSnapshot);
            }
            LogTextCacheStats();
        }
        finally
        {
            await refWriter.DisposeAsync();
        }
    }

    /// <summary>
    /// #35 — AP-19 chunk-level candidate gathering. For each entry visited
    /// during the chunk pre-pass, collect every <c>word_form</c>-tier surface
    /// form that the per-entry emit path will touch: the lemma, every
    /// inflection form, every foreign translation lemma, every etymology
    /// source lemma, every semantic-relation target. The hashes get bulk-
    /// probed against <c>substrate.entity</c> ONCE per chunk; the emit pass
    /// then ref-only's existing entities and emits the diff. Same shape as
    /// Iso639's <c>EmitLanguageNameChunkAsync</c> probe + emit pattern.
    /// </summary>
    private static void CollectCandidateWordFormHashes(WiktEntry entry, HashSet<HashKey> chunkCandidates)
    {
        AddCandidate(entry.Word);
        foreach (WiktForm f in entry.Forms)
        {
            if (!string.IsNullOrEmpty(f.Form) && f.Form != entry.Word)
            {
                AddCandidate(f.Form);
            }
        }
        foreach (WiktEtymologyTemplate t in entry.EtymologyTemplates)
        {
            string? srcWord = ArgOrNull(t.Args, "2") ?? ArgOrNull(t.Args, "1");
            if (!string.IsNullOrEmpty(srcWord))
            {
                AddCandidate(srcWord);
            }
        }
        foreach (WiktTranslation tr in entry.Translations)
        {
            if (!string.IsNullOrEmpty(tr.Word))
            {
                AddCandidate(tr.Word);
            }
        }
        AddRelationCandidates(entry.Synonyms);
        AddRelationCandidates(entry.Antonyms);
        AddRelationCandidates(entry.Hypernyms);
        AddRelationCandidates(entry.Hyponyms);
        AddRelationCandidates(entry.Meronyms);
        AddRelationCandidates(entry.CoordinateTerms);
        AddRelationCandidates(entry.Derived);
        AddRelationCandidates(entry.Related);

        void AddCandidate(string text)
        {
            // Skip pathological inputs that the native text decomposer rejects
            // (whitespace-only, single-codepoint control/punctuation forms that
            // can't materialize as a word_form). hartonomous_text_decompose
            // returns -10 on these; treat them as honest abstention from the
            // candidate probe rather than failing the whole chunk.
            if (string.IsNullOrWhiteSpace(text)) { return; }
            try
            {
                Hash32 h = ComputeWordFormHash(text);
                chunkCandidates.Add(new HashKey(h));
            }
            catch (System.InvalidOperationException)
            {
                // Native decomposer rejected this surface form (e.g. -10
                // invalid-for-word-form). Skip — emit pass also skips and
                // the entry's other word_forms still process normally.
            }
        }

        void AddRelationCandidates(IReadOnlyList<WiktRelation> rels)
        {
            foreach (WiktRelation r in rels)
            {
                if (string.IsNullOrEmpty(r.Word) || r.Word == "—") { continue; }
                AddCandidate(r.Word);
            }
        }
    }

    private static long UpdateMax(ref long target, long value)
    {
        long current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return current;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);

        return value;
    }

    private long EmitEntry(
        IIngestionBatch batch,
        WiktEntry entry,
        Dictionary<string, int> posIdMap,
        Dictionary<string, int> langIdMap,
        Action<string, EdgeMemberSpec[]> addEdge,
        HashSet<HashKey> chunkExisting)
    {
        long entityCount = 0;

        int? ResolveLangId(string? langCode)
        {
            if (string.IsNullOrEmpty(langCode))
            {
                return null;
            }
            return langIdMap.TryGetValue(langCode, out int id) ? id : (int?)null;
        }

        // #35 — AP-19 chunk-local emit-or-ref. If the substrate (or a prior
        // entry in the same chunk) already contains this word_form hash,
        // return a handle-only ref so the full canonical-text-DAG decompose
        // does not re-fire. Otherwise EmitText runs once and mutates the
        // chunk-existing set so subsequent occurrences inside the chunk get
        // the ref path. Same shape as Iso639Decomposer.EmitLanguageNameChunkAsync.
        //
        // Rejected (Handle == default) returns mean the native text
        // decomposer refused to materialize this surface form as a word_form
        // (e.g. whitespace-only / single-codepoint punctuation /
        // hartonomous_text_decompose return -10). Callers MUST check
        // !handle.Hash.IsZero before using the handle. Honest abstention —
        // the surface form is not a valid word_form at the substrate's
        // canonical-text-DAG; we skip emit + downstream edges and let other
        // entries in the chunk process normally.
        (EntityHandle Handle, bool Emitted) EmitOrRefWordForm(string text, string topType)
        {
            if (string.IsNullOrWhiteSpace(text)) { return (default, false); }
            Hash32 h;
            try
            {
                h = ComputeWordFormHash(text);
            }
            catch (System.InvalidOperationException)
            {
                return (default, false);
            }
            HashKey key = new(h);
            if (chunkExisting.Contains(key))
            {
                return (new EntityHandle(h, topType), false);
            }
            try
            {
                (EntityHandle handle, _, _) = EmitText(batch, text, _codepointProperties, topType, TrustPriorMu);
                chunkExisting.Add(key);
                return (handle, true);
            }
            catch (System.InvalidOperationException)
            {
                return (default, false);
            }
        }

        (EntityHandle lemmaHandle, bool lemmaEmitted) = EmitOrRefWordForm(entry.Word, "lemma");
        // Lemma is the entry's primary identity. If the native decomposer
        // rejected it (e.g. entry.Word is a 1-byte punctuation glyph or
        // whitespace-only), abstain on the whole entry — its dependent forms,
        // translations, etymology, relations all anchor to the lemma handle.
        if (lemmaHandle.Hash.IsZero)
        {
            return 0;
        }
        if (lemmaEmitted)
        {
            batch.AddSignificance(lemmaHandle, "source_authority", TrustPriorMu);
            entityCount++;
        }

        // #33 — AP-8 typed has_pos edge on content-hashed POS reference entity.
        // The legacy entity_pos junction stays as a denormalized analytics
        // cache (per AP-8 correction 2026-05-14); the authoritative Glicko-2
        // surface is the edge_significance row on the typed has_pos edge.
        // Same pattern as WordNetDecomposer line 332-357 and OmwDecomposer.
        string? upos = WiktPosMap.ToUpos(entry.Pos);
        if (upos is not null)
        {
            if (posIdMap.TryGetValue(upos, out int posId))
            {
                // Legacy junction (denormalized analytics cache per AP-8)
                batch.AddJunction("entity_pos", lemmaHandle, posId, TrustPriorMu);
            }

            // Unified Glicko surface — has_pos edge on the content-hashed POS
            // reference-vocabulary entity. Idempotent: same code → same hash →
            // one substrate.entity row regardless of which decomposer attests.
            Hash32 posHash = ReferenceVocabularyHashes.PosEntityHash(upos);
            EntityHandle posHandle = batch.AddEntity(posHash, "pos");
            addEdge("has_pos",
            [
                new EdgeMemberSpec(lemmaHandle, "source", 0),
                new EdgeMemberSpec(posHandle,   "target", 1),
            ]);
        }

        int? sourceLangId = ResolveLangId(entry.LangCode);
        if (sourceLangId is int langId && !string.IsNullOrEmpty(entry.LangCode))
        {
            CrossLinkAttestation.EmitLanguageAttestation(batch, lemmaHandle, entry.LangCode, langId, ProvenanceCode);
        }

        foreach (WiktForm form in entry.Forms)
        {
            if (string.IsNullOrEmpty(form.Form) || form.Form == entry.Word)
            {
                continue;
            }
            (EntityHandle infHandle, bool infEmitted) = EmitOrRefWordForm(form.Form, "word_form");
            if (infHandle.Hash.IsZero) { continue; }
            if (infEmitted)
            {
                batch.AddSignificance(infHandle, "source_authority", TrustPriorMu);
                entityCount++;
            }

            if (sourceLangId is int infLang && !string.IsNullOrEmpty(entry.LangCode))
            {
                CrossLinkAttestation.EmitLanguageAttestation(batch, infHandle, entry.LangCode, infLang, ProvenanceCode);
            }

            addEdge("has_form",
            [
                new EdgeMemberSpec(lemmaHandle, "source", 0),
                new EdgeMemberSpec(infHandle,   "target", 1),
            ]);

            addEdge("inflection_of",
            [
                new EdgeMemberSpec(infHandle,   "source", 0),
                new EdgeMemberSpec(lemmaHandle, "target", 1),
            ]);
        }

        foreach (WiktHyphenation hyph in entry.Hyphenations)
        {
            string repr = HyphenationRepresentation(hyph);
            if (string.IsNullOrEmpty(repr))
            {
                continue;
            }
            EntityHandle hyphDoc = IngestText(batch, repr);
            addEdge("has_hyphenation",
            [
                new EdgeMemberSpec(lemmaHandle, "source", 0),
                new EdgeMemberSpec(hyphDoc,     "target", 1),
            ]);
        }

        foreach (WiktSound sound in entry.Sounds)
        {
            foreach (string attr in EnumerateSoundAttrs(sound))
            {
                EntityHandle pDoc = IngestText(batch, attr);
                addEdge("has_pronunciation",
                [
                    new EdgeMemberSpec(lemmaHandle, "source", 0),
                    new EdgeMemberSpec(pDoc,        "target", 1),
                ]);
            }
        }

        if (!string.IsNullOrEmpty(entry.EtymologyText))
        {
            EntityHandle etymologyDoc = IngestText(batch, entry.EtymologyText);
            addEdge("has_etymology",
            [
                new EdgeMemberSpec(lemmaHandle,  "source", 0),
                new EdgeMemberSpec(etymologyDoc, "target", 1),
            ]);
        }

        foreach (WiktEtymologyTemplate t in entry.EtymologyTemplates)
        {
            string? edgeCode = EtymologyTemplateNameToEdgeCode(t.Name);
            if (edgeCode is null)
            {
                continue;
            }

            string? srcWord = ArgOrNull(t.Args, "2") ?? ArgOrNull(t.Args, "1");
            string? srcLang = ArgOrNull(t.Args, "1");
            if (string.IsNullOrEmpty(srcWord))
            {
                if (!string.IsNullOrEmpty(t.Expansion))
                {
                    EntityHandle expansionDoc = IngestText(batch, t.Expansion);
                    addEdge(edgeCode,
                    [
                        new EdgeMemberSpec(lemmaHandle,  "source", 0),
                        new EdgeMemberSpec(expansionDoc, "target", 1),
                    ]);
                }
                continue;
            }

            (EntityHandle srcLemma, bool srcEmitted) = EmitOrRefWordForm(srcWord, "lemma");
            if (srcLemma.Hash.IsZero) { continue; }
            if (srcEmitted)
            {
                batch.AddSignificance(srcLemma, "source_authority", TrustPriorMu);
                entityCount++;
            }
            int? srcLangId = ResolveLangId(srcLang);
            if (srcLangId is int sl && !string.IsNullOrEmpty(srcLang))
            {
                CrossLinkAttestation.EmitLanguageAttestation(batch, srcLemma, srcLang, sl, ProvenanceCode);
            }

            addEdge(edgeCode,
            [
                new EdgeMemberSpec(lemmaHandle, "source", 0),
                new EdgeMemberSpec(srcLemma,    "target", 1),
            ]);
        }

        foreach (WiktTranslation tr in entry.Translations)
        {
            if (string.IsNullOrEmpty(tr.Word))
            {
                continue;
            }
            // Cross-lingual target filter: skip translations to languages the
            // practitioner did not select. Eliminates phantom foreign-lemma
            // entities (and their entity_language junctions + translation_of
            // edges) that would otherwise sprawl the substrate by ~25× for a
            // narrow-language ingest. LanguageAllowed handles BCP47 + ISO 639
            // form normalization when UseLanguageResolver was wired up at
            // decomposer startup; otherwise falls back to exact equality.
            if (!LanguageAllowed(tr.LangCode))
            {
                continue;
            }
            (EntityHandle foreignLemma, bool foreignEmitted) = EmitOrRefWordForm(tr.Word, "lemma");
            if (foreignLemma.Hash.IsZero) { continue; }
            if (foreignEmitted)
            {
                batch.AddSignificance(foreignLemma, "source_authority", TrustPriorMu);
                entityCount++;
            }
            int? trLangId = ResolveLangId(tr.LangCode);
            if (trLangId is int tl && !string.IsNullOrEmpty(tr.LangCode))
            {
                CrossLinkAttestation.EmitLanguageAttestation(batch, foreignLemma, tr.LangCode, tl, ProvenanceCode);
            }

            addEdge("translation_of",
            [
                new EdgeMemberSpec(lemmaHandle,  "source", 0),
                new EdgeMemberSpec(foreignLemma, "target", 1),
            ]);
        }

        entityCount += EmitRelations(batch, lemmaHandle, entry.Synonyms,        "synonym",         ResolveLangId, addEdge, EmitOrRefWordForm);
        entityCount += EmitRelations(batch, lemmaHandle, entry.Antonyms,        "antonym",         ResolveLangId, addEdge, EmitOrRefWordForm);
        entityCount += EmitRelations(batch, lemmaHandle, entry.Hypernyms,       "hypernym",        ResolveLangId, addEdge, EmitOrRefWordForm);
        entityCount += EmitRelations(batch, lemmaHandle, entry.Hyponyms,        "hyponym",         ResolveLangId, addEdge, EmitOrRefWordForm);
        entityCount += EmitRelations(batch, lemmaHandle, entry.Meronyms,        "member_meronym",  ResolveLangId, addEdge, EmitOrRefWordForm);
        entityCount += EmitRelations(batch, lemmaHandle, entry.CoordinateTerms, "coordinate_term", ResolveLangId, addEdge, EmitOrRefWordForm);
        entityCount += EmitRelations(batch, lemmaHandle, entry.Derived,         "derived",         ResolveLangId, addEdge, EmitOrRefWordForm);
        entityCount += EmitRelations(batch, lemmaHandle, entry.Related,         "related",         ResolveLangId, addEdge, EmitOrRefWordForm);

        foreach (WiktSense sense in entry.Senses)
        {
            if (sense.Glosses.Count == 0)
            {
                continue;
            }

            string joinedGloss = string.Join("\n", sense.Glosses);
            if (joinedGloss.Length == 0)
            {
                continue;
            }

            EntityHandle glossDoc = IngestText(batch, joinedGloss);
            addEdge("has_gloss",
            [
                new EdgeMemberSpec(lemmaHandle, "source", 0),
                new EdgeMemberSpec(glossDoc,    "target", 1),
            ]);

            foreach (WiktExample ex in sense.Examples)
            {
                if (string.IsNullOrEmpty(ex.Text))
                {
                    continue;
                }
                EntityHandle exDoc = IngestText(batch, ex.Text);
                addEdge("has_example",
                [
                    new EdgeMemberSpec(lemmaHandle, "source", 0),
                    new EdgeMemberSpec(exDoc,       "target", 1),
                ]);
            }

            foreach (string wd in sense.Wikidata)
            {
                if (string.IsNullOrEmpty(wd))
                {
                    continue;
                }
                EntityHandle wdDoc = IngestText(batch, wd);
                addEdge("has_wikidata",
                [
                    new EdgeMemberSpec(lemmaHandle, "source", 0),
                    new EdgeMemberSpec(wdDoc,       "target", 1),
                ]);
            }
        }

        return entityCount;
    }

    private int EmitRelations(
        IIngestionBatch batch,
        EntityHandle source,
        IReadOnlyList<WiktRelation> relations,
        string edgeCode,
        Func<string?, int?> resolveLang,
        Action<string, EdgeMemberSpec[]> addEdge,
        Func<string, string, (EntityHandle Handle, bool Emitted)> emitOrRefWordForm)
    {
        int entityCount = 0;

        foreach (WiktRelation rel in relations)
        {
            if (string.IsNullOrEmpty(rel.Word) || rel.Word == "—")
            {
                continue;
            }
            // #35 — AP-19. Route the relation target through the chunk-local
            // emit-or-ref helper so common lexicon targets ("the" / "of" /
            // "be") that already exist in substrate collapse to handle-only
            // refs instead of re-walking the canonical text DAG.
            (EntityHandle target, bool targetEmitted) = emitOrRefWordForm(rel.Word, "lemma");
            // Honest abstention: native decomposer rejected this surface form
            // (e.g. 1-byte punctuation rel.Word) — skip the relation edge.
            if (target.Hash.IsZero) { continue; }
            if (targetEmitted)
            {
                batch.AddSignificance(target, "source_authority", TrustPriorMu);
                entityCount++;
            }

            // If WiktRelation carried a language code on a target headword,
            // attach an entity_language junction. wiktextract's relations are
            // implicitly same-language as the entry, so we don't currently have
            // a per-relation lang field — this is a no-op until the parser is
            // extended.
            _ = resolveLang;

            addEdge(edgeCode,
            [
                new EdgeMemberSpec(source, "source", 0),
                new EdgeMemberSpec(target, "target", 1),
            ]);
        }

        return entityCount;
    }

    /// <summary>
    /// Map a Wiktionary etymology template Name to the canonical substrate
    /// edge_type code. Returns null for templates we don't represent (the
    /// long tail of wiktextract templates is huge; we cover the eight
    /// load-bearing kinds).
    /// </summary>
    private static string? EtymologyTemplateNameToEdgeCode(string name) => name switch
    {
        "inh"          => "etym_inherited_from",
        "der"          => "etym_derived_from",
        "bor"          => "etym_borrowed_from",
        "cog"          => "etym_cognate_with",
        "cal"          => "etym_calque_of",
        "m" or "mention"  => "etym_mention",
        "l" or "link"     => "etym_link",
        "etymon"       => "etym_etymon",
        _ => null,
    };

    private static string? ArgOrNull(IReadOnlyDictionary<string, string> args, string key) =>
        args.TryGetValue(key, out string? v) ? v : null;

    private static IEnumerable<string> EnumerateSoundAttrs(WiktSound s)
    {
        if (!string.IsNullOrEmpty(s.Ipa)) { yield return s.Ipa; }
        if (!string.IsNullOrEmpty(s.Enpr)) { yield return s.Enpr; }
        if (!string.IsNullOrEmpty(s.Audio)) { yield return s.Audio; }
        if (!string.IsNullOrEmpty(s.OggUrl)) { yield return s.OggUrl; }
        if (!string.IsNullOrEmpty(s.Mp3Url)) { yield return s.Mp3Url; }
    }

    private static string HyphenationRepresentation(WiktHyphenation hyph)
    {
        // U+00B7 MIDDLE DOT is the canonical hyphenation separator used by
        // Wiktionary's display layer (e.g., "ex·am·ple"). Joining parts with
        // it produces a deterministic surface representation that converges
        // by Merkle identity with any other ingestion of the same hyphenation.
        if (hyph.Parts.Count == 0)
        {
            return string.Empty;
        }
        return string.Join('·', hyph.Parts);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Wiktionary: {Entries} entries processed, {Entities} entities, {Edges} edges")]
        public static partial void EntriesProcessed(ILogger logger, long entries, long entities, long edges);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Edges by type: {Code}={Count}")]
        public static partial void EdgesByType(ILogger logger, string code, long count);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Wiktionary complete: {Entries} entries scanned, {Entities} entities, {Edges} edges")]
        public static partial void DecompositionComplete(ILogger logger, long entries, long entities, long edges);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "wiktionary progress: {Pct:F2}% ({BytesRead:N0}/{TotalBytes:N0} bytes), {Entries:N0} parsed, checkpoint #{CheckpointNum}")]
        public static partial void Progress(ILogger logger, double pct, long bytesRead, long totalBytes, long entries, int checkpointNum);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Wiktionary language filter active: {AllowedCount} canonical language(s) in filter (BCP47 + ISO 639 form normalized via substrate.language alias map)")]
        public static partial void LanguageFilterActive(ILogger logger, int allowedCount);
    }
}
