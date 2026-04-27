using System;
using System.Collections.Generic;
using System.IO;
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
    private readonly IReferenceDataReader? _referenceDataReader;
    private readonly IJunctionWriter? _junctionWriter;
    private readonly IReferenceDataWriter? _referenceDataWriter;

    public OmwDecomposer(
        DecomposerConfig config,
        ILogger<OmwDecomposer> logger,
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
        OmwReferenceTableWriter refWriter = new(_referenceDataReader!, _junctionWriter!, _referenceDataWriter!);
        try
        {
            Dictionary<string, int> langIdMap = await refWriter.LoadLanguageCodeMapAsync(ct);
            Log.LanguagesLoaded(Logger, langIdMap.Count);

            // Load UD POS code → id map so we can write entity_pos junctions for each lemma.
            Dictionary<string, int> posIdMap = await refWriter.LoadPosMapAsync(ct);

            long entityCount = 0;
            long edgeCount = 0;
            int batchNum = 0;
            int filesProcessed = 0;

            // Per-batch state. Each batch carries lemma entities + their inline
            // entity_language / entity_pos junctions, and the cross-phase
            // aligned_to_synset edges (lemma in this batch → synset already
            // committed by the WordNetDecomposer in the prior phase).
            // Synset IDs for the batch are resolved at flush via the pipeline's
            // batch-scoped resolver — NOT a phase-wide hash list. Rules:
            // .claude/rules/00-hartonomous-core.md § "Banned patterns".
            IIngestionBatch batch = pipeline.CreateBatch();
            Dictionary<string, EntityHandle> batchLemmaHandles = new(StringComparer.Ordinal);
            HashSet<byte[]> batchSynsetHashes = new(ByteArrayEqualityComparer.Instance);
            List<(EntityHandle LemmaHandle, byte[] SynsetHash, double TrustMu)> batchAlignments = new();

            async Task FlushBatchAsync()
            {
                if (batch.EntityCount == 0 && batch.EdgeCount == 0 && batchAlignments.Count == 0)
                {
                    return;
                }

                if (batchAlignments.Count > 0 && batchSynsetHashes.Count > 0)
                {
                    IReadOnlyDictionary<byte[], long> synsetIds =
                        await pipeline.ResolveEntityIdsAsync([.. batchSynsetHashes], ct);

                    foreach ((EntityHandle lemmaHandle, byte[] synsetHash, double _) in batchAlignments)
                    {
                        if (!synsetIds.TryGetValue(synsetHash, out long synsetId))
                        {
                            continue;
                        }
                        batch.AddEdge("aligned_to_synset", ProvenanceCode,
                        [
                            new EdgeMemberSpec(lemmaHandle, null, "source", 0),
                            new EdgeMemberSpec(null, synsetId, "target", 1),
                        ]);
                        edgeCount++;
                    }
                }

                batchNum++;
                await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "omw", ct);

                batch = pipeline.CreateBatch();
                batchLemmaHandles.Clear();
                batchSynsetHashes.Clear();
                batchAlignments.Clear();
            }

            foreach (OmwSourceInfo source in sources)
            {
                ct.ThrowIfCancellationRequested();

                if (!LanguageAllowed(source.LangCode))
                {
                    continue;
                }

                List<OmwTabEntry> entries = OmwParser.ParseTabFile(source.FilePath);
                double trustMu = GetTrustMu(source.Tier);

                foreach (OmwTabEntry entry in entries)
                {
                    if (entry.Relation != "lemma")
                    {
                        continue;
                    }

                    string langCode = entry.LangCode;
                    if (!langIdMap.TryGetValue(langCode, out int langId))
                    {
                        continue;
                    }

                    // Synset identity matches WordNetDecomposer's hashing of the
                    // synsetCode "XXXXXXXX-p" — already in substrate from the
                    // prior WordNet phase. We resolve its substrate id at flush.
                    byte[] synsetHash = ComputeHash(entry.SynsetCode);

                    string lemmaWord = entry.Word;
                    string lemmaKey = $"{lemmaWord}:{langCode}";

                    EntityHandle lemmaHandle;
                    if (batchLemmaHandles.TryGetValue(lemmaKey, out EntityHandle existing))
                    {
                        lemmaHandle = existing;
                    }
                    else
                    {
                        (EntityHandle h, byte[] _) =
                            EmitLemmaMaybeCompound(batch, lemmaWord, ProvenanceCode);
                        lemmaHandle = h;
                        batch.AddSignificance(lemmaHandle, "source_authority", trustMu);
                        EmitContourPhysicality(batch, lemmaHandle, lemmaWord);
                        batchLemmaHandles[lemmaKey] = lemmaHandle;
                        entityCount++;

                        // entity_language junction inline.
                        batch.AddJunction("entity_language", lemmaHandle, langId);
                    }

                    // POS junction derived from the synset code suffix.
                    string udPos = SynsetCodeToUdPos(entry.SynsetCode);
                    if (udPos != "X" && posIdMap.TryGetValue(udPos, out int posId))
                    {
                        batch.AddJunction("entity_pos", lemmaHandle, posId, trustMu);
                    }

                    // aligned_to_synset edge: source = lemma (this batch), target =
                    // synset (resolved at flush from substrate.entity).
                    batchSynsetHashes.Add(synsetHash);
                    batchAlignments.Add((lemmaHandle, synsetHash, trustMu));

                    if (batch.EntityCount >= BatchSize || batchAlignments.Count >= BatchSize)
                    {
                        await FlushBatchAsync();
                    }
                }

                filesProcessed++;
                if (filesProcessed % 100 == 0)
                {
                    Log.FilesProcessed(Logger, filesProcessed, sources.Count);
                }
            }

            await FlushBatchAsync();

            Log.EntitiesCreated(Logger, entityCount, batchNum, filesProcessed);
            Log.EdgesCreated(Logger, edgeCount);
            Log.DecompositionComplete(Logger, entityCount, edgeCount, filesProcessed);
        }
        finally
        {
            await refWriter.DisposeAsync();
        }
    }

    /// <summary>
    /// Maps an OMW synset code ("XXXXXXXX-p") to the UD POS tag matching
    /// <c>WordNetParser.PosCharToUdPos</c>: n→NOUN, v→VERB, a/s→ADJ, r→ADV.
    /// Returns "X" if the suffix is missing or unrecognised.
    /// </summary>
    private static string SynsetCodeToUdPos(string synsetCode)
    {
        if (string.IsNullOrEmpty(synsetCode) || synsetCode.Length < 2 || synsetCode[^2] != '-')
        {
            return "X";
        }
        return synsetCode[^1] switch
        {
            'n' => "NOUN",
            'v' => "VERB",
            'a' or 's' => "ADJ",
            'r' => "ADV",
            _ => "X",
        };
    }

    private static double GetTrustMu(OmwSourceTier tier) => tier switch
    {
        OmwSourceTier.Curated => CuratedTrustMu,
        OmwSourceTier.Cldr => CldrTrustMu,
        OmwSourceTier.Wiktionary => WiktTrustMu,
        _ => WiktTrustMu,
    };

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

        [LoggerMessage(Level = LogLevel.Information, Message = "entity_pos junctions: {Count}")]
        public static partial void PosJunctionsWritten(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "OMW complete: {Entities} entities, {Edges} edges from {Files} files")]
        public static partial void DecompositionComplete(ILogger logger, long entities, long edges, int files);
    }
}
