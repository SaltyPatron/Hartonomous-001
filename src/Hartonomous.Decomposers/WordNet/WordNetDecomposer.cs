using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using Hartonomous.Core.Text;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.WordNet;

/// <summary>
/// Princeton WordNet 3.0 decomposer with content-pure synset identity.
///
/// Synset identity = BLAKE3 Merkle of (sorted member lemma hashes plus
/// gloss text-composition hashes). The WordNet file offset and lex_filenum are placement
/// metadata, not content, so they do NOT enter the synset's identity hash.
/// Two synsets with the same lemma group + same gloss collapse to one
/// substrate.entity row even from different lexicons.
///
/// Senses are has_sense edges (lemma → synset). No separate word_sense
/// entity exists — the relation IS the sense. Per-arena Glicko ratings
/// for each sense live on substrate.edge_significance keyed by the
/// has_sense edge.
///
/// External identifiers (WordNet offsets) are recorded as substrate content
/// via has_wordnet_offset edges (synset → text_composition for the offset
/// string). OMW joins via these edges to find synsets by their authoring
/// offset, instead of computing matching identity hashes from placement.
///
/// Per-sense verb sentence frames are n-ary has_frame edges
/// (lemma=source + frame_text=target + synset=context). The context role
/// disambiguates which sense's frame is being attested without needing
/// an intermediate word_sense entity.
///
/// WordNet's 24 pointer-relation types (hypernym/hyponym/holonym/meronym/
/// antonym/etc.) become substrate.edge rows synset → synset, all of which
/// use the offset → synset_hash map built in pass 1.
/// </summary>
public sealed partial class WordNetDecomposer : TextIngestingDecomposer
{
    public override string ProvenanceCode => "princeton_wordnet";
    public override string DisplayName => "WordNet 3.0 (Princeton)";
    public override IReadOnlyList<Phase> Phases => [Phase.WordNetOmw];

    protected override double TrustPriorMu => 95000.0;
    protected override ICodepointProperties CodepointProperties => _codepointProperties;

    private readonly string _dictDir;
    private readonly ICodepointProperties _codepointProperties;
    private readonly IReferenceDataReader? _referenceDataReader;
    private readonly IJunctionWriter? _junctionWriter;
    private readonly IReferenceDataWriter? _referenceDataWriter;

    public WordNetDecomposer(
        DecomposerConfig config,
        Hartonomous.Core.Text.SubstrateTextDecomposer substrateTextDecomposer,
        ILogger<WordNetDecomposer> logger,
        ICodepointProperties codepointProperties,
        IReferenceDataReader? referenceDataReader = null,
        IJunctionWriter? junctionWriter = null,
        IReferenceDataWriter? referenceDataWriter = null)
        : base(config, substrateTextDecomposer, logger)
    {
        _dictDir = config.SourceDirectory;
        _codepointProperties = codepointProperties;
        _referenceDataReader = referenceDataReader;
        _junctionWriter = junctionWriter;
        _referenceDataWriter = referenceDataWriter;
    }

    protected override IReadOnlyList<string> GetSourcePaths() =>
    [
        Path.Combine(_dictDir, "data.noun"),
        Path.Combine(_dictDir, "data.verb"),
        Path.Combine(_dictDir, "data.adj"),
        Path.Combine(_dictDir, "data.adv"),
        Path.Combine(_dictDir, "index.sense"),
    ];

    protected override async Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        Log.Parsing(Logger);
        List<SynsetRecord> synsets = [];
        synsets.AddRange(WordNetParser.ParseDataFile(Path.Combine(_dictDir, "data.noun")));
        synsets.AddRange(WordNetParser.ParseDataFile(Path.Combine(_dictDir, "data.verb")));
        synsets.AddRange(WordNetParser.ParseDataFile(Path.Combine(_dictDir, "data.adj")));
        synsets.AddRange(WordNetParser.ParseDataFile(Path.Combine(_dictDir, "data.adv")));

        List<MorphException> morphExceptions = [];
        foreach ((string file, char pos) in new[]
        {
            ("noun.exc", 'n'), ("verb.exc", 'v'), ("adj.exc", 'a'), ("adv.exc", 'r'),
        })
        {
            string path = Path.Combine(_dictDir, file);
            if (File.Exists(path))
            {
                morphExceptions.AddRange(WordNetParser.ParseExceptionFile(path, pos));
            }
        }

        List<VerbSentence> verbSentences = [];
        List<VerbSentenceIndex> verbSentenceIndex = [];
        string sentsPath = Path.Combine(_dictDir, "sents.vrb");
        string sentidxPath = Path.Combine(_dictDir, "sentidx.vrb");
        if (File.Exists(sentsPath))
        {
            verbSentences = WordNetParser.ParseSentences(sentsPath);
        }
        if (File.Exists(sentidxPath))
        {
            verbSentenceIndex = WordNetParser.ParseSentenceIndex(sentidxPath);
        }

        // index.sense — canonical (sense_key, synset_offset, sense_number, tag_count)
        // table. We use it here to (a) report an honest count in the parse summary,
        // and (b) validate the synthetic sense_keys we construct for verb-frame
        // matching below. Tag_count and sense_number are read but not yet primed
        // into substrate.edge_significance — that requires a producer-surface
        // primitive for per-edge mu priming, which doesn't exist on IIngestionBatch
        // (edge significance is currently primed in bulk uniformly from
        // provenance.initial_mu). Wiring tag_count into the lexical_disambiguation
        // arena is its own architectural call.
        List<SenseIndexEntry> senseIndex = WordNetParser.ParseSenseIndex(
            Path.Combine(_dictDir, "index.sense"));
        HashSet<string> knownSenseKeys = new(senseIndex.Count, StringComparer.Ordinal);
        foreach (SenseIndexEntry sie in senseIndex)
        {
            knownSenseKeys.Add(sie.SenseKey);
        }

        Log.Parsed(Logger, synsets.Count, senseIndex.Count, morphExceptions.Count);

        WordNetReferenceTableWriter refWriter =
            new(_referenceDataReader!, _junctionWriter!, _referenceDataWriter!);
        try
        {
            Dictionary<string, int> posIdMap = await refWriter.LoadPosMapAsync(ct);
            Dictionary<string, int> lexnameIdMap = await refWriter.LoadLexnameMapAsync(ct);
            int engLangId = await refWriter.LoadEnglishLanguageIdAsync(ct);

            Dictionary<int, string> verbFrameTextById = new(verbSentences.Count);
            foreach (VerbSentence vs in verbSentences)
            {
                verbFrameTextById[vs.Id] = vs.Template;
            }
            Dictionary<string, IReadOnlyList<int>> frameIdsBySenseKey =
                new(verbSentenceIndex.Count, StringComparer.Ordinal);
            foreach (VerbSentenceIndex vsi in verbSentenceIndex)
            {
                frameIdsBySenseKey[vsi.SenseKey] = vsi.SentenceIds;
            }

            long entityCount = 0;
            long edgeCount = 0;
            int batchNum = 0;
            int synsetsProcessed = 0;
            Dictionary<string, long> edgeCountByType = new(StringComparer.Ordinal);

            // offsetCode → content-pure synset_hash. Built in pass 1, consumed
            // in pass 2 (pointer resolution) and by OMW via has_wordnet_offset.
            Dictionary<string, byte[]> offsetToSynsetHash =
                new(synsets.Count, StringComparer.Ordinal);

            IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);

            async Task FlushBatchAsync()
            {
                if (batch.EntityCount == 0 && batch.EdgeCount == 0)
                {
                    return;
                }
                batchNum++;
                await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount,
                    batchNum, "wordnet", ct);
                batch = pipeline.CreateBatch(ProvenanceCode);
            }

            void Bump(string code)
            {
                edgeCountByType.TryGetValue(code, out long c);
                edgeCountByType[code] = c + 1;
                edgeCount++;
            }

            // ── Pass 1: emit lemmas + synsets with content-pure hashes,
            //           glosses, has_sense edges, junctions, has_wordnet_offset.
            foreach (SynsetRecord syn in synsets)
            {
                ct.ThrowIfCancellationRequested();

                string offsetCode = $"{syn.Offset:D8}-{syn.SsType}";

                List<EntityHandle> memberHandles = new(syn.Words.Count);
                List<byte[]> memberHashes = new(syn.Words.Count);
                List<(byte[] Hash, (double X, double Y, double Z, double M) Centroid)> memberPhysicalityComponents = new(syn.Words.Count);
                foreach (SynsetWord sw in syn.Words)
                {
                    (EntityHandle h, byte[] lemmaHash, (double X, double Y, double Z, double M) lemmaCentroid) =
                        EmitText(batch, sw.Word, _codepointProperties, "lemma", TrustPriorMu);
                    batch.AddSignificance(h, "source_authority", TrustPriorMu);
                    memberHandles.Add(h);
                    memberHashes.Add(lemmaHash);
                    memberPhysicalityComponents.Add((lemmaHash, lemmaCentroid));
                    entityCount++;
                }

                // Synset content hash: Merkle over sorted member lemma hashes
                // plus core text-decomposition hashes for the authored gloss
                // content. Sorting drops member-order from the identity (the
                // order survives via has_sense edge member positions, not in
                // the synset's content).
                (byte[] Hash, (double X, double Y, double Z, double M) Centroid)[] sortedMemberPhysicalityComponents = memberPhysicalityComponents
                    .OrderBy(component => component.Hash, ByteArraySortComparer.Instance)
                    .ToArray();
                byte[][] sortedLemmaHashes = sortedMemberPhysicalityComponents
                    .Select(component => component.Hash)
                    .ToArray();

                (string definition, List<string> examples) = WordNetParser.ParseGloss(syn.Gloss);
                List<(string EdgeType, EntityHandle TextHandle)> glossEdges = [];
                List<byte[]> glossTextHashes = [];
                if (definition.Length > 0)
                {
                    EntityHandle defDoc = IngestText(batch, definition);
                    glossEdges.Add(("has_gloss", defDoc));
                    glossTextHashes.Add(defDoc.Hash);
                }
                foreach (string example in examples)
                {
                    if (example.Length == 0)
                    {
                        continue;
                    }
                    EntityHandle exDoc = IngestText(batch, example);
                    glossEdges.Add(("has_example", exDoc));
                    glossTextHashes.Add(exDoc.Hash);
                }
                if (glossTextHashes.Count == 0)
                {
                    EntityHandle emptyGlossDoc = IngestText(batch, syn.Gloss);
                    glossTextHashes.Add(emptyGlossDoc.Hash);
                }

                byte[][] synsetContent = new byte[sortedLemmaHashes.Length + glossTextHashes.Count][];
                Array.Copy(sortedLemmaHashes, synsetContent, sortedLemmaHashes.Length);
                for (int i = 0; i < glossTextHashes.Count; i++)
                {
                    synsetContent[sortedLemmaHashes.Length + i] = glossTextHashes[i];
                }
                byte[] synsetHash = ComputeMerkleHash(synsetContent.AsSpan());

                EntityHandle synsetHandle = batch.AddEntity(synsetHash, "synset");
                AddSynsetPhysicality(batch, synsetHandle, sortedMemberPhysicalityComponents);
                batch.AddSignificance(synsetHandle, "source_authority", TrustPriorMu);
                entityCount++;

                offsetToSynsetHash[offsetCode] = synsetHash;

                // External-id bridge edge for OMW + cross-lexicon resolution.
                // The offset string ("00001740-n") is a structured identifier,
                // not a natural-language sentence. Hash directly via
                // ComputeAtomicStringHash — OMW computes the same hash to
                // resolve synset_hash by querying has_wordnet_offset's target
                // entity. Routing this synthetic identifier through
                // TextDecomposer's full DAG (codepoints → graphemes →
                // word_forms → sentence → document) would be wasteful and
                // make the lookup harder to reproduce on the OMW side.
                byte[] offsetDocHash = WordNetSynsetIdentity.OffsetCodeHash(offsetCode);
                EntityHandle offsetDoc = batch.AddEntity(offsetDocHash, "text_composition");
                batch.AddEdge("has_wordnet_offset", ProvenanceCode,
                [
                    new EdgeMemberSpec(synsetHandle, "source", 0),
                    new EdgeMemberSpec(offsetDoc,    "target", 1),
                ]);
                Bump("has_wordnet_offset");

                // Synset-level POS classification (one POS per synset).
                string udPos = WordNetParser.PosCharToUdPos(WordNetParser.SsTypeToPos(syn.SsType));
                if (posIdMap.TryGetValue(udPos, out int posId))
                {
                    batch.AddJunction("entity_pos", synsetHandle, posId, TrustPriorMu);
                }

                // Synset-level lexname classification (one lexname per synset).
                string lexnameCode = GetLexname(syn.LexFileNum);
                if (lexnameIdMap.TryGetValue(lexnameCode, out int lexnameId))
                {
                    batch.AddJunction("entity_lexname", synsetHandle, lexnameId);
                }

                // has_sense edges: each member lemma → synset. The edge IS
                // the sense; per-arena Glicko ratings live on edge_significance.
                for (int i = 0; i < memberHandles.Count; i++)
                {
                    batch.AddEdge("has_sense", ProvenanceCode,
                    [
                        new EdgeMemberSpec(memberHandles[i], "source", 0),
                        new EdgeMemberSpec(synsetHandle,     "target", 1),
                    ]);
                    Bump("has_sense");
                    batch.AddJunction("entity_language", memberHandles[i], engLangId);
                }

                // has_gloss + has_example edges surface the same text entities
                // used in synset identity for navigation, recompose, and
                // example attestation.
                foreach ((string edgeType, EntityHandle textHandle) in glossEdges)
                {
                    batch.AddEdge(edgeType, ProvenanceCode,
                    [
                        new EdgeMemberSpec(synsetHandle, "source", 0),
                        new EdgeMemberSpec(textHandle,   "target", 1),
                    ]);
                    Bump(edgeType);
                }

                synsetsProcessed++;
                if (batch.EntityCount >= BatchSize || batch.EdgeCount >= BatchSize)
                {
                    await FlushBatchAsync();
                }
            }
            await FlushBatchAsync();
            Log.Pass1Done(Logger, synsetsProcessed, entityCount, edgeCount);

            // ── Pass 2: pointer relations (synset → synset) using the offset map. ──
            int pointerCount = 0;
            int frameEdgeCount = 0;
            int verbSenseKeyMismatches = 0;
            foreach (SynsetRecord syn in synsets)
            {
                ct.ThrowIfCancellationRequested();

                string offsetCode = $"{syn.Offset:D8}-{syn.SsType}";
                if (!offsetToSynsetHash.TryGetValue(offsetCode, out byte[]? srcHash))
                {
                    continue;
                }
                EntityHandle srcHandle = batch.AddEntity(srcHash, "synset");

                foreach (PointerRecord ptr in syn.Pointers)
                {
                    string relationCode = WordNetParser.PointerSymbolToRelation(ptr.Symbol);
                    if (relationCode.StartsWith("unknown_", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    string targetOffset = $"{ptr.TargetOffset:D8}-{ptr.TargetPos}";
                    if (!offsetToSynsetHash.TryGetValue(targetOffset, out byte[]? targetHash))
                    {
                        continue;
                    }
                    EntityHandle targetHandle = batch.AddEntity(targetHash, "synset");

                    batch.AddEdge(relationCode, ProvenanceCode,
                    [
                        new EdgeMemberSpec(srcHandle,    "source", 0),
                        new EdgeMemberSpec(targetHandle, "target", 1),
                    ]);
                    Bump(relationCode);
                    pointerCount++;
                }

                // Verb sentence frames per (lemma, synset) pair. The has_frame
                // edge is n-ary: source=lemma + target=frame_text + context=synset.
                // The context role makes the frame attestation specific to this
                // sense (i.e., this lemma's appearance in this synset), without
                // needing a word_sense intermediate entity.
                if (syn.SsType == 'v')
                {
                    foreach (SynsetWord sw in syn.Words)
                    {
                        // Reconstruct the WordNet sense_key for this (lemma, synset).
                        // Format: "lemma%ss_type:lex_filenum:lex_id:head_word:head_id"
                        // For verbs ss_type=2; head_word/head_id are empty.
                        string senseKey =
                            $"{sw.Word}%2:{syn.LexFileNum:D2}:{sw.LexId:D2}::";
                        if (!knownSenseKeys.Contains(senseKey))
                        {
                            // Synthetic key disagrees with index.sense — count it.
                            // (Frame match below will also miss; we want to know
                            // whether the miss is "no frames assigned" vs "bug in
                            // our synthetic-key construction".)
                            verbSenseKeyMismatches++;
                        }
                        if (!frameIdsBySenseKey.TryGetValue(senseKey, out IReadOnlyList<int>? fids))
                        {
                            continue;
                        }

                        (EntityHandle lemmaHandle, _, _) =
                            EmitText(batch, sw.Word, _codepointProperties, "lemma", TrustPriorMu);

                        foreach (int fid in fids)
                        {
                            if (!verbFrameTextById.TryGetValue(fid, out string? frameTxt))
                            {
                                continue;
                            }
                            EntityHandle frameDoc = IngestText(batch, frameTxt);
                            batch.AddEdge("has_frame", ProvenanceCode,
                            [
                                new EdgeMemberSpec(lemmaHandle, "source",  0),
                                new EdgeMemberSpec(frameDoc,    "target",  1),
                                new EdgeMemberSpec(srcHandle,   "context", 2),
                            ]);
                            Bump("has_frame");
                            frameEdgeCount++;
                        }
                    }
                }

                if (batch.EntityCount >= BatchSize || batch.EdgeCount >= BatchSize)
                {
                    await FlushBatchAsync();
                }
            }
            await FlushBatchAsync();
            Log.Pass2Done(Logger, pointerCount, frameEdgeCount);
            if (verbSenseKeyMismatches > 0)
            {
                Log.VerbSenseKeyMismatches(Logger, verbSenseKeyMismatches);
            }

            // ── Pass 3: morph exceptions → word_form + inflection_of. ──
            // Inflected forms ARE word_forms — content-addressed by their
            // UTF-8 bytes. The "is inflected" relationship lives on the
            // inflection_of edge (back to the lemma), not on a separate type.
            int morphEntityCount = 0;
            foreach (MorphException mex in morphExceptions)
            {
                ct.ThrowIfCancellationRequested();

                (EntityHandle inflectedHandle, _, _) =
                    EmitText(batch, mex.InflectedForm, _codepointProperties, "word_form", TrustPriorMu);
                batch.AddSignificance(inflectedHandle, "source_authority", TrustPriorMu);
                morphEntityCount++;
                entityCount++;

                if (posIdMap.TryGetValue(WordNetParser.PosCharToUdPos(mex.Pos), out int infPosId))
                {
                    batch.AddJunction("entity_pos", inflectedHandle, infPosId, TrustPriorMu);
                }
                batch.AddJunction("entity_language", inflectedHandle, engLangId);

                foreach (string baseForm in mex.BaseForms)
                {
                    (EntityHandle baseHandle, _, _) =
                        EmitText(batch, baseForm, _codepointProperties, "lemma", TrustPriorMu);

                    batch.AddEdge("inflection_of", ProvenanceCode,
                    [
                        new EdgeMemberSpec(inflectedHandle, "source", 0),
                        new EdgeMemberSpec(baseHandle,      "target", 1),
                    ]);
                    Bump("inflection_of");
                }
                if (batch.EntityCount >= BatchSize || batch.EdgeCount >= BatchSize)
                {
                    await FlushBatchAsync();
                }
            }
            await FlushBatchAsync();
            Log.MorphDone(Logger, morphEntityCount);

            foreach (KeyValuePair<string, long> kv in edgeCountByType)
            {
                Log.EdgesByType(Logger, kv.Key, kv.Value);
            }
            Log.DecompositionComplete(Logger, entityCount, edgeCount, synsetsProcessed);
            LogTextCacheStats();
        }
        finally
        {
            await refWriter.DisposeAsync();
        }
    }

    private static void AddSynsetPhysicality(
        IIngestionBatch batch,
        EntityHandle synsetHandle,
        (byte[] Hash, (double X, double Y, double Z, double M) Centroid)[] sortedMemberPhysicalityComponents)
    {
        if (sortedMemberPhysicalityComponents.Length == 0)
        {
            return;
        }

        int vertexCount = Math.Max(2, sortedMemberPhysicalityComponents.Length);
        (double X1, double X2, double X3, double X4)[] vertices = new (double X1, double X2, double X3, double X4)[vertexCount];
        for (int index = 0; index < sortedMemberPhysicalityComponents.Length; index++)
        {
            (double x, double y, double z, double m) = sortedMemberPhysicalityComponents[index].Centroid;
            vertices[index] = (x, y, z, m);
        }
        if (sortedMemberPhysicalityComponents.Length == 1)
        {
            vertices[1] = vertices[0];
        }

        batch.AddPhysicalityLineString4d(synsetHandle, "contour", vertices.AsSpan());
    }

    /// <summary>
    /// Lexicographic byte-array comparer for stable Merkle ordering of
    /// member-lemma hashes. Two hashes with the same prefix sort by their
    /// continuation byte. Equal lengths assumed (BLAKE3 always 32 bytes).
    /// </summary>
    private sealed class ByteArraySortComparer : IComparer<byte[]>
    {
        public static readonly ByteArraySortComparer Instance = new();
        public int Compare(byte[]? a, byte[]? b)
        {
            if (a is null) { return b is null ? 0 : -1; }
            if (b is null) { return 1; }
            int len = Math.Min(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                int diff = a[i] - b[i];
                if (diff != 0) { return diff; }
            }
            return a.Length - b.Length;
        }
    }

    private static string GetLexname(int lexFileNum) => lexFileNum switch
    {
        0 => "adj.all", 1 => "adj.pert", 2 => "adv.all",
        3 => "noun.Tops", 4 => "noun.act", 5 => "noun.animal",
        6 => "noun.artifact", 7 => "noun.attribute", 8 => "noun.body",
        9 => "noun.cognition", 10 => "noun.communication", 11 => "noun.event",
        12 => "noun.feeling", 13 => "noun.food", 14 => "noun.group",
        15 => "noun.location", 16 => "noun.motive", 17 => "noun.object",
        18 => "noun.person", 19 => "noun.phenomenon", 20 => "noun.plant",
        21 => "noun.possession", 22 => "noun.process", 23 => "noun.quantity",
        24 => "noun.relation", 25 => "noun.shape", 26 => "noun.state",
        27 => "noun.substance", 28 => "noun.time",
        29 => "verb.body", 30 => "verb.change", 31 => "verb.cognition",
        32 => "verb.communication", 33 => "verb.competition", 34 => "verb.consumption",
        35 => "verb.contact", 36 => "verb.creation", 37 => "verb.emotion",
        38 => "verb.motion", 39 => "verb.perception", 40 => "verb.possession",
        41 => "verb.social", 42 => "verb.stative", 43 => "verb.weather",
        44 => "adj.ppl",
        _ => "unknown",
    };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Parsing WordNet 3.0 data files")]
        public static partial void Parsing(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Parsed: {Synsets} synsets, {Senses} sense index entries, {Morphs} morph exceptions")]
        public static partial void Parsed(ILogger logger, int synsets, int senses, int morphs);

        [LoggerMessage(Level = LogLevel.Information, Message = "Pass 1 complete: {Synsets} synsets, {Entities} entities, {Edges} edges")]
        public static partial void Pass1Done(ILogger logger, int synsets, long entities, long edges);

        [LoggerMessage(Level = LogLevel.Information, Message = "Pass 2 complete: {Pointers} pointer edges, {Frames} has_frame edges")]
        public static partial void Pass2Done(ILogger logger, int pointers, int frames);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Verb sense_key mismatches vs index.sense: {Count} (synthetic key construction may be wrong for these — frames will be silently dropped)")]
        public static partial void VerbSenseKeyMismatches(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Morph exceptions: {Count} inflected_form entities + inflection_of edges")]
        public static partial void MorphDone(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Edges by type: {Code}={Count}")]
        public static partial void EdgesByType(ILogger logger, string code, long count);

        [LoggerMessage(Level = LogLevel.Information, Message = "WordNet 3.0 complete: {Entities} entities, {Edges} edges, {Synsets} synsets")]
        public static partial void DecompositionComplete(ILogger logger, long entities, long edges, int synsets);
    }
}
