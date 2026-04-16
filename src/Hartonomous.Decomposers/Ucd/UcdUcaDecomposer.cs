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
    private readonly string _connectionString;

    public UcdUcaDecomposer(DecomposerConfig config, ILogger<UcdUcaDecomposer> logger)
        : base(config, logger)
    {
        _sourceDir = config.SourceDirectory;
        _connectionString = config.ConnectionString;
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
        UcdReferenceTableWriter refWriter = new(_connectionString);
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
                        [new EdgeMemberSpec(entity, null, "source", 0),
                         new EdgeMemberSpec(target, null, "target", 1)]);
                    edgeCount++;
                }

                if (cp.SimpleCaseFolding.HasValue && cp.SimpleCaseFolding.Value != cp.Value)
                {
                    byte[] targetHash = HashCodepoint(cp.SimpleCaseFolding.Value);
                    EntityHandle target = batch.AddEntity(targetHash, "codepoint");
                    batch.AddEdge("case_folds_to", ProvenanceCode,
                        [new EdgeMemberSpec(entity, null, "source", 0),
                         new EdgeMemberSpec(target, null, "target", 1)]);
                    edgeCount++;
                }

                if (collationMap.TryGetValue(cp.Value, out CollationWeight weights))
                {
                    byte[] ceHash = HashCollationElement(weights);
                    EntityHandle ceEntity = batch.AddEntity(ceHash, "collation_element");
                    batch.AddEdge("has_collation_weight", ProvenanceCode,
                        [new EdgeMemberSpec(entity, null, "source", 0),
                         new EdgeMemberSpec(ceEntity, null, "target", 1)]);
                    edgeCount++;
                }

                if (ucaSortedCps.TryGetValue(cp.Value, out int ucaIndex))
                {
                    (double x, double y, double z, double m) = SuperFibonacciS3.Project(ucaIndex, totalUcaEntries);
                    byte[] wkb = PointZMToWkb(x, y, z, m);
                    batch.AddPhysicality(entity, "s3_position", wkb);
                }

                entityCount++;

                if (batch.EntityCount >= BatchSize)
                {
                    batchNum++;
                    await SubmitAndReportAsync(pipeline, reporter, batch,
                        new ProgressSnapshot
                        {
                            DecomposerCode = ProvenanceCode,
                            CurrentPhase = "entities",
                            EntitiesCreated = entityCount,
                            EdgesCreated = edgeCount,
                            CurrentFile = "ucd.all.grouped.xml",
                            CurrentBatch = batchNum,
                        }, ct);
                    batch = pipeline.CreateBatch();
                }
            }

            if (batch.EntityCount > 0)
            {
                batchNum++;
                await SubmitAndReportAsync(pipeline, reporter, batch,
                    new ProgressSnapshot
                    {
                        DecomposerCode = ProvenanceCode,
                        CurrentPhase = "entities",
                        EntitiesCreated = entityCount,
                        EdgesCreated = edgeCount,
                        CurrentFile = "ucd.all.grouped.xml",
                        CurrentBatch = batchNum,
                    }, ct);
            }

            Log.EntitiesCreated(Logger, entityCount, edgeCount, batchNum);

            // ── Phase 5: Resolve entity IDs and write codepoint_property junction table ──
            Log.ResolvingEntityIds(Logger);

            List<byte[]> allHashes = cpHashMap.Values.ToList();
            IReadOnlyDictionary<byte[], long> entityIdMap =
                await pipeline.ResolveEntityIdsAsync(allHashes, ct);

            List<CodepointPropertyRow> propertyRows = new(allCodepoints.Count);
            int resolved = 0;
            int unresolved = 0;

            foreach (CodepointRecord cp in allCodepoints)
            {
                if (!cpHashMap.TryGetValue(cp.Value, out byte[]? hash))
                {
                    unresolved++;
                    continue;
                }

                if (!entityIdMap.TryGetValue(hash, out long entityId))
                {
                    unresolved++;
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

                propertyRows.Add(new CodepointPropertyRow(
                    entityId, gcId, scriptId, blockId, gcbId, wbId, sbId, lbId));
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

            Log.JunctionTablesWritten(Logger, resolved, unresolved);
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

    private static byte[] HashCodepoint(int cpValue)
    {
        byte[] cpBytes = new byte[4];
        cpBytes[0] = (byte)(cpValue >> 24);
        cpBytes[1] = (byte)(cpValue >> 16);
        cpBytes[2] = (byte)(cpValue >> 8);
        cpBytes[3] = (byte)cpValue;
        return ComputeHash(cpBytes.AsSpan());
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

    internal static byte[] PointZMToWkb(double x, double y, double z, double m)
    {
        byte[] wkb = new byte[37];
        wkb[0] = 1; // little-endian
        BitConverter.TryWriteBytes(wkb.AsSpan(1), 0xC0000001u); // PointZM
        BitConverter.TryWriteBytes(wkb.AsSpan(5), x);
        BitConverter.TryWriteBytes(wkb.AsSpan(13), y);
        BitConverter.TryWriteBytes(wkb.AsSpan(21), z);
        BitConverter.TryWriteBytes(wkb.AsSpan(29), m);
        return wkb;
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

        [LoggerMessage(Level = LogLevel.Information, Message = "Junction tables written: {Resolved} resolved, {Unresolved} unresolved")]
        public static partial void JunctionTablesWritten(ILogger logger, int resolved, int unresolved);

        [LoggerMessage(Level = LogLevel.Information, Message = "UCD/UCA decomposition complete: {Entities} entities, {Edges} edges total")]
        public static partial void DecompositionComplete(ILogger logger, long entities, long edges);
    }
}
