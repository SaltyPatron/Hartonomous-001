using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
                    void AddEdgeByCode(string edgeCode, EdgeMemberSpec[] members)
                    {
                        batch.AddEdge(edgeCode, ProvenanceCode, members);
                        edgeCountByType.AddOrUpdate(edgeCode, 1, static (_, current) => current + 1);
                        Interlocked.Increment(ref edgeCount);
                    }

                    foreach (string line in chunk.Lines)
                    {
                        taskCt.ThrowIfCancellationRequested();
                        WiktEntry? entry = WiktionaryJsonlParser.ParseLine(line);
                        if (entry is null)
                        {
                            continue;
                        }
                        long entriesNow = Interlocked.Increment(ref entryCount);

                        if (!LanguageAllowed(entry.LangCode))
                        {
                            continue;
                        }
                        if (string.IsNullOrEmpty(entry.Word) || entry.Senses.Count == 0)
                        {
                            continue;
                        }

                        Interlocked.Add(ref entityCount, EmitEntry(batch, entry, posIdMap, langIdMap, AddEdgeByCode));

                        FlushBatchIfFull();

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
        Action<string, EdgeMemberSpec[]> addEdge)
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

        (EntityHandle lemmaHandle, _, _) =
            EmitText(batch, entry.Word, _codepointProperties, "lemma", TrustPriorMu);
        batch.AddSignificance(lemmaHandle, "source_authority", TrustPriorMu);
        entityCount++;

        string? upos = WiktPosMap.ToUpos(entry.Pos);
        if (upos is not null && posIdMap.TryGetValue(upos, out int posId))
        {
            batch.AddJunction("entity_pos", lemmaHandle, posId, TrustPriorMu);
        }
        int? sourceLangId = ResolveLangId(entry.LangCode);
        if (sourceLangId is int langId)
        {
            batch.AddJunction("entity_language", lemmaHandle, langId);
        }

        foreach (WiktForm form in entry.Forms)
        {
            if (string.IsNullOrEmpty(form.Form) || form.Form == entry.Word)
            {
                continue;
            }
            (EntityHandle infHandle, _, _) =
                EmitText(batch, form.Form, _codepointProperties, "word_form", TrustPriorMu);
            batch.AddSignificance(infHandle, "source_authority", TrustPriorMu);
            entityCount++;

            if (sourceLangId is int infLang)
            {
                batch.AddJunction("entity_language", infHandle, infLang);
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

            (EntityHandle srcLemma, _, _) =
                EmitText(batch, srcWord, _codepointProperties, "lemma", TrustPriorMu);
            batch.AddSignificance(srcLemma, "source_authority", TrustPriorMu);
            entityCount++;
            int? srcLangId = ResolveLangId(srcLang);
            if (srcLangId is int sl)
            {
                batch.AddJunction("entity_language", srcLemma, sl);
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
            (EntityHandle foreignLemma, _, _) =
                EmitText(batch, tr.Word, _codepointProperties, "lemma", TrustPriorMu);
            batch.AddSignificance(foreignLemma, "source_authority", TrustPriorMu);
            entityCount++;
            int? trLangId = ResolveLangId(tr.LangCode);
            if (trLangId is int tl)
            {
                batch.AddJunction("entity_language", foreignLemma, tl);
            }

            addEdge("translation_of",
            [
                new EdgeMemberSpec(lemmaHandle,  "source", 0),
                new EdgeMemberSpec(foreignLemma, "target", 1),
            ]);
        }

        entityCount += EmitRelations(batch, lemmaHandle, entry.Synonyms,        "synonym",         ResolveLangId, addEdge);
        entityCount += EmitRelations(batch, lemmaHandle, entry.Antonyms,        "antonym",         ResolveLangId, addEdge);
        entityCount += EmitRelations(batch, lemmaHandle, entry.Hypernyms,       "hypernym",        ResolveLangId, addEdge);
        entityCount += EmitRelations(batch, lemmaHandle, entry.Hyponyms,        "hyponym",         ResolveLangId, addEdge);
        entityCount += EmitRelations(batch, lemmaHandle, entry.Meronyms,        "member_meronym",  ResolveLangId, addEdge);
        entityCount += EmitRelations(batch, lemmaHandle, entry.CoordinateTerms, "coordinate_term", ResolveLangId, addEdge);
        entityCount += EmitRelations(batch, lemmaHandle, entry.Derived,         "derived",         ResolveLangId, addEdge);
        entityCount += EmitRelations(batch, lemmaHandle, entry.Related,         "related",         ResolveLangId, addEdge);

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
        Action<string, EdgeMemberSpec[]> addEdge)
    {
        int entityCount = 0;

        foreach (WiktRelation rel in relations)
        {
            if (string.IsNullOrEmpty(rel.Word) || rel.Word == "—")
            {
                continue;
            }
            (EntityHandle target, _, _) =
                EmitText(batch, rel.Word, _codepointProperties, "lemma", TrustPriorMu);
            batch.AddSignificance(target, "source_authority", TrustPriorMu);
            entityCount++;

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
    }
}
