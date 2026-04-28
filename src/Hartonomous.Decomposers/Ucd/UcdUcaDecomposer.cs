using Hartonomous.Core;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Data;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Ucd;

public sealed partial class UcdUcaDecomposer : BaseDecomposer
{
    public override string ProvenanceCode => "unicode_consortium";
    public override string DisplayName => "UCD/UCA Decomposer (Unicode 17.0.0)";
    public override IReadOnlyList<Phase> Phases => [Phase.UcdUca];

    private const double TrustPriorMu = 95000.0;

    private readonly string _sourceDir;
    private readonly IReferenceDataReader? _referenceDataReader;
    private readonly IJunctionWriter? _junctionWriter;
    private readonly IReferenceDataWriter? _referenceDataWriter;

    public UcdUcaDecomposer(
        DecomposerConfig config,
        ILogger<UcdUcaDecomposer> logger,
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
        Path.Combine(_sourceDir, "ucdxml", "ucd.all.grouped.xml"),
        Path.Combine(_sourceDir, "uca", "allkeys.txt"),
    ];

    protected override async Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        string xmlPath = Path.Combine(_sourceDir, "ucdxml", "ucd.all.grouped.xml");
        string allkeysPath = Path.Combine(_sourceDir, "uca", "allkeys.txt");

        // ── Phase 1: Parse UCA collation weights → UCA-sorted index for S3 projection ──
        Log.ParsingCollationWeights(Logger);
        Dictionary<int, CollationWeight> collationMap = UcaParser.ParseAllKeys(allkeysPath);

        Dictionary<int, int> ucaSortedCps = collationMap
            .OrderBy(kv => kv.Value, CollationWeightComparer.Instance)
            .Select((kv, i) => (Codepoint: kv.Key, Index: i))
            .ToDictionary(x => x.Codepoint, x => x.Index);
        int totalUcaEntries = ucaSortedCps.Count;
        Log.CollationWeightsParsed(Logger, collationMap.Count);

        // ── Phase 2: First XML pass — collect reference table values ──
        Log.ParsingUcdXml(Logger);
        ReferenceTableCollector refCollector = new();

        // We need two passes: first to collect reference table values (so we can populate
        // reference tables before creating junction entries), second to create entities.
        // The XML parser yields lazily, so we materialize the codepoints.
        List<CodepointRecord> allCodepoints = [];
        foreach (CodepointRecord cp in UcdXmlParser.Parse(xmlPath, refCollector, ct))
        {
            allCodepoints.Add(cp);
        }
        Log.XmlParsed(Logger, allCodepoints.Count, refCollector.GeneralCategories.Count,
            refCollector.Scripts.Count, refCollector.Blocks.Count);

        // ── Phase 3: Populate reference tables ──
        UcdReferenceTableWriter refWriter = new(_referenceDataReader!, _junctionWriter!, _referenceDataWriter!);
        try
        {
            Dictionary<string, int> gcIds = await refWriter.PopulateGeneralCategoriesAsync(
                refCollector.GeneralCategories.Keys, ct);
            Dictionary<string, int> scriptIds = await refWriter.PopulateScriptsAsync(
                refCollector.Scripts.Keys, ct);
            Dictionary<string, int> blockIds = await refWriter.PopulateBlocksAsync(
                refCollector.Blocks, ct);
            Dictionary<(string, string), int> breakIds = await refWriter.PopulateBreakPropertiesAsync(
                refCollector.BreakProperties.Keys, ct);

            Log.ReferenceTablesPopulated(Logger, gcIds.Count, scriptIds.Count,
                blockIds.Count, breakIds.Count);

            await reporter.ReportAsync(new ProgressSnapshot
            {
                DecomposerCode = ProvenanceCode,
                CurrentPhase = "reference_tables",
                EntitiesCreated = 0,
                EdgesCreated = 0,
            }, ct);

            // ── Phase 4: Create codepoint entities, edges, physicality via pipeline ──
            // Track hashes so we can resolve entity IDs for junction entries later.
            Dictionary<int, byte[]> cpHashMap = new(allCodepoints.Count);
            long entityCount = 0;
            long edgeCount = 0;
            int batchNum = 0;

            IIngestionBatch batch = pipeline.CreateBatch();
            HashSet<ulong> ceHashesEmittedPhysicality = [];

            foreach (CodepointRecord cp in allCodepoints)
            {
                ct.ThrowIfCancellationRequested();

                byte[] hash = HashCodepoint(cp.Value);
                cpHashMap[cp.Value] = hash;
                EntityHandle entity = batch.AddEntity(hash, "codepoint");

                batch.AddSignificance(entity, "source_authority", TrustPriorMu);

                if (cp.SimpleLowercase.HasValue && cp.SimpleLowercase.Value != cp.Value)
                {
                    byte[] targetHash = HashCodepoint(cp.SimpleLowercase.Value);
                    EntityHandle target = batch.AddEntity(targetHash, "codepoint");
                    batch.AddEdge("maps_to_lowercase", ProvenanceCode,
                        [new EdgeMemberSpec(entity, "source", 0),
                         new EdgeMemberSpec(target, "target", 1)]);
                    edgeCount++;
                }

                if (cp.SimpleCaseFolding.HasValue && cp.SimpleCaseFolding.Value != cp.Value)
                {
                    byte[] targetHash = HashCodepoint(cp.SimpleCaseFolding.Value);
                    EntityHandle target = batch.AddEntity(targetHash, "codepoint");
                    batch.AddEdge("case_folds_to", ProvenanceCode,
                        [new EdgeMemberSpec(entity, "source", 0),
                         new EdgeMemberSpec(target, "target", 1)]);
                    edgeCount++;
                }

                // Every codepoint gets an S3 position, derived directly from its Unicode scalar
                // value via Super-Fibonacci over the full 0..0x10FFFF code space. Adjacent code
                // points are adjacent on S3 — block/script locality is preserved geometrically.
                {
                    (double x, double y, double z, double m) = PhysicalityEmitter.CodepointS3Position(cp.Value);
                    batch.AddPhysicalityPoint4d(entity, "s3_position", x, y, z, m);
                }

                if (collationMap.TryGetValue(cp.Value, out CollationWeight weights))
                {
                    byte[] ceHash = HashCollationElement(weights);
                    EntityHandle ceEntity = batch.AddEntity(ceHash, "collation_element");
                    batch.AddEdge("has_collation_weight", ProvenanceCode,
                        [new EdgeMemberSpec(entity, "source", 0),
                         new EdgeMemberSpec(ceEntity, "target", 1)]);
                    edgeCount++;

                    // Collation element physicality is emitted once per unique CE hash, not per
                    // codepoint that resolves to it — otherwise thousands of codepoints sharing
                    // a CE would each emit redundant rows. UCA sort index is the CE's geometric
                    // identity. ceHash collapsed to a ulong is a fast in-batch guard; the DB
                    // unique constraint is the cross-batch/run safety net.
                    ulong ceKey = BitConverter.ToUInt64(ceHash, 0);
                    if (ceHashesEmittedPhysicality.Add(ceKey) &&
                        ucaSortedCps.TryGetValue(cp.Value, out int ucaIndex))
                    {
                        (double cx, double cy, double cz, double cm) =
                            PhysicalityEmitter.SuperFibonacciS3(ucaIndex, totalUcaEntries);
                        batch.AddPhysicalityPoint4d(ceEntity, "s3_position", cx, cy, cz, cm);
                    }
                }

                entityCount++;

                if (batch.EntityCount >= BatchSize)
                {
                    batchNum++;
                    await ReportProgressAsync(pipeline, reporter, batch,
                        entityCount, edgeCount, batchNum, "ucd.all.grouped.xml", ct, "entities");
                    batch = pipeline.CreateBatch();
                }
            }

            if (batch.EntityCount > 0)
            {
                batchNum++;
                await ReportProgressAsync(pipeline, reporter, batch,
                    entityCount, edgeCount, batchNum, "ucd.all.grouped.xml", ct, "entities");
            }

            Log.EntitiesCreated(Logger, entityCount, edgeCount, batchNum);

            // ── Phase 5: write codepoint_property junction table ──
            // Hash-as-PK substrate eliminates the resolve step. The codepoint
            // entity_type_id is fixed (1, "codepoint" partition); each
            // codepoint's hash is already in cpHashMap.
            const int CodepointEntityTypeId = 1;

            List<CodepointPropertyRow> propertyRows = new(allCodepoints.Count);
            HashSet<byte[]> seenHashes = new(ByteArrayEqualityComparer.Instance);
            int resolved = 0;
            int unresolved = 0;
            int duplicates = 0;

            foreach (CodepointRecord cp in allCodepoints)
            {
                if (!cpHashMap.TryGetValue(cp.Value, out byte[]? hash))
                {
                    unresolved++;
                    continue;
                }

                if (!seenHashes.Add(hash))
                {
                    duplicates++;
                    continue;
                }

                if (!gcIds.TryGetValue(cp.GeneralCategory, out int gcId))
                {
                    continue;
                }

                if (!scriptIds.TryGetValue(cp.Script, out int scriptId))
                {
                    continue;
                }

                if (!blockIds.TryGetValue(cp.Block, out int blockId))
                {
                    // Use a fallback block ID if the block wasn't in the map.
                    if (!blockIds.TryGetValue("NB", out blockId))
                    {
                        continue;
                    }
                }

                int? gcbId = ResolveBreakProperty(breakIds, cp.GraphemeClusterBreak, "GCB");
                int? wbId = ResolveBreakProperty(breakIds, cp.WordBreak, "WB");
                int? sbId = ResolveBreakProperty(breakIds, cp.SentenceBreak, "SB");
                int? lbId = ResolveBreakProperty(breakIds, cp.LineBreak, "LB");

                int[]? fullFold = cp.FullCaseFolding;
                // Treat identity folds as no-fold so the column stays NULL.
                if (fullFold is { Length: 1 } && fullFold[0] == cp.Value)
                {
                    fullFold = null;
                }
                int? simpleFold = cp.SimpleCaseFolding;
                if (simpleFold.HasValue && simpleFold.Value == cp.Value)
                {
                    simpleFold = null;
                }
                int[]? decompMap = cp.DecompositionMapping;
                if (decompMap is { Length: 1 } && decompMap[0] == cp.Value)
                {
                    decompMap = null;
                }

                propertyRows.Add(new CodepointPropertyRow(
                    CodepointEntityTypeId, hash, cp.Value, gcId, scriptId, blockId, gcbId, wbId, sbId, lbId,
                    cp.IsExtendedPictographic,
                    (short)cp.CanonicalCombiningClass,
                    cp.DecompositionType,
                    decompMap,
                    simpleFold,
                    fullFold));
                resolved++;
            }

            // Write in batches to avoid massive parameter arrays.
            int cpBatchSize = 50_000;
            for (int i = 0; i < propertyRows.Count; i += cpBatchSize)
            {
                int count = Math.Min(cpBatchSize, propertyRows.Count - i);
                await refWriter.WriteCodepointPropertiesAsync(
                    propertyRows.GetRange(i, count), ct);

                await reporter.ReportAsync(new ProgressSnapshot
                {
                    DecomposerCode = ProvenanceCode,
                    CurrentPhase = "junction_tables",
                    EntitiesCreated = entityCount,
                    EdgesCreated = edgeCount,
                    CurrentBatch = i / cpBatchSize + 1,
                }, ct);
            }

            Log.JunctionTablesWritten(Logger, resolved, unresolved, duplicates);
        }
        finally
        {
            await refWriter.DisposeAsync();
        }

        Log.DecompositionComplete(Logger, pipeline.Stats.EntitiesSubmitted, pipeline.Stats.EdgesSubmitted);
    }

    private static int? ResolveBreakProperty(
        Dictionary<(string, string), int> breakIds, string? code, string category)
    {
        if (code == null)
        {
            return null;
        }

        return breakIds.TryGetValue((code, category), out int id) ? id : null;
    }


    private static byte[] HashCollationElement(CollationWeight weights)
    {
        byte[] ceBytes = new byte[6];
        ceBytes[0] = (byte)(weights.Primary >> 8);
        ceBytes[1] = (byte)weights.Primary;
        ceBytes[2] = (byte)(weights.Secondary >> 8);
        ceBytes[3] = (byte)weights.Secondary;
        ceBytes[4] = (byte)(weights.Tertiary >> 8);
        ceBytes[5] = (byte)weights.Tertiary;
        return ComputeHash(ceBytes.AsSpan());
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Parsing UCA collation weights (allkeys.txt)")]
        public static partial void ParsingCollationWeights(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Parsed {Count} collation weight entries")]
        public static partial void CollationWeightsParsed(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Parsing UCD XML (ucd.all.grouped.xml)")]
        public static partial void ParsingUcdXml(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "XML parsed: {Codepoints} codepoints, {Gc} general_categories, {Scripts} scripts, {Blocks} blocks")]
        public static partial void XmlParsed(ILogger logger, int codepoints, int gc, int scripts, int blocks);

        [LoggerMessage(Level = LogLevel.Information, Message = "Reference tables populated: {Gc} gc, {Scripts} scripts, {Blocks} blocks, {Breaks} break_properties")]
        public static partial void ReferenceTablesPopulated(ILogger logger, int gc, int scripts, int blocks, int breaks);

        [LoggerMessage(Level = LogLevel.Information, Message = "Entities created: {Entities} entities, {Edges} edges in {Batches} batches")]
        public static partial void EntitiesCreated(ILogger logger, long entities, long edges, int batches);

        [LoggerMessage(Level = LogLevel.Information, Message = "Resolving entity IDs for junction table population")]
        public static partial void ResolvingEntityIds(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Existing codepoint_property rows: {Count} (will be skipped)")]
        public static partial void ExistingPropertyRows(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Junction tables written: {Resolved} resolved, {Unresolved} unresolved, {Duplicates} duplicates skipped")]
        public static partial void JunctionTablesWritten(ILogger logger, int resolved, int unresolved, int duplicates);

        [LoggerMessage(Level = LogLevel.Information, Message = "UCD/UCA decomposition complete: {Entities} entities, {Edges} edges total")]
        public static partial void DecompositionComplete(ILogger logger, long entities, long edges);
    }
}
