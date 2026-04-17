using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core;
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
    private readonly string _connectionString;

    public Iso639Decomposer(DecomposerConfig config, ILogger<Iso639Decomposer> logger)
        : base(config, logger)
    {
        _sourceDir = config.SourceDirectory;
        _connectionString = config.ConnectionString;
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

        Iso639ReferenceTableWriter refWriter = new(_connectionString);
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

                byte[] nameHash = ComputeHash(rec.RefName);
                codeToNameHash[rec.Id] = nameHash;
                EntityHandle nameEntity = batch.AddEntity(nameHash, "language_name");

                batch.AddSignificance(nameEntity, "source_authority", TrustPriorMu);

                int position = 0;
                foreach (Rune rune in rec.RefName.EnumerateRunes())
                {
                    byte[] cpHash = HashCodepoint(rune.Value);
                    EntityHandle cpHandle = batch.AddEntity(cpHash, "codepoint");
                    batch.AddSequence(nameEntity, cpHandle, position, 1);
                    position++;
                }

                EmitNamePhysicality(batch, nameEntity, rec.RefName);
                entityCount++;

                if (batch.EntityCount >= BatchSize)
                {
                    batchNum++;
                    await SubmitBatchAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, ct);
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

                byte[] printHash = ComputeHash(entry.PrintName);
                byte[] invertHash = ComputeHash(entry.InvertedName);

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
                    EntityHandle altEntity = batch.AddEntity(printHash, "language_name");
                    batch.AddSignificance(altEntity, "source_authority", TrustPriorMu);
                    altList.Add(printHash);

                    int pos = 0;
                    foreach (Rune rune in entry.PrintName.EnumerateRunes())
                    {
                        batch.AddSequence(altEntity, batch.AddEntity(HashCodepoint(rune.Value), "codepoint"), pos, 1);
                        pos++;
                    }

                    EmitNamePhysicality(batch, altEntity, entry.PrintName);
                    entityCount++;
                }

                if (!invertIsRef && !invertIsPrint)
                {
                    EntityHandle altEntity = batch.AddEntity(invertHash, "language_name");
                    batch.AddSignificance(altEntity, "source_authority", TrustPriorMu);
                    altList.Add(invertHash);

                    int pos = 0;
                    foreach (Rune rune in entry.InvertedName.EnumerateRunes())
                    {
                        batch.AddSequence(altEntity, batch.AddEntity(HashCodepoint(rune.Value), "codepoint"), pos, 1);
                        pos++;
                    }

                    EmitNamePhysicality(batch, altEntity, entry.InvertedName);
                    entityCount++;
                }

                if (batch.EntityCount >= BatchSize)
                {
                    batchNum++;
                    await SubmitBatchAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, ct);
                    batch = pipeline.CreateBatch();
                }
            }

            // Submit remaining entities before edge creation.
            if (batch.EntityCount > 0)
            {
                batchNum++;
                await SubmitBatchAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, ct);
            }

            Log.EntitiesCreated(Logger, entityCount, batchNum);

            // ── Step 4: Resolve all name entity IDs ──
            HashSet<byte[]> allHashes = new(ByteArrayEqualityComparer.Instance);
            foreach (byte[] h in codeToNameHash.Values)
            {
                allHashes.Add(h);
            }
            foreach (List<byte[]> alts in codeToAlternateHashes.Values)
            {
                foreach (byte[] h in alts)
                {
                    allHashes.Add(h);
                }
            }

            IReadOnlyDictionary<byte[], long> entityIdMap =
                await pipeline.ResolveEntityIdsAsync([.. allHashes], ct);

            // ── Step 5: Update language.name_entity_id FK ──
            List<(string Code, long EntityId)> fkUpdates = new(codeToNameHash.Count);
            foreach (KeyValuePair<string, byte[]> kv in codeToNameHash)
            {
                if (entityIdMap.TryGetValue(kv.Value, out long entityId))
                {
                    fkUpdates.Add((kv.Key, entityId));
                }
            }

            await refWriter.UpdateNameEntityIdsAsync(fkUpdates, ct);
            Log.NameEntityIdsUpdated(Logger, fkUpdates.Count);

            // ── Step 6: entity_language junctions ──
            await refWriter.WriteLanguageJunctionsAsync(fkUpdates, langIdMap, ct);
            Log.JunctionEntriesWritten(Logger, fkUpdates.Count);

            // ── Step 7: Edges — macrolanguage containment, alternate names, retirements ──
            batch = pipeline.CreateBatch();

            // Macrolanguage containment: macrolanguage_contains edges.
            foreach (MacrolanguageMapping mapping in macroMappings)
            {
                ct.ThrowIfCancellationRequested();

                if (!codeToNameHash.TryGetValue(mapping.MacrolanguageId, out byte[]? macroHash) ||
                    !codeToNameHash.TryGetValue(mapping.IndividualId, out byte[]? indivHash))
                {
                    continue;
                }

                if (!entityIdMap.TryGetValue(macroHash, out long macroEntityId) ||
                    !entityIdMap.TryGetValue(indivHash, out long indivEntityId))
                {
                    continue;
                }

                batch.AddEdge("macrolanguage_contains", ProvenanceCode,
                [
                    new EdgeMemberSpec(null, macroEntityId, "source", 0),
                    new EdgeMemberSpec(null, indivEntityId, "target", 1),
                ]);
                edgeCount++;

                if (batch.EdgeCount >= BatchSize)
                {
                    batchNum++;
                    await SubmitBatchAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, ct);
                    batch = pipeline.CreateBatch();
                }
            }

            // Alternate name edges: has_alternate_name from ref name to each alternate.
            foreach (KeyValuePair<string, List<byte[]>> kv in codeToAlternateHashes)
            {
                if (!codeToNameHash.TryGetValue(kv.Key, out byte[]? refHash) ||
                    !entityIdMap.TryGetValue(refHash, out long refEntityId))
                {
                    continue;
                }

                foreach (byte[] altHash in kv.Value)
                {
                    if (!entityIdMap.TryGetValue(altHash, out long altEntityId))
                    {
                        continue;
                    }

                    batch.AddEdge("has_alternate_name", ProvenanceCode,
                    [
                        new EdgeMemberSpec(null, refEntityId, "source", 0),
                        new EdgeMemberSpec(null, altEntityId, "target", 1),
                    ]);
                    edgeCount++;
                }

                if (batch.EdgeCount >= BatchSize)
                {
                    batchNum++;
                    await SubmitBatchAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, ct);
                    batch = pipeline.CreateBatch();
                }
            }

            // Retirement edges: superseded_by from retired code to replacement.
            foreach (RetirementRecord ret in retirements)
            {
                if (ret.ChangeTo is null)
                {
                    continue;
                }

                if (!codeToNameHash.TryGetValue(ret.Id, out byte[]? retiredHash))
                {
                    // Retired code may not be in the main language list — create entity.
                    retiredHash = ComputeHash(ret.RefName);
                }

                if (!codeToNameHash.TryGetValue(ret.ChangeTo, out byte[]? replacementHash))
                {
                    continue;
                }

                if (!entityIdMap.TryGetValue(retiredHash, out long retiredId) ||
                    !entityIdMap.TryGetValue(replacementHash, out long replacementId))
                {
                    continue;
                }

                batch.AddEdge("superseded_by", ProvenanceCode,
                [
                    new EdgeMemberSpec(null, retiredId, "source", 0),
                    new EdgeMemberSpec(null, replacementId, "target", 1),
                ]);
                edgeCount++;
            }

            if (batch.EdgeCount > 0 || batch.EntityCount > 0)
            {
                batchNum++;
                await SubmitBatchAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, ct);
            }

            Log.DecompositionComplete(Logger, entityCount, edgeCount, fkUpdates.Count);
        }
        finally
        {
            await refWriter.DisposeAsync();
        }
    }

    private async Task SubmitBatchAsync(
        IIngestionPipeline pipeline, IProgressReporter reporter,
        IIngestionBatch batch, long entityCount, long edgeCount, int batchNum,
        CancellationToken ct)
    {
        await SubmitAndReportAsync(pipeline, reporter, batch,
            new ProgressSnapshot
            {
                DecomposerCode = ProvenanceCode,
                CurrentPhase = "ingestion",
                EntitiesCreated = entityCount,
                EdgesCreated = edgeCount,
                CurrentFile = "iso-639-3",
                CurrentBatch = batchNum,
            }, ct);
    }

    private static void EmitNamePhysicality(IIngestionBatch batch, EntityHandle entity, string surfaceForm)
    {
        List<(double X, double Y, double Z, double M)> vertices =
            PhysicalityEmitter.SurfaceFormVertices(surfaceForm);
        if (vertices.Count >= 2)
        {
            batch.AddPhysicality(entity, "contour", PhysicalityEmitter.LineStringZmWkb(vertices));
        }
        else if (vertices.Count == 1)
        {
            (double x, double y, double z, double m) = vertices[0];
            batch.AddPhysicality(entity, "s3_position", PhysicalityEmitter.PointZmWkb(x, y, z, m));
        }
    }

    internal static byte[] HashCodepoint(int cpValue)
    {
        byte[] cpBytes = new byte[4];
        cpBytes[0] = (byte)(cpValue >> 24);
        cpBytes[1] = (byte)(cpValue >> 16);
        cpBytes[2] = (byte)(cpValue >> 8);
        cpBytes[3] = (byte)cpValue;
        return ComputeHash(cpBytes.AsSpan());
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
