using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Hartonomous.Decomposers.Iso639;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Omw;

public sealed partial class OmwDecomposer : BaseDecomposer
{
    public override string ProvenanceCode => "omwn_consortium";
    public override string DisplayName => "Open Multilingual Wordnet (OMW)";
    public override IReadOnlyList<Phase> Phases => [Phase.WordNetOmw];

    private const double CuratedTrustMu = 90000.0;
    private const double CldrTrustMu = 70000.0;
    private const double WiktTrustMu = 50000.0;

    private readonly string _sourceDir;
    private readonly string _connectionString;

    public OmwDecomposer(DecomposerConfig config, ILogger<OmwDecomposer> logger)
        : base(config, logger)
    {
        _sourceDir = config.SourceDirectory;
        _connectionString = config.ConnectionString;
    }

    protected override IReadOnlyList<string> GetSourcePaths() =>
    [
        Path.Combine(_sourceDir, "wns"),
    ];

    protected override async Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        string wnsDir = Path.Combine(_sourceDir, "wns");
        List<OmwSourceInfo> sources = OmwParser.DiscoverTabFiles(wnsDir);
        Log.SourcesDiscovered(Logger, sources.Count);

        // Load synset hash lookup from WordNet phase.
        // Synset keys are "offset:pos" — we need to resolve synsetCode "XXXXXXXX-p" to the hash.
        // Build lookup: "XXXXXXXX-p" → byte[] hash (matching WordNet decomposer's hashing).
        // Also load language code → id map for entity_language junctions.
        OmwReferenceTableWriter refWriter = new(_connectionString);
        try
        {
            Dictionary<string, int> langIdMap = await refWriter.LoadLanguageCodeMapAsync(ct);
            Log.LanguagesLoaded(Logger, langIdMap.Count);

            long entityCount = 0;
            long edgeCount = 0;
            int batchNum = 0;
            int filesProcessed = 0;

            // Track all created lemma hashes for entity_language junction population.
            // Key: lemmaHash → (langIds to assign).
            Dictionary<string, byte[]> lemmaKeyToHash = new(500_000, StringComparer.Ordinal);
            Dictionary<string, HashSet<int>> lemmaKeyToLangIds = new(500_000, StringComparer.Ordinal);

            // Track alignment edges to create: (lemmaHash, synsetHash).
            List<(byte[] LemmaHash, byte[] SynsetHash, double TrustMu)> alignments = new(3_000_000);

            IIngestionBatch batch = pipeline.CreateBatch();

            foreach (OmwSourceInfo source in sources)
            {
                ct.ThrowIfCancellationRequested();

                List<OmwTabEntry> entries = OmwParser.ParseTabFile(source.FilePath);
                double trustMu = GetTrustMu(source.Tier);

                foreach (OmwTabEntry entry in entries)
                {
                    if (entry.Relation != "lemma")
                    {
                        continue;
                    }

                    // Resolve language.
                    string langCode = entry.LangCode;
                    if (!langIdMap.TryGetValue(langCode, out int langId))
                    {
                        continue;
                    }

                    // Compute synset hash matching WordNet decomposer convention.
                    // SynsetCode format: "XXXXXXXX-p" → "synset_XXXXXXXX_p"
                    byte[]? synsetHash = ParseSynsetHash(entry.SynsetCode);
                    if (synsetHash is null)
                    {
                        continue;
                    }

                    // Compute lemma hash.
                    string normalizedWord = entry.Word.ToLowerInvariant();
                    string lemmaKey = $"{normalizedWord}:{langCode}";

                    if (!lemmaKeyToHash.TryGetValue(lemmaKey, out byte[]? lemmaHash))
                    {
                        lemmaHash = ComputeHash(normalizedWord);
                        lemmaKeyToHash[lemmaKey] = lemmaHash;

                        EntityHandle lemmaEntity = batch.AddEntity(lemmaHash, "lemma");
                        batch.AddSignificance(lemmaEntity, "source_authority", trustMu);

                        // Compose lemma from codepoints.
                        int position = 0;
                        foreach (Rune rune in normalizedWord.EnumerateRunes())
                        {
                            byte[] cpHash = Iso639Decomposer.HashCodepoint(rune.Value);
                            EntityHandle cpHandle = batch.AddEntity(cpHash, "codepoint");
                            batch.AddSequence(lemmaEntity, cpHandle, position, 1);
                            position++;
                        }

                        // Emit contour trajectory physicality over the lemma's codepoint positions.
                        List<(double X, double Y, double Z, double M)> vertices =
                            PhysicalityEmitter.SurfaceFormVertices(normalizedWord);
                        if (vertices.Count >= 2)
                        {
                            batch.AddPhysicality(
                                lemmaEntity,
                                "contour",
                                PhysicalityEmitter.LineStringZmWkb(vertices));
                        }
                        else if (vertices.Count == 1)
                        {
                            (double x, double y, double z, double m) = vertices[0];
                            batch.AddPhysicality(
                                lemmaEntity,
                                "s3_position",
                                PhysicalityEmitter.PointZmWkb(x, y, z, m));
                        }

                        entityCount++;
                    }

                    // Track language assignment.
                    if (!lemmaKeyToLangIds.TryGetValue(lemmaKey, out HashSet<int>? langIds))
                    {
                        langIds = [];
                        lemmaKeyToLangIds[lemmaKey] = langIds;
                    }
                    langIds.Add(langId);

                    alignments.Add((lemmaHash, synsetHash, trustMu));

                    if (batch.EntityCount >= BatchSize)
                    {
                        batchNum++;
                        await SubmitBatchAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, ct);
                        batch = pipeline.CreateBatch();
                    }
                }

                filesProcessed++;
                if (filesProcessed % 100 == 0)
                {
                    Log.FilesProcessed(Logger, filesProcessed, sources.Count);
                }
            }

            // Submit remaining entities.
            if (batch.EntityCount > 0)
            {
                batchNum++;
                await SubmitBatchAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, ct);
            }

            Log.EntitiesCreated(Logger, entityCount, batchNum, filesProcessed);

            // ── Resolve entity IDs ──
            HashSet<byte[]> allHashes = new(ByteArrayEqualityComparer.Instance);
            foreach (byte[] h in lemmaKeyToHash.Values)
            {
                allHashes.Add(h);
            }

            // Collect unique synset hashes.
            HashSet<byte[]> synsetHashes = new(ByteArrayEqualityComparer.Instance);
            foreach ((byte[] _, byte[] synsetHash, double _) in alignments)
            {
                synsetHashes.Add(synsetHash);
            }
            foreach (byte[] h in synsetHashes)
            {
                allHashes.Add(h);
            }

            Log.ResolvingIds(Logger, allHashes.Count);
            IReadOnlyDictionary<byte[], long> entityIdMap =
                await pipeline.ResolveEntityIdsAsync([.. allHashes], ct);
            Log.IdsResolved(Logger, entityIdMap.Count);

            // ── Create alignment edges ──
            batch = pipeline.CreateBatch();
            foreach ((byte[] lemmaHash, byte[] synsetHash, double _) in alignments)
            {
                ct.ThrowIfCancellationRequested();

                if (!entityIdMap.TryGetValue(lemmaHash, out long lemmaId) ||
                    !entityIdMap.TryGetValue(synsetHash, out long synsetId))
                {
                    continue;
                }

                batch.AddEdge("aligned_to_synset", ProvenanceCode,
                [
                    new EdgeMemberSpec(null, lemmaId, "source", 0),
                    new EdgeMemberSpec(null, synsetId, "target", 1),
                ]);
                edgeCount++;

                if (batch.EdgeCount >= BatchSize)
                {
                    batchNum++;
                    await SubmitBatchAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, ct);
                    batch = pipeline.CreateBatch();
                }
            }

            if (batch.EdgeCount > 0 || batch.EntityCount > 0)
            {
                batchNum++;
                await SubmitBatchAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, ct);
            }

            Log.EdgesCreated(Logger, edgeCount);

            // ── entity_language junctions ──
            List<(long EntityId, int LangId)> langJunctions = new(lemmaKeyToLangIds.Count * 2);
            foreach (KeyValuePair<string, HashSet<int>> kv in lemmaKeyToLangIds)
            {
                if (!lemmaKeyToHash.TryGetValue(kv.Key, out byte[]? hash) ||
                    !entityIdMap.TryGetValue(hash, out long entityId))
                {
                    continue;
                }

                foreach (int langId in kv.Value)
                {
                    langJunctions.Add((entityId, langId));
                }
            }

            await refWriter.WriteEntityLanguageJunctionsAsync(langJunctions, ct);
            Log.LanguageJunctionsWritten(Logger, langJunctions.Count);

            Log.DecompositionComplete(Logger, entityCount, edgeCount, filesProcessed);
        }
        finally
        {
            await refWriter.DisposeAsync();
        }
    }

    private static byte[]? ParseSynsetHash(string synsetCode)
    {
        // Format: "XXXXXXXX-p" where X is digits and p is pos char.
        int dashIdx = synsetCode.IndexOf('-');
        if (dashIdx < 0 || dashIdx + 1 >= synsetCode.Length)
        {
            return null;
        }

        string offsetStr = synsetCode[..dashIdx];
        char pos = synsetCode[dashIdx + 1];

        if (!int.TryParse(offsetStr, System.Globalization.CultureInfo.InvariantCulture, out int offset))
        {
            return null;
        }

        return ComputeHash($"synset_{offset:D8}_{pos}");
    }

    private static double GetTrustMu(OmwSourceTier tier) => tier switch
    {
        OmwSourceTier.Curated => CuratedTrustMu,
        OmwSourceTier.Cldr => CldrTrustMu,
        OmwSourceTier.Wiktionary => WiktTrustMu,
        _ => WiktTrustMu,
    };

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
                CurrentFile = "omw",
                CurrentBatch = batchNum,
            }, ct);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Discovered {Count} OMW source files")]
        public static partial void SourcesDiscovered(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Count} language codes")]
        public static partial void LanguagesLoaded(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Files processed: {Done}/{Total}")]
        public static partial void FilesProcessed(ILogger logger, int done, int total);

        [LoggerMessage(Level = LogLevel.Information, Message = "Entities created: {Count} in {Batches} batches from {Files} files")]
        public static partial void EntitiesCreated(ILogger logger, long count, int batches, int files);

        [LoggerMessage(Level = LogLevel.Information, Message = "Resolving entity IDs for {Count} hashes")]
        public static partial void ResolvingIds(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Entity IDs resolved: {Count}")]
        public static partial void IdsResolved(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Alignment edges created: {Count}")]
        public static partial void EdgesCreated(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Information, Message = "entity_language junctions: {Count}")]
        public static partial void LanguageJunctionsWritten(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "OMW complete: {Entities} entities, {Edges} edges from {Files} files")]
        public static partial void DecompositionComplete(ILogger logger, long entities, long edges, int files);
    }
}
