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
            Dictionary<string, byte[]> codeToNameHash = new(languages.Count, StringComparer.Ordinal);
            IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);

            foreach (Iso639Record rec in languages)
            {
                ct.ThrowIfCancellationRequested();

                // language_name identity = native text root hash over the same text DAG.
                // The shared text decomposer creates codepoint + grapheme_cluster +
                // language_name entities and sequence rows in one pass; same content from
                // any decomposer yields the same entity row with language_name classification.
                (EntityHandle nameEntity, byte[] nameHash, _) =
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
            Dictionary<string, List<byte[]>> codeToAlternateHashes = new(StringComparer.Ordinal);

            foreach (NameIndexEntry entry in nameIndex)
            {
                ct.ThrowIfCancellationRequested();

                if (!codeToNameHash.TryGetValue(entry.Id, out byte[]? refHash))
                {
                    continue;
                }

                // Canonical Merkle hash for both alternates — same content as the
                // reference name yields the same hash → no duplicate row.
                byte[] printHash = ComputeWordFormHash(entry.PrintName);
                byte[] invertHash = ComputeWordFormHash(entry.InvertedName);

                bool printIsRef = SequenceEqual(printHash, refHash);
                bool invertIsRef = SequenceEqual(invertHash, refHash);
                bool invertIsPrint = SequenceEqual(invertHash, printHash);

                if (!codeToAlternateHashes.TryGetValue(entry.Id, out List<byte[]>? altList))
                {
                    altList = [];
                    codeToAlternateHashes[entry.Id] = altList;
                }

                if (!printIsRef)
                {
                    (EntityHandle altEntity, byte[] hash, _) =
                        EmitText(batch, entry.PrintName, _codepointProperties, "language_name", TrustPriorMu);
                    batch.AddSignificance(altEntity, "source_authority", TrustPriorMu);
                    altList.Add(hash);
                    entityCount++;
                }

                if (!invertIsRef && !invertIsPrint)
                {
                    (EntityHandle altEntity, byte[] hash, _) =
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
            foreach (KeyValuePair<string, byte[]> kv in codeToNameHash)
            {
                languageNameRows.Add((kv.Key, kv.Value));
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
            EntityHandle MakeHandle(byte[] hash) => edgeBatch.AddEntity(hash, "language_name");

            foreach (KeyValuePair<string, List<byte[]>> kv in codeToAlternateHashes)
            {
                if (!codeToNameHash.TryGetValue(kv.Key, out byte[]? refHash))
                {
                    continue;
                }
                EntityHandle refHandle = MakeHandle(refHash);
                foreach (byte[] altHash in kv.Value)
                {
                    EntityHandle altHandle = MakeHandle(altHash);
                    edgeBatch.AddEdge("has_alternate_name", ProvenanceCode,
                    [
                        new EdgeMemberSpec(refHandle, "source", 0),
                        new EdgeMemberSpec(altHandle, "target", 1),
                    ]);
                    edgeCount++;
                }
            }

            foreach (MacrolanguageMapping m in macroMappings)
            {
                if (!codeToNameHash.TryGetValue(m.MacrolanguageId, out byte[]? macroHash))
                {
                    continue;
                }
                if (!codeToNameHash.TryGetValue(m.IndividualId, out byte[]? indivHash))
                {
                    continue;
                }
                edgeBatch.AddEdge("macrolanguage_contains", ProvenanceCode,
                [
                    new EdgeMemberSpec(MakeHandle(macroHash), "source", 0),
                    new EdgeMemberSpec(MakeHandle(indivHash), "target", 1),
                ]);
                edgeCount++;
            }

            foreach (RetirementRecord r in retirements)
            {
                string? changeTo = r.ChangeTo;
                if (string.IsNullOrEmpty(changeTo))
                {
                    continue;
                }
                if (!codeToNameHash.TryGetValue(changeTo, out byte[]? successorHash))
                {
                    continue;
                }
                // The retired code's reference name is in the retirement record,
                // not in codeToNameHash (since the retired code is no longer in
                // iso-639-3.tab). Emit it as a fresh language_name entity so the
                // edge has a source.
                (EntityHandle retiredHandle, byte[] retiredHash, _) =
                    EmitText(edgeBatch, r.RefName, _codepointProperties, "language_name", TrustPriorMu);
                edgeBatch.AddSignificance(retiredHandle, "source_authority", TrustPriorMu);
                entityCount++;
                edgeBatch.AddEdge("superseded_by", ProvenanceCode,
                [
                    new EdgeMemberSpec(retiredHandle, "source", 0),
                    new EdgeMemberSpec(MakeHandle(successorHash), "target", 1),
                ]);
                edgeCount++;
            }

            if (edgeBatch.EntityCount > 0 || edgeBatch.EdgeCount > 0)
            {
                batchNum++;
                await ReportProgressAsync(pipeline, reporter, edgeBatch, entityCount, edgeCount, batchNum, "iso-639-3", ct);
            }

            Log.DecompositionComplete(Logger, entityCount, edgeCount, languageNameRows.Count);
        }
        finally
        {
            await refWriter.DisposeAsync();
        }
    }

    private static bool SequenceEqual(byte[] a, byte[] b)
    {
        return a.AsSpan().SequenceEqual(b);
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
    }
}
