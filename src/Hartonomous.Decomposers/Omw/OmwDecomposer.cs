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
using Hartonomous.Core.Text.Segmentation;
using Hartonomous.Decomposers.Iso639;
using Hartonomous.Decomposers.WordNet;
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
    private readonly ICodepointProperties _codepointProperties;
    private readonly IReferenceDataReader? _referenceDataReader;
    private readonly IJunctionWriter? _junctionWriter;
    private readonly IReferenceDataWriter? _referenceDataWriter;

    public OmwDecomposer(
        DecomposerConfig config,
        ILogger<OmwDecomposer> logger,
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

        OmwReferenceTableWriter refWriter = new(_referenceDataReader!, _junctionWriter!, _referenceDataWriter!);
        try
        {
            Dictionary<string, int> langIdMap = await refWriter.LoadLanguageCodeMapAsync(ct);
            Log.LanguagesLoaded(Logger, langIdMap.Count);

            // Load UD POS code → id map so we can write entity_pos junctions for each lemma.
            Dictionary<string, int> posIdMap = await refWriter.LoadPosMapAsync(ct);

            // Bridge from WordNet's authoring offset string to the substrate's
            // content-pure synset_hash. Built by WordNet's has_wordnet_offset
            // edges in the prior pass; queried here via a single substrate
            // function. Key = ComputeHash("XXXXXXXX-p") — the same hash WordNet
            // used for the offset text_composition entity.
            Dictionary<byte[], byte[]> offsetDocHashToSynsetHash =
                await _referenceDataReader!.LoadWordNetOffsetSynsetMapAsync(ct);
            Log.OffsetMapLoaded(Logger, offsetDocHashToSynsetHash.Count);

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
            IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);
            Dictionary<string, EntityHandle> batchLemmaHandles = new(StringComparer.Ordinal);
            HashSet<byte[]> batchSynsetHashes = new(ByteArrayEqualityComparer.Instance);
            List<(EntityHandle LemmaHandle, byte[] SynsetHash, double TrustMu)> batchAlignments = new();

            async Task FlushBatchAsync()
            {
                if (batch.EntityCount == 0 && batch.EdgeCount == 0 && batchAlignments.Count == 0)
                {
                    return;
                }

                if (batchAlignments.Count > 0)
                {
                    // Hash-as-PK substrate: synset entities are identified by
                    // (entity_type_id, hash). The WordNet phase already wrote
                    // them; the OMW alignment edge writes (lemma_hash → synset_hash)
                    // directly, no resolve step. ON CONFLICT in substrate.entity
                    // makes re-emitting the synset entity here idempotent if a
                    // particular synset wasn't seen in WordNet.
                    foreach ((EntityHandle lemmaHandle, byte[] synsetHash, double _) in batchAlignments)
                    {
                        EntityHandle synsetHandle = batch.AddEntity(synsetHash, "synset");
                        batch.AddEdge("aligned_to_synset", ProvenanceCode,
                        [
                            new EdgeMemberSpec(lemmaHandle, "source", 0),
                            new EdgeMemberSpec(synsetHandle, "target", 1),
                        ]);
                        edgeCount++;
                    }
                }

                batchNum++;
                await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "omw", ct);

                batch = pipeline.CreateBatch(ProvenanceCode);
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

                    // Resolve substrate's content-pure synset hash from the
                    // OMW-supplied offset code via the bridge map. The offset
                    // string ("XXXXXXXX-p") is hashed as a text_composition by
                    // WordNet's has_wordnet_offset emission; we compute the
                    // same hash and look up the linked synset_hash. Skip
                    // entries whose offset isn't in the substrate (e.g. OMW
                    // referencing synsets WordNet didn't include).
                    byte[] offsetDocHash = WordNetSynsetIdentity.OffsetCodeHash(entry.SynsetCode);
                    if (!offsetDocHashToSynsetHash.TryGetValue(offsetDocHash, out byte[]? synsetHash))
                    {
                        continue;
                    }

                    string lemmaWord = entry.Word;
                    string lemmaKey = $"{lemmaWord}:{langCode}";

                    EntityHandle lemmaHandle;
                    if (batchLemmaHandles.TryGetValue(lemmaKey, out EntityHandle existing))
                    {
                        lemmaHandle = existing;
                    }
                    else
                    {
                        (EntityHandle h, byte[] _, _) =
                            EmitText(batch, lemmaWord, _codepointProperties, "lemma", trustMu);
                        lemmaHandle = h;
                        batch.AddSignificance(lemmaHandle, "source_authority", trustMu);
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

        [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Count} WordNet offset → synset hash bridges")]
        public static partial void OffsetMapLoaded(ILogger logger, int count);

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
