using System;
using System.Collections.Generic;
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

namespace Hartonomous.Decomposers.Iso639;

/// <summary>
/// ISO 639-3 + ISO 639-2 + IETF language tag decomposer.
///
/// AP-19 compliance: every chunk of candidate <c>language_name</c> entity
/// hashes is computed in-process via <see cref="BaseDecomposer.ComputeWordFormHash"/>
/// and probed via <see cref="IIngestionPipeline.GetExistingEntityHashesAsync"/>
/// ONCE per chunk before any <c>EmitText</c> call fires. Existing entities
/// get handle-only references; only missing entities pay the full canonical-
/// text-DAG decomposition cost. The 30:1 write amplification observed on
/// blind-emit decomposers (2026-05-08 telemetry, AP-19 citation) collapses to
/// roughly 1:1 against substrate cardinality for this decomposer.
/// </summary>
public sealed partial class Iso639Decomposer : BaseDecomposer
{
    public override string ProvenanceCode => "sil_international";
    public override string DisplayName => "ISO 639-3 Decomposer (SIL International)";
    public override IReadOnlyList<Phase> Phases => [Phase.Iso639];

    private const double TrustPriorMu = 95000.0;
    private const int PreDedupeChunk = 256;

    private readonly string _sourceDir;
    private readonly ICodepointProperties _codepointProperties;
    private readonly IReferenceDataReader? _referenceDataReader;
    private readonly IJunctionWriter? _junctionWriter;
    private readonly IReferenceDataWriter? _referenceDataWriter;

    public Iso639Decomposer(
        DecomposerConfig config,
        ILogger<Iso639Decomposer> logger,
        ICodepointProperties codepointProperties,
        IReferenceDataReader? referenceDataReader = null,
        IJunctionWriter? junctionWriter = null,
        IReferenceDataWriter? referenceDataWriter = null)
        : base(config, logger)
    {
        _sourceDir = config.SourceDirectory;
        _codepointProperties = codepointProperties;
        _referenceDataReader = referenceDataReader;
        _junctionWriter = junctionWriter;
        _referenceDataWriter = referenceDataWriter;
    }

    protected override IReadOnlyList<string> GetSourcePaths() =>
    [
        Path.Combine(_sourceDir, "iso-639-3.tab"),
        Path.Combine(_sourceDir, "iso-639-3-macrolanguages.tab"),
        Path.Combine(_sourceDir, "iso-639-3_Name_Index.tab"),
        Path.Combine(_sourceDir, "iso-639-3_Retirements.tab"),
    ];

    /// <summary>
    /// AP-19 pre-dedupe helper. Buffers candidate strings + their precomputed
    /// language_name root hashes; on flush, probes the substrate ONCE per chunk
    /// and emits only the missing diff through <see cref="EmitText"/>. For
    /// existing entities the handle is constructed handle-only so downstream
    /// FKs still resolve without the heavy canonical-text-DAG decomposition.
    /// Returns the set of (text, hash, handle) triples in input order.
    /// </summary>
    private async Task<List<(string Text, Hash32 Hash, EntityHandle Handle)>> EmitLanguageNameChunkAsync(
        IIngestionBatch batch,
        IIngestionPipeline pipeline,
        List<string> texts,
        bool addSignificance,
        CancellationToken ct)
    {
        // Step 1: precompute candidate hashes locally + dedupe within chunk.
        List<Hash32> hashes = new(texts.Count);
        Dictionary<HashKey, int> firstIndexByHash = new();
        for (int i = 0; i < texts.Count; i++)
        {
            Hash32 h = ComputeWordFormHash(texts[i]);
            hashes.Add(h);
            firstIndexByHash.TryAdd(new HashKey(h), i);
        }

        // Step 2: one bulk probe per chunk (AP-19).
        HashSet<HashKey> existing = await pipeline.GetExistingEntityHashesAsync(hashes, ct);

        // Step 3: emit only the diff; existing entities get handle-only refs.
        List<(string Text, Hash32 Hash, EntityHandle Handle)> results = new(texts.Count);
        for (int i = 0; i < texts.Count; i++)
        {
            Hash32 hash = hashes[i];
            HashKey key = new(hash);

            EntityHandle handle;
            if (existing.Contains(key))
            {
                handle = new EntityHandle(hash, "language_name");
            }
            else
            {
                // First occurrence in chunk → full EmitText DAG decomposition.
                // Subsequent occurrences fall under existing via this set
                // being mutated below.
                (EntityHandle h, _, _) = EmitText(batch, texts[i], _codepointProperties, "language_name", TrustPriorMu);
                handle = h;
                if (addSignificance)
                {
                    batch.AddSignificance(handle, "source_authority", TrustPriorMu);
                }
                existing.Add(key);
            }
            results.Add((texts[i], hash, handle));
        }
        return results;
    }

    protected override async Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        // ── Parse all four source files ──
        string langPath = Path.Combine(_sourceDir, "iso-639-3.tab");
        string macroPath = Path.Combine(_sourceDir, "iso-639-3-macrolanguages.tab");
        string namePath = Path.Combine(_sourceDir, "iso-639-3_Name_Index.tab");
        string retirePath = Path.Combine(_sourceDir, "iso-639-3_Retirements.tab");

        Log.Parsing(Logger);
        List<Iso639Record> languages = Iso639Parser.ParseLanguages(langPath);
        List<MacrolanguageMapping> macroMappings = Iso639Parser.ParseMacrolanguages(macroPath);
        List<NameIndexEntry> nameIndex = Iso639Parser.ParseNameIndex(namePath);
        List<RetirementRecord> retirements = Iso639Parser.ParseRetirements(retirePath);
        Log.Parsed(Logger, languages.Count, macroMappings.Count, nameIndex.Count, retirements.Count);

        Iso639ReferenceTableWriter refWriter = new(_referenceDataReader!, _junctionWriter!, _referenceDataWriter!);
        try
        {
            // ── Step 1: Populate language reference table ──
            await refWriter.PopulateLanguagesAsync(languages, ct);
            Dictionary<string, int> langIdMap = await refWriter.LoadLanguageCodeMapAsync(ct);
            Log.ReferenceTablePopulated(Logger, langIdMap.Count);

            await reporter.ReportAsync(new ProgressSnapshot
            {
                DecomposerCode = ProvenanceCode,
                CurrentPhase = "reference_table",
                EntitiesCreated = 0,
                EdgesCreated = 0,
            }, ct);

            long entityCount = 0;
            long edgeCount = 0;
            int batchNum = 0;

            // Track code → nameHash for junction rows and edge creation.
            Dictionary<string, Hash32> codeToNameHash = new(languages.Count, StringComparer.Ordinal);
            IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);

            // ── Step 2: Create language_name entities for canonical refNames ──
            // AP-19 chunked: precompute hashes, probe substrate once per chunk,
            // only invoke EmitText (which fans out into the full text DAG) for
            // entities not already in the substrate.
            for (int chunkStart = 0; chunkStart < languages.Count; chunkStart += PreDedupeChunk)
            {
                int chunkEnd = Math.Min(chunkStart + PreDedupeChunk, languages.Count);
                List<string> chunkTexts = new(chunkEnd - chunkStart);
                for (int i = chunkStart; i < chunkEnd; i++) { chunkTexts.Add(languages[i].RefName); }

                var emitted = await EmitLanguageNameChunkAsync(batch, pipeline, chunkTexts, addSignificance: true, ct);
                for (int i = 0; i < emitted.Count; i++)
                {
                    codeToNameHash[languages[chunkStart + i].Id] = emitted[i].Hash;
                    entityCount++;
                }

                if (batch.EntityCount >= BatchSize)
                {
                    batchNum++;
                    await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "iso-639-3", ct);
                    batch = pipeline.CreateBatch(ProvenanceCode);
                }
            }

            // ── Step 3: Alternative names from Name_Index ──
            // Same AP-19 pattern: precompute hashes per chunk, probe, emit diff.
            Dictionary<string, List<Hash32>> codeToAlternateHashes = new(StringComparer.Ordinal);

            // Filter entries first so we only buffer attributable rows.
            List<(NameIndexEntry Entry, Hash32 RefHash, Hash32 PrintHash, Hash32 InvertHash)> filtered = new(nameIndex.Count);
            foreach (NameIndexEntry entry in nameIndex)
            {
                if (!codeToNameHash.TryGetValue(entry.Id, out Hash32 refHash)) { continue; }
                Hash32 printHash = ComputeWordFormHash(entry.PrintName);
                Hash32 invertHash = ComputeWordFormHash(entry.InvertedName);
                filtered.Add((entry, refHash, printHash, invertHash));
            }

            for (int chunkStart = 0; chunkStart < filtered.Count; chunkStart += PreDedupeChunk)
            {
                int chunkEnd = Math.Min(chunkStart + PreDedupeChunk, filtered.Count);
                // Build the list of (text, expectedHash) emit candidates plus
                // a parallel list of (entryId, slot) so we know where to attach
                // hashes after emission.
                List<string> chunkTexts = new(2 * (chunkEnd - chunkStart));
                List<(string EntryId, int Slot, Hash32 ExpectedHash)> slotMap = new(2 * (chunkEnd - chunkStart));

                for (int i = chunkStart; i < chunkEnd; i++)
                {
                    var (entry, refHash, printHash, invertHash) = filtered[i];
                    bool printIsRef = printHash.Equals(refHash);
                    bool invertIsRef = invertHash.Equals(refHash);
                    bool invertIsPrint = invertHash.Equals(printHash);

                    if (!printIsRef)
                    {
                        chunkTexts.Add(entry.PrintName);
                        slotMap.Add((entry.Id, 0, printHash));
                    }
                    if (!invertIsRef && !invertIsPrint)
                    {
                        chunkTexts.Add(entry.InvertedName);
                        slotMap.Add((entry.Id, 1, invertHash));
                    }
                }

                if (chunkTexts.Count == 0) { continue; }

                var emitted = await EmitLanguageNameChunkAsync(batch, pipeline, chunkTexts, addSignificance: true, ct);

                for (int i = 0; i < emitted.Count; i++)
                {
                    string entryId = slotMap[i].EntryId;
                    if (!codeToAlternateHashes.TryGetValue(entryId, out List<Hash32>? altList))
                    {
                        altList = new List<Hash32>();
                        codeToAlternateHashes[entryId] = altList;
                    }
                    altList.Add(emitted[i].Hash);
                    entityCount++;
                }

                if (batch.EntityCount >= BatchSize)
                {
                    batchNum++;
                    await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "iso-639-3", ct);
                    batch = pipeline.CreateBatch(ProvenanceCode);
                }
            }

            // Submit remaining entities before edge creation.
            if (batch.EntityCount > 0)
            {
                batchNum++;
                await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "iso-639-3", ct);
            }

            Log.EntitiesCreated(Logger, entityCount, batchNum);

            // ── Step 4: entity_language junctions ──
            List<(string Code, byte[] NameHash)> languageNameRows = new(codeToNameHash.Count);
            foreach (KeyValuePair<string, Hash32> kv in codeToNameHash)
            {
                languageNameRows.Add((kv.Key, kv.Value.ToByteArray()));
            }

            await refWriter.WriteLanguageJunctionsAsync(languageNameRows, langIdMap, ct);
            Log.JunctionEntriesWritten(Logger, languageNameRows.Count);

            // ── Step 5: cross-language edges between language_name entities ──
            // The handles here reference entities established in steps 2 and 3,
            // so AddEntity acts as a handle factory (the rows already drain via
            // ON CONFLICT DO NOTHING). Edges are typed relations whose identity
            // is computed server-side at flush; producer-side edge pre-dedupe
            // would need an edge_type_id resolver on the IIngestionPipeline
            // surface that is not yet exposed.

            IIngestionBatch edgeBatch = pipeline.CreateBatch(ProvenanceCode);
            EntityHandle MakeHandle(Hash32 hash) => edgeBatch.AddEntity(hash, "language_name");

            foreach (KeyValuePair<string, List<Hash32>> kv in codeToAlternateHashes)
            {
                if (!codeToNameHash.TryGetValue(kv.Key, out Hash32 refHash))
                {
                    continue;
                }
                EntityHandle refHandle = MakeHandle(refHash);
                foreach (Hash32 altHash in kv.Value)
                {
                    EntityHandle altHandle = MakeHandle(altHash);
                    edgeBatch.AddEdge("has_alternate_name", ProvenanceCode,
                    [
                        new EdgeMemberSpec(refHandle, "source", 0),
                        new EdgeMemberSpec(altHandle, "target", 1),
                    ],
                    ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                    EdgeArenaRouter.EventsFor("has_alternate_name"));
                    edgeCount++;
                }
            }

            foreach (MacrolanguageMapping m in macroMappings)
            {
                if (!codeToNameHash.TryGetValue(m.MacrolanguageId, out Hash32 macroHash))
                {
                    continue;
                }
                if (!codeToNameHash.TryGetValue(m.IndividualId, out Hash32 indivHash))
                {
                    continue;
                }
                edgeBatch.AddEdge("macrolanguage_contains", ProvenanceCode,
                [
                    new EdgeMemberSpec(MakeHandle(macroHash), "source", 0),
                    new EdgeMemberSpec(MakeHandle(indivHash), "target", 1),
                ],
                ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                EdgeArenaRouter.EventsFor("macrolanguage_contains"));
                edgeCount++;
            }

            // ── Step 5b: retirement records ──
            // The retired code's reference name is in the retirement record
            // (not in codeToNameHash, since it's no longer in iso-639-3.tab),
            // so emit it as a language_name entity with the same AP-19 chunked
            // pre-dedupe pattern.
            List<(RetirementRecord R, Hash32 SuccessorHash)> retirementsToEmit = new(retirements.Count);
            List<string> retiredNames = new(retirements.Count);
            foreach (RetirementRecord r in retirements)
            {
                if (string.IsNullOrEmpty(r.ChangeTo)) { continue; }
                if (!codeToNameHash.TryGetValue(r.ChangeTo, out Hash32 successorHash)) { continue; }
                retirementsToEmit.Add((r, successorHash));
                retiredNames.Add(r.RefName);
            }

            for (int chunkStart = 0; chunkStart < retirementsToEmit.Count; chunkStart += PreDedupeChunk)
            {
                int chunkEnd = Math.Min(chunkStart + PreDedupeChunk, retirementsToEmit.Count);
                List<string> chunkTexts = new(chunkEnd - chunkStart);
                for (int i = chunkStart; i < chunkEnd; i++) { chunkTexts.Add(retiredNames[i]); }

                var emitted = await EmitLanguageNameChunkAsync(edgeBatch, pipeline, chunkTexts, addSignificance: true, ct);
                for (int i = 0; i < emitted.Count; i++)
                {
                    EntityHandle retiredHandle = emitted[i].Handle;
                    Hash32 successorHash = retirementsToEmit[chunkStart + i].SuccessorHash;
                    entityCount++;
                    edgeBatch.AddEdge("superseded_by", ProvenanceCode,
                    [
                        new EdgeMemberSpec(retiredHandle, "source", 0),
                        new EdgeMemberSpec(MakeHandle(successorHash), "target", 1),
                    ],
                    ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                    EdgeArenaRouter.EventsFor("superseded_by"));
                    edgeCount++;
                }
            }

            if (edgeBatch.EntityCount > 0 || edgeBatch.EdgeCount > 0)
            {
                batchNum++;
                await ReportProgressAsync(pipeline, reporter, edgeBatch, entityCount, edgeCount, batchNum, "iso-639-3", ct);
            }

            // ── Step 6: ISO 639-2 cross-source corroboration ──
            // LoC's ISO-639-2_utf-8.txt brings alpha-3-bibliographic + alpha-3-
            // terminologic + alpha-2 + English/French names. Cross-source
            // attestation accumulates on existing language_name entities.
            string iso6392Path = Path.Combine(_sourceDir, "ISO-639-2_utf-8.txt");
            if (File.Exists(iso6392Path))
            {
                List<Iso6392Record> iso6392Records = Iso639Parser.ParseIso639_2(iso6392Path);
                IIngestionBatch iso6392Batch = pipeline.CreateBatch("library_of_congress");
                long iso6392Edges = 0;

                // Filter to rows whose alpha3-bibliographic is recognized.
                List<Iso6392Record> recognized = new(iso6392Records.Count);
                foreach (Iso6392Record r in iso6392Records)
                {
                    if (codeToNameHash.ContainsKey(r.Alpha3Bibliographic))
                    {
                        recognized.Add(r);
                    }
                }

                for (int chunkStart = 0; chunkStart < recognized.Count; chunkStart += PreDedupeChunk)
                {
                    int chunkEnd = Math.Min(chunkStart + PreDedupeChunk, recognized.Count);

                    // Collect (entryIndex, slot, text) tuples so we can map the
                    // post-emit handles back to source records.
                    List<string> chunkTexts = new(2 * (chunkEnd - chunkStart));
                    List<int> chunkRecIndex = new(2 * (chunkEnd - chunkStart));
                    List<int> chunkSlot = new(2 * (chunkEnd - chunkStart)); // 0 = english, 1 = french

                    for (int i = chunkStart; i < chunkEnd; i++)
                    {
                        Iso6392Record r = recognized[i];
                        if (r.EnglishName.Length > 0) { chunkTexts.Add(r.EnglishName); chunkRecIndex.Add(i); chunkSlot.Add(0); }
                        if (r.FrenchName.Length > 0)  { chunkTexts.Add(r.FrenchName);  chunkRecIndex.Add(i); chunkSlot.Add(1); }
                    }

                    if (chunkTexts.Count == 0) { continue; }

                    // AP-19 chunked pre-dedupe also for this provenance.
                    List<Hash32> hashes = new(chunkTexts.Count);
                    foreach (string t in chunkTexts) { hashes.Add(ComputeWordFormHash(t)); }
                    HashSet<HashKey> existing = await pipeline.GetExistingEntityHashesAsync(hashes, ct);

                    for (int i = 0; i < chunkTexts.Count; i++)
                    {
                        Iso6392Record r = recognized[chunkRecIndex[i]];
                        Hash32 anchorHash = codeToNameHash[r.Alpha3Bibliographic];
                        Hash32 thisHash = hashes[i];

                        EntityHandle anchorHandle = iso6392Batch.AddEntity(anchorHash, "language_name");

                        EntityHandle nameHandle;
                        if (existing.Contains(new HashKey(thisHash)))
                        {
                            nameHandle = new EntityHandle(thisHash, "language_name");
                        }
                        else
                        {
                            (EntityHandle h, _, _) = EmitText(iso6392Batch, chunkTexts[i], _codepointProperties, "language_name", TrustPriorMu);
                            nameHandle = h;
                            existing.Add(new HashKey(thisHash));
                        }

                        if (!thisHash.Equals(anchorHash))
                        {
                            iso6392Batch.AddEdge("has_alternate_name", "library_of_congress",
                            [
                                new EdgeMemberSpec(anchorHandle, "source", 0),
                                new EdgeMemberSpec(nameHandle, "target", 1),
                            ],
                            ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                            EdgeArenaRouter.EventsFor("has_alternate_name"));
                            iso6392Edges++;
                        }
                    }

                    if (iso6392Batch.EntityCount >= 1000 || iso6392Batch.EdgeCount >= 1000)
                    {
                        batchNum++;
                        await ReportProgressAsync(pipeline, reporter, iso6392Batch, entityCount, edgeCount, batchNum, "iso-639-2", ct);
                        iso6392Batch = pipeline.CreateBatch("library_of_congress");
                    }
                }

                if (iso6392Batch.EntityCount > 0 || iso6392Batch.EdgeCount > 0)
                {
                    batchNum++;
                    await ReportProgressAsync(pipeline, reporter, iso6392Batch, entityCount, edgeCount, batchNum, "iso-639-2", ct);
                }
                edgeCount += iso6392Edges;
                Log.Iso6392Complete(Logger, iso6392Records.Count, iso6392Edges);
            }

            Log.DecompositionComplete(Logger, entityCount, edgeCount, languageNameRows.Count);
        }
        finally
        {
            await refWriter.DisposeAsync();
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Parsing ISO 639-3 data files")]
        public static partial void Parsing(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Parsed: {Languages} languages, {Macros} macrolanguage mappings, {Names} name index entries, {Retirements} retirements")]
        public static partial void Parsed(ILogger logger, int languages, int macros, int names, int retirements);

        [LoggerMessage(Level = LogLevel.Information, Message = "Language reference table populated: {Count} rows")]
        public static partial void ReferenceTablePopulated(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Entities created: {Count} in {Batches} batches")]
        public static partial void EntitiesCreated(ILogger logger, long count, int batches);

        [LoggerMessage(Level = LogLevel.Information, Message = "entity_language junction: {Count} entries")]
        public static partial void JunctionEntriesWritten(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "ISO 639-3 complete: {Entities} entities, {Edges} edges, {Languages} language references")]
        public static partial void DecompositionComplete(ILogger logger, long entities, long edges, int languages);

        [LoggerMessage(Level = LogLevel.Information, Message = "ISO 639-2 cross-source: {Records} LoC records, {Edges} has_alternate_name edges (English + French) corroborating ISO 639-3 entities")]
        public static partial void Iso6392Complete(ILogger logger, int records, long edges);
    }
}
