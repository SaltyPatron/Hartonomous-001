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

public sealed partial class Iso639Decomposer : BaseDecomposer
{
    public override string ProvenanceCode => "sil_international";
    public override string DisplayName => "ISO 639-3 Decomposer (SIL International)";
    public override IReadOnlyList<Phase> Phases => [Phase.Iso639];

    private const double TrustPriorMu = 95000.0;

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

            // ── Step 2: Create language_name entities with codepoint composition ──
            // Each reference name decomposes into constituent codepoints via sequence
            // entries. Codepoint entities already exist from UCD — re-adding them to
            // the batch causes a no-op upsert that returns existing IDs for linking.
            long entityCount = 0;
            long edgeCount = 0;
            int batchNum = 0;

            // Track code → nameHash for junction rows and edge creation.
            Dictionary<string, Hash32> codeToNameHash = new(languages.Count, StringComparer.Ordinal);
            IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);

            foreach (Iso639Record rec in languages)
            {
                ct.ThrowIfCancellationRequested();

                // language_name identity = native text root hash over the same text DAG.
                // The shared text decomposer creates codepoint + grapheme_cluster +
                // language_name entities and composition metadata in one pass; same content from
                // any decomposer yields the same entity row with language_name classification.
                (EntityHandle nameEntity, Hash32 nameHash, _) =
                    EmitText(batch, rec.RefName, _codepointProperties, "language_name", TrustPriorMu);
                codeToNameHash[rec.Id] = nameHash;

                batch.AddSignificance(nameEntity, "source_authority", TrustPriorMu);
                entityCount++;

                if (batch.EntityCount >= BatchSize)
                {
                    batchNum++;
                    await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "iso-639-3", ct);
                    batch = pipeline.CreateBatch(ProvenanceCode);
                }
            }

            // ── Step 3: Alternative names from Name_Index ──
            // Names that differ from the reference name are additional language_name
            // entities linked via has_alternate_name edges.
            Dictionary<string, List<Hash32>> codeToAlternateHashes = new(StringComparer.Ordinal);

            foreach (NameIndexEntry entry in nameIndex)
            {
                ct.ThrowIfCancellationRequested();

                if (!codeToNameHash.TryGetValue(entry.Id, out Hash32 refHash))
                {
                    continue;
                }

                // Canonical Merkle hash for both alternates — same content as the
                // reference name yields the same hash → no duplicate row.
                Hash32 printHash = ComputeWordFormHash(entry.PrintName);
                Hash32 invertHash = ComputeWordFormHash(entry.InvertedName);

                bool printIsRef = printHash.Equals(refHash);
                bool invertIsRef = invertHash.Equals(refHash);
                bool invertIsPrint = invertHash.Equals(printHash);

                if (!codeToAlternateHashes.TryGetValue(entry.Id, out List<Hash32>? altList))
                {
                    altList = [];
                    codeToAlternateHashes[entry.Id] = altList;
                }

                if (!printIsRef)
                {
                    (EntityHandle altEntity, Hash32 hash, _) =
                        EmitText(batch, entry.PrintName, _codepointProperties, "language_name", TrustPriorMu);
                    batch.AddSignificance(altEntity, "source_authority", TrustPriorMu);
                    altList.Add(hash);
                    entityCount++;
                }

                if (!invertIsRef && !invertIsPrint)
                {
                    (EntityHandle altEntity, Hash32 hash, _) =
                        EmitText(batch, entry.InvertedName, _codepointProperties, "language_name", TrustPriorMu);
                    batch.AddSignificance(altEntity, "source_authority", TrustPriorMu);
                    altList.Add(hash);
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
            // The substrate.language reference table has no back-pointer to
            // name entities. Names are substrate content; language membership
            // is evidence on entity_language keyed by entity hash.
            List<(string Code, byte[] NameHash)> languageNameRows = new(codeToNameHash.Count);
            foreach (KeyValuePair<string, Hash32> kv in codeToNameHash)
            {
                languageNameRows.Add((kv.Key, kv.Value.ToByteArray()));
            }

            await refWriter.WriteLanguageJunctionsAsync(languageNameRows, langIdMap, ct);
            Log.JunctionEntriesWritten(Logger, languageNameRows.Count);

            // ── Step 5: cross-language edges between language_name entities ──
            // Three relations from the ISO 639-3 source data that the substrate
            // surfaces as traversable edges:
            //   has_alternate_name        — alt names from Name_Index
            //   member_of_macrolanguage   — Norwegian → Bokmål, Chinese → Mandarin
            //   superseded_by             — fri → fry, auv → oci
            //
            // All three live between language_name entities — name strings ARE
            // entities in the substrate, and these are typed relations between
            // them. The reference-table junction (entity_language) already
            // records "this name belongs to language code X"; these edges
            // record relations between the names themselves so the engine can
            // walk them.

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

            foreach (RetirementRecord r in retirements)
            {
                string? changeTo = r.ChangeTo;
                if (string.IsNullOrEmpty(changeTo))
                {
                    continue;
                }
                if (!codeToNameHash.TryGetValue(changeTo, out Hash32 successorHash))
                {
                    continue;
                }
                // The retired code's reference name is in the retirement record,
                // not in codeToNameHash (since the retired code is no longer in
                // iso-639-3.tab). Emit it as a fresh language_name entity so the
                // edge has a source.
                (EntityHandle retiredHandle, Hash32 retiredHash, _) =
                    EmitText(edgeBatch, r.RefName, _codepointProperties, "language_name", TrustPriorMu);
                edgeBatch.AddSignificance(retiredHandle, "source_authority", TrustPriorMu);
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

            if (edgeBatch.EntityCount > 0 || edgeBatch.EdgeCount > 0)
            {
                batchNum++;
                await ReportProgressAsync(pipeline, reporter, edgeBatch, entityCount, edgeCount, batchNum, "iso-639-3", ct);
            }

            // ── Step 6: ISO 639-2 cross-source corroboration (P4 partial) ──
            // The LoC-published ISO-639-2_utf-8.txt brings alpha-3-bibliographic
            // + alpha-3-terminologic + alpha-2 + English/French names. Same
            // alpha-3 codes that ISO 639-3 attests; cross-source corroboration
            // accumulates on the existing language_name entities. The substrate
            // doesn't create new entities for codes already attested by 639-3;
            // it fires additional has_alternate_name edges from the existing
            // refHash to French name variants + alpha-3-terminologic name
            // variants when those differ from the bibliographic name.
            string iso6392Path = Path.Combine(_sourceDir, "ISO-639-2_utf-8.txt");
            if (File.Exists(iso6392Path))
            {
                List<Iso6392Record> iso6392Records = Iso639Parser.ParseIso639_2(iso6392Path);
                IIngestionBatch iso6392Batch = pipeline.CreateBatch("library_of_congress");
                long iso6392Edges = 0;
                foreach (Iso6392Record r in iso6392Records)
                {
                    // Anchor to the existing language_name entity for the
                    // bibliographic alpha-3 code (P4: cross-source on shared
                    // identity). ISO 639-3 + ISO 639-2 both attest to the same
                    // code → same content-hash → same entity. The LoC
                    // attestations layer onto the 639-3 entity via library_of_congress
                    // provenance, distinct from sil_international provenance.
                    if (!codeToNameHash.TryGetValue(r.Alpha3Bibliographic, out Hash32 anchorHash))
                    {
                        continue; // 639-2 has codes 639-3 doesn't recognize; skip those
                    }
                    EntityHandle anchorHandle = iso6392Batch.AddEntity(anchorHash, "language_name");

                    // English name attestation from LoC. If it differs from the
                    // 639-3 ref_name, fire has_alternate_name edge.
                    if (r.EnglishName.Length > 0)
                    {
                        (EntityHandle engHandle, _, _) = EmitText(iso6392Batch, r.EnglishName,
                            _codepointProperties, "language_name", TrustPriorMu);
                        if (!engHandle.Hash.Equals(anchorHash))
                        {
                            iso6392Batch.AddEdge("has_alternate_name", "library_of_congress",
                            [
                                new EdgeMemberSpec(anchorHandle, "source", 0),
                                new EdgeMemberSpec(engHandle, "target", 1),
                            ],
                            ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                            EdgeArenaRouter.EventsFor("has_alternate_name"));
                            iso6392Edges++;
                        }
                    }

                    // French name as alternate (cross-lingual corroboration).
                    if (r.FrenchName.Length > 0)
                    {
                        (EntityHandle frHandle, _, _) = EmitText(iso6392Batch, r.FrenchName,
                            _codepointProperties, "language_name", TrustPriorMu);
                        iso6392Batch.AddEdge("has_alternate_name", "library_of_congress",
                        [
                            new EdgeMemberSpec(anchorHandle, "source", 0),
                            new EdgeMemberSpec(frHandle, "target", 1),
                        ],
                        ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                        EdgeArenaRouter.EventsFor("has_alternate_name"));
                        iso6392Edges++;
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
