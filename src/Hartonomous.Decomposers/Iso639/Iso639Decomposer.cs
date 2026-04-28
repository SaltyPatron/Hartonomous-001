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
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Iso639;

public sealed partial class Iso639Decomposer : BaseDecomposer
{
    public override string ProvenanceCode => "sil_international";
    public override string DisplayName => "ISO 639-3 Decomposer (SIL International)";
    public override IReadOnlyList<Phase> Phases => [Phase.Iso639];

    private const double TrustPriorMu = 95000.0;

    private readonly string _sourceDir;
    private readonly IReferenceDataReader? _referenceDataReader;
    private readonly IJunctionWriter? _junctionWriter;
    private readonly IReferenceDataWriter? _referenceDataWriter;

    public Iso639Decomposer(
        DecomposerConfig config,
        ILogger<Iso639Decomposer> logger,
        IReferenceDataReader? referenceDataReader = null,
        IJunctionWriter? junctionWriter = null,
        IReferenceDataWriter? referenceDataWriter = null)
        : base(config, logger)
    {
        _sourceDir = config.SourceDirectory;
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

            // Track code → nameHash for FK updates and edge creation.
            Dictionary<string, byte[]> codeToNameHash = new(languages.Count, StringComparer.Ordinal);
            IIngestionBatch batch = pipeline.CreateBatch();

            foreach (Iso639Record rec in languages)
            {
                ct.ThrowIfCancellationRequested();

                // language_name identity = canonical Merkle of codepoint children.
                // EmitWordFormMerkle creates codepoint + grapheme_cluster + language_name
                // entities and sequence rows in one pass; same content from any decomposer
                // (e.g. text_composition "English" from TextDecomposer) yields the same
                // Merkle hash → same entity row in the language_name partition.
                (EntityHandle nameEntity, byte[] nameHash, _) =
                    EmitWordFormMerkle(batch, rec.RefName, "language_name");
                codeToNameHash[rec.Id] = nameHash;

                batch.AddSignificance(nameEntity, "source_authority", TrustPriorMu);
                entityCount++;

                if (batch.EntityCount >= BatchSize)
                {
                    batchNum++;
                    await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "iso-639-3", ct);
                    batch = pipeline.CreateBatch();
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
                        EmitWordFormMerkle(batch, entry.PrintName, "language_name");
                    batch.AddSignificance(altEntity, "source_authority", TrustPriorMu);
                    altList.Add(hash);
                    entityCount++;
                }

                if (!invertIsRef && !invertIsPrint)
                {
                    (EntityHandle altEntity, byte[] hash, _) =
                        EmitWordFormMerkle(batch, entry.InvertedName, "language_name");
                    batch.AddSignificance(altEntity, "source_authority", TrustPriorMu);
                    altList.Add(hash);
                    entityCount++;
                }

                if (batch.EntityCount >= BatchSize)
                {
                    batchNum++;
                    await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "iso-639-3", ct);
                    batch = pipeline.CreateBatch();
                }
            }

            // Submit remaining entities before edge creation.
            if (batch.EntityCount > 0)
            {
                batchNum++;
                await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "iso-639-3", ct);
            }

            Log.EntitiesCreated(Logger, entityCount, batchNum);

            // ── Step 4: language.name_entity_(type_id, hash) FK + entity_language junctions
            // No phase-wide resolve — codeToNameHash already carries every name hash, and in
            // the hash-as-PK substrate the (entity_type_id, hash) pair IS the FK that
            // language.name references. The reference writer takes (code, hash) pairs and
            // updates the row directly.
            List<(string Code, byte[] NameHash)> fkUpdates = new(codeToNameHash.Count);
            foreach (KeyValuePair<string, byte[]> kv in codeToNameHash)
            {
                fkUpdates.Add((kv.Key, kv.Value));
            }

            await refWriter.UpdateNameEntityIdsAsync(fkUpdates, ct);
            Log.NameEntityIdsUpdated(Logger, fkUpdates.Count);

            // ── Step 6: entity_language junctions ──
            await refWriter.WriteLanguageJunctionsAsync(fkUpdates, langIdMap, ct);
            Log.JunctionEntriesWritten(Logger, fkUpdates.Count);

            // Step 7 cross-lingual edges (macrolanguage_contains / has_alternate_name /
            // superseded_by) are intentionally NOT emitted here. Those relationships are
            // metadata between LANGUAGE CODES (rows in substrate.language reference
            // table), not between NAME ENTITIES (rows in substrate.entity). The correct
            // model — once the reference-layer junction tables for macrolanguage and
            // supersession land (task #59) — populates substrate.language_macrolanguage
            // and substrate.language_supersession instead. Emitting them as substrate.edge
            // rows between language_name entities is the parent-child antipattern the
            // greenfield rebuild explicitly removed. Cross-name link information that IS
            // appropriate here (e.g. "this name string is one display form of language
            // code X") is already captured by entity_language above — multiple distinct
            // name entities for the same code naturally produce multiple junction rows.
            _ = macroMappings; _ = codeToAlternateHashes; _ = retirements;

            Log.DecompositionComplete(Logger, entityCount, edgeCount, fkUpdates.Count);
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

        [LoggerMessage(Level = LogLevel.Information, Message = "Name entity IDs updated on {Count} language rows")]
        public static partial void NameEntityIdsUpdated(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "entity_language junction: {Count} entries")]
        public static partial void JunctionEntriesWritten(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "ISO 639-3 complete: {Entities} entities, {Edges} edges, {Languages} language references")]
        public static partial void DecompositionComplete(ILogger logger, long entities, long edges, int languages);
    }
}
