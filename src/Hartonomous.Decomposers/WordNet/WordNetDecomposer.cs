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

namespace Hartonomous.Decomposers.WordNet;

public sealed partial class WordNetDecomposer : BaseDecomposer
{
    public override string ProvenanceCode => "princeton_wordnet";
    public override string DisplayName => "WordNet 3.0 (Princeton)";
    public override IReadOnlyList<Phase> Phases => [Phase.WordNetOmw];

    private const double TrustPriorMu = 95000.0;

    private readonly string _dictDir;
    private readonly IReferenceDataReader? _referenceDataReader;
    private readonly IJunctionWriter? _junctionWriter;
    private readonly IReferenceDataWriter? _referenceDataWriter;

    public WordNetDecomposer(
        DecomposerConfig config,
        ILogger<WordNetDecomposer> logger,
        IReferenceDataReader? referenceDataReader = null,
        IJunctionWriter? junctionWriter = null,
        IReferenceDataWriter? referenceDataWriter = null)
        : base(config, logger)
    {
        _dictDir = config.SourceDirectory;
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
        // ── Parse all source files ──
        Log.Parsing(Logger);

        List<SynsetRecord> nounSynsets = WordNetParser.ParseDataFile(Path.Combine(_dictDir, "data.noun"));
        List<SynsetRecord> verbSynsets = WordNetParser.ParseDataFile(Path.Combine(_dictDir, "data.verb"));
        List<SynsetRecord> adjSynsets = WordNetParser.ParseDataFile(Path.Combine(_dictDir, "data.adj"));
        List<SynsetRecord> advSynsets = WordNetParser.ParseDataFile(Path.Combine(_dictDir, "data.adv"));

        List<SenseIndexEntry> senseIndex = WordNetParser.ParseSenseIndex(Path.Combine(_dictDir, "index.sense"));

        List<MorphException> morphExceptions = [];
        morphExceptions.AddRange(WordNetParser.ParseExceptionFile(Path.Combine(_dictDir, "noun.exc"), 'n'));
        morphExceptions.AddRange(WordNetParser.ParseExceptionFile(Path.Combine(_dictDir, "verb.exc"), 'v'));
        morphExceptions.AddRange(WordNetParser.ParseExceptionFile(Path.Combine(_dictDir, "adj.exc"), 'a'));
        morphExceptions.AddRange(WordNetParser.ParseExceptionFile(Path.Combine(_dictDir, "adv.exc"), 'r'));

        List<VerbSentence> verbSentences = WordNetParser.ParseSentences(Path.Combine(_dictDir, "sents.vrb"));
        List<VerbSentenceIndex> verbSentIdx = WordNetParser.ParseSentenceIndex(Path.Combine(_dictDir, "sentidx.vrb"));

        // Merge all synsets into one list per POS-keyed lookup.
        List<(SynsetRecord Synset, char Pos)> allSynsets = new(
            nounSynsets.Count + verbSynsets.Count + adjSynsets.Count + advSynsets.Count);
        foreach (SynsetRecord s in nounSynsets) { allSynsets.Add((s, 'n')); }
        foreach (SynsetRecord s in verbSynsets) { allSynsets.Add((s, 'v')); }
        foreach (SynsetRecord s in adjSynsets) { allSynsets.Add((s, 'a')); }
        foreach (SynsetRecord s in advSynsets) { allSynsets.Add((s, 'r')); }

        Log.Parsed(Logger, allSynsets.Count, senseIndex.Count, morphExceptions.Count);

        WordNetReferenceTableWriter refWriter = new(_referenceDataReader!, _junctionWriter!, _referenceDataWriter!);
        try
        {
            // ── Load reference data ──
            Dictionary<string, int> lexnameMap = await refWriter.LoadLexnameMapAsync(ct);
            Dictionary<string, int> posMap = await refWriter.LoadPosMapAsync(ct);
            int engLangId = await refWriter.LoadEnglishLanguageIdAsync(ct);

            // ── Build synset key → offset lookup (POS-qualified) ──
            // Key: "offset:pos" → unique synset identifier
            Dictionary<string, byte[]> synsetKeyToHash = new(allSynsets.Count, StringComparer.Ordinal);
            Dictionary<string, byte[]> lemmaToHash = new(150_000, StringComparer.Ordinal);
            Dictionary<string, byte[]> senseKeyToHash = new(senseIndex.Count, StringComparer.Ordinal);

            // Build sense key → (synset offset, pos) lookup for word-level pointer resolution.
            Dictionary<string, (int Offset, char Pos)> senseKeyLookup = new(senseIndex.Count, StringComparer.Ordinal);
            foreach (SenseIndexEntry si in senseIndex)
            {
                char ssType = ParseSsTypeFromSenseKey(si.SenseKey);
                senseKeyLookup[si.SenseKey] = (si.SynsetOffset, WordNetParser.SsTypeToPos(ssType));
            }

            // ── Step 1: Create synset + lemma + word_sense entities ──
            long entityCount = 0;
            long edgeCount = 0;
            int batchNum = 0;

            IIngestionBatch batch = pipeline.CreateBatch();

            // Track for junction table population.
            List<(string SynsetKey, string UdPos)> synsetPosEntries = new(allSynsets.Count);
            List<(string LemmaKey, string UdPos)> lemmaPosEntries = new(200_000);

            // Track gloss/example text_composition hashes for edge creation after ID resolution.
            List<(string SynsetKey, byte[] GlossHash)> glossEntries = new(allSynsets.Count);
            List<(string SynsetKey, byte[] ExampleHash)> exampleEntries = new(allSynsets.Count * 2);

            foreach ((SynsetRecord synset, char pos) in allSynsets)
            {
                ct.ThrowIfCancellationRequested();

                string synsetKey = $"{synset.Offset}:{pos}";
                string synsetCode = $"{synset.Offset:D8}-{pos}";
                byte[] synsetHash = ComputeHash(synsetCode);
                synsetKeyToHash[synsetKey] = synsetHash;

                EntityHandle synsetEntity = batch.AddEntity(synsetHash, "synset");
                batch.AddSignificance(synsetEntity, "source_authority", TrustPriorMu);
                entityCount++;

                // Decompose gloss into definition + examples → text_composition entities.
                (string definition, List<string> examples) = WordNetParser.ParseGloss(synset.Gloss);

                if (definition.Length > 0)
                {
                    (EntityHandle glossEntity, byte[] glossHash) = EmitWordFormMerkle(batch, definition, "text_composition");
                    batch.AddSignificance(glossEntity, "source_authority", TrustPriorMu);
                    EmitContourPhysicality(batch, glossEntity, definition);
                    glossEntries.Add((synsetKey, glossHash));
                    entityCount++;
                }

                foreach (string example in examples)
                {
                    (EntityHandle exampleEntity, byte[] exampleHash) = EmitWordFormMerkle(batch, example, "text_composition");
                    batch.AddSignificance(exampleEntity, "source_authority", TrustPriorMu);
                    EmitContourPhysicality(batch, exampleEntity, example);
                    exampleEntries.Add((synsetKey, exampleHash));
                    entityCount++;
                }

                string udPos = WordNetParser.PosCharToUdPos(pos);
                synsetPosEntries.Add((synsetKey, udPos));

                // Collect per-lemma vertex lists so the synset itself can emit a
                // MULTILINESTRINGZM trajectory that unions every member spelling.
                List<IReadOnlyList<(double X, double Y, double Z, double M)>> synsetMemberContours = new();

                // Lemmas in this synset.
                foreach (SynsetWord word in synset.Words)
                {
                    string lemmaKey = word.Word;

                    List<(double, double, double, double)> lemmaVertices =
                        PhysicalityEmitter.SurfaceFormVertices(lemmaKey);

                    if (!lemmaToHash.ContainsKey(lemmaKey))
                    {
                        // EmitLemmaMaybeCompound: monolexical → single lemma; multi-word
                        // ("high_rise") → lemma + part word_forms + lexicalized_compound edge.
                        (EntityHandle lemmaEntity, byte[] lemmaHash) =
                            EmitLemmaMaybeCompound(batch, lemmaKey, ProvenanceCode);
                        lemmaToHash[lemmaKey] = lemmaHash;
                        batch.AddSignificance(lemmaEntity, "source_authority", TrustPriorMu);

                        EmitContourPhysicality(batch, lemmaEntity, lemmaKey);

                        entityCount++;
                    }

                    // Record POS evidence for EVERY synset this lemma appears in,
                    // not just the first. "rake" as noun AND verb both accumulate.
                    lemmaPosEntries.Add((lemmaKey, udPos));

                    if (lemmaVertices.Count > 0)
                    {
                        synsetMemberContours.Add(lemmaVertices);
                    }
                }

                // Synset physicality = the S³ centroid of every member-lemma
                // vertex. The synset is one concept at one place on S³; the
                // member lemmas already preserve their own trajectories. We
                // average all (x, y, z, m) coordinates across every member
                // contour, then L2-normalize so the result lands on the unit
                // 3-sphere.
                if (synsetMemberContours.Count > 0)
                {
                    double sx = 0, sy = 0, sz = 0, sm = 0;
                    int n = 0;
                    foreach (IReadOnlyList<(double X, double Y, double Z, double M)> contour in synsetMemberContours)
                    {
                        foreach ((double X, double Y, double Z, double M) v in contour)
                        {
                            sx += v.X; sy += v.Y; sz += v.Z; sm += v.M;
                            n++;
                        }
                    }
                    if (n > 0)
                    {
                        double cx = sx / n, cy = sy / n, cz = sz / n, cm = sm / n;
                        double norm = Math.Sqrt(cx * cx + cy * cy + cz * cz + cm * cm);
                        if (norm > 0)
                        {
                            cx /= norm; cy /= norm; cz /= norm; cm /= norm;
                            batch.AddPhysicalityPoint4d(synsetEntity, "s3_position", cx, cy, cz, cm);
                        }
                    }
                }

                if (batch.EntityCount >= BatchSize)
                {
                    batchNum++;
                    await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "wordnet-3.0", ct);
                    batch = pipeline.CreateBatch();
                }
            }

            // Word sense entities from index.sense.
            foreach (SenseIndexEntry si in senseIndex)
            {
                ct.ThrowIfCancellationRequested();

                // Word sense identity = Merkle(lemma_hash, synset_hash).
                // The sense key encodes the lemma (before '%') and synset offset + pos.
                int pctSense = si.SenseKey.IndexOf('%');
                string senseLemmaStr = pctSense > 0 ? si.SenseKey[..pctSense] : si.SenseKey;
                char sensePos = WordNetParser.SsTypeToPos(ParseSsTypeFromSenseKey(si.SenseKey));
                string senseSynKey = $"{si.SynsetOffset}:{sensePos}";
                byte[] senseLemmaHash = ComputeWordFormHash(senseLemmaStr);
                byte[] senseSynHash = synsetKeyToHash.TryGetValue(senseSynKey, out byte[]? existingSynHash)
                    ? existingSynHash
                    : ComputeHash(""); // fallback — should not happen
                byte[] senseHash = ComputeMerkleHash(new[] { senseLemmaHash, senseSynHash }.AsSpan());
                senseKeyToHash[si.SenseKey] = senseHash;

                EntityHandle senseEntity = batch.AddEntity(senseHash, "word_sense");
                double mu = si.TagCount > 0 ? TrustPriorMu + (si.TagCount * 10.0) : TrustPriorMu;
                batch.AddSignificance(senseEntity, "lexical_disambiguation", mu);
                entityCount++;

                // word_sense trajectory = the spelling of its lemma (the part of the sense key
                // before the first '%'). Same geometric trajectory as the underlying lemma but
                // attached as an independent physicality on the sense entity.
                EmitContourPhysicality(batch, senseEntity, senseLemmaStr);

                if (batch.EntityCount >= BatchSize)
                {
                    batchNum++;
                    await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "wordnet-3.0", ct);
                    batch = pipeline.CreateBatch();
                }
            }

            // Morph exception lemma entities (inflected forms that may not be in synsets).
            foreach (MorphException exc in morphExceptions)
            {
                ct.ThrowIfCancellationRequested();

                string inflKey = exc.InflectedForm;
                if (!lemmaToHash.ContainsKey(inflKey))
                {
                    (EntityHandle entity, byte[] hash) =
                        EmitLemmaMaybeCompound(batch, inflKey, ProvenanceCode);
                    lemmaToHash[inflKey] = hash;
                    batch.AddSignificance(entity, "source_authority", TrustPriorMu);

                    EmitContourPhysicality(batch, entity, inflKey);
                    entityCount++;
                }

                foreach (string baseForm in exc.BaseForms)
                {
                    string baseKey = baseForm;
                    if (!lemmaToHash.ContainsKey(baseKey))
                    {
                        (EntityHandle entity, byte[] hash) =
                            EmitLemmaMaybeCompound(batch, baseKey, ProvenanceCode);
                        lemmaToHash[baseKey] = hash;
                        batch.AddSignificance(entity, "source_authority", TrustPriorMu);

                        EmitContourPhysicality(batch, entity, baseKey);
                        entityCount++;
                    }
                }

                if (batch.EntityCount >= BatchSize)
                {
                    batchNum++;
                    await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "wordnet-3.0", ct);
                    batch = pipeline.CreateBatch();
                }
            }

            // Verb frame template entities. Templates are real text (e.g. "Somebody ----s")
            // → emit through the canonical Merkle path so identical strings from
            // Wiktionary citations or text corpora collapse onto the same text_composition.
            Dictionary<int, byte[]> frameIdToHash = new(verbSentences.Count);
            foreach (VerbSentence vs in verbSentences)
            {
                (EntityHandle entity, byte[] hash) =
                    EmitWordFormMerkle(batch, vs.Template, "text_composition");
                frameIdToHash[vs.Id] = hash;
                batch.AddSignificance(entity, "source_authority", TrustPriorMu);

                // text_composition trajectory = contour through every codepoint of the template.
                EmitContourPhysicality(batch, entity, vs.Template);

                entityCount++;
            }

            // Submit remaining entities.
            if (batch.EntityCount > 0)
            {
                batchNum++;
                await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "wordnet-3.0", ct);
            }

            Log.EntitiesCreated(Logger, entityCount, batchNum);

            // ── Step 2: Resolve all entity IDs ──
            HashSet<byte[]> allHashes = new(ByteArrayEqualityComparer.Instance);
            foreach (byte[] h in synsetKeyToHash.Values) { allHashes.Add(h); }
            foreach (byte[] h in lemmaToHash.Values) { allHashes.Add(h); }
            foreach (byte[] h in senseKeyToHash.Values) { allHashes.Add(h); }
            foreach (byte[] h in frameIdToHash.Values) { allHashes.Add(h); }
            foreach ((_, byte[] h) in glossEntries) { allHashes.Add(h); }
            foreach ((_, byte[] h) in exampleEntries) { allHashes.Add(h); }

            IReadOnlyDictionary<byte[], long> entityIdMap =
                await pipeline.ResolveEntityIdsAsync([.. allHashes], ct);

            Log.IdsResolved(Logger, entityIdMap.Count);

            // ── Step 3: Populate sense reference table ──
            List<(string Code, string Gloss, int LexnameId, int PosId)> senseRows = new(allSynsets.Count);
            foreach ((SynsetRecord synset, char pos) in allSynsets)
            {
                string udPos = WordNetParser.PosCharToUdPos(pos);
                string lexname = GetLexname(synset.LexFileNum);
                int lexnameId = lexnameMap.GetValueOrDefault(lexname, 0);
                int posId = posMap.GetValueOrDefault(udPos, 0);

                if (lexnameId == 0 || posId == 0)
                {
                    continue;
                }

                string synsetCode = $"{synset.Offset:D8}-{pos}";
                senseRows.Add((synsetCode, synset.Gloss, lexnameId, posId));
            }
            await refWriter.PopulateSensesAsync(senseRows, ct);
            Dictionary<string, int> senseDbMap = await refWriter.LoadSenseMapAsync(ct);
            Log.SensesPopulated(Logger, senseRows.Count);

            // ── Step 4: entity_pos and entity_sense junctions ──
            List<(long EntityId, int PosId)> posJunctions = new(allSynsets.Count + lemmaPosEntries.Count);
            foreach ((string synsetKey, string udPos) in synsetPosEntries)
            {
                if (synsetKeyToHash.TryGetValue(synsetKey, out byte[]? hash) &&
                    entityIdMap.TryGetValue(hash, out long eid) &&
                    posMap.TryGetValue(udPos, out int posId))
                {
                    posJunctions.Add((eid, posId));
                }
            }
            foreach ((string lemmaKey, string udPos) in lemmaPosEntries)
            {
                if (lemmaToHash.TryGetValue(lemmaKey, out byte[]? hash) &&
                    entityIdMap.TryGetValue(hash, out long eid) &&
                    posMap.TryGetValue(udPos, out int posId))
                {
                    posJunctions.Add((eid, posId));
                }
            }
            await refWriter.WriteEntityPosJunctionsAsync(posJunctions, ct);
            Log.PosJunctionsWritten(Logger, posJunctions.Count);

            List<(long EntityId, int SenseId, double Mu)> senseJunctions = new(senseIndex.Count);
            foreach (SenseIndexEntry si in senseIndex)
            {
                char ssType = ParseSsTypeFromSenseKey(si.SenseKey);
                char pos = WordNetParser.SsTypeToPos(ssType);
                string synsetCode = $"{si.SynsetOffset:D8}-{pos}";

                if (senseKeyToHash.TryGetValue(si.SenseKey, out byte[]? wsHash) &&
                    entityIdMap.TryGetValue(wsHash, out long wsEid) &&
                    senseDbMap.TryGetValue(synsetCode, out int senseId))
                {
                    double mu = si.TagCount > 0 ? TrustPriorMu + (si.TagCount * 10.0) : TrustPriorMu;
                    senseJunctions.Add((wsEid, senseId, mu));
                }
            }
            await refWriter.WriteEntitySenseJunctionsAsync(senseJunctions, ct);
            Log.SenseJunctionsWritten(Logger, senseJunctions.Count);

            // ── Step 5: entity_language for synsets (English) ──
            List<long> synsetEntityIds = new(synsetKeyToHash.Count);
            foreach (byte[] h in synsetKeyToHash.Values)
            {
                if (entityIdMap.TryGetValue(h, out long eid))
                {
                    synsetEntityIds.Add(eid);
                }
            }
            await refWriter.WriteEntityLanguageJunctionsAsync(synsetEntityIds, engLangId, ct);
            Log.LanguageJunctionsWritten(Logger, synsetEntityIds.Count);

            // ── Step 6: Edges — semantic relations ──
            batch = pipeline.CreateBatch();

            foreach ((SynsetRecord synset, char pos) in allSynsets)
            {
                ct.ThrowIfCancellationRequested();

                string srcKey = $"{synset.Offset}:{pos}";
                if (!synsetKeyToHash.TryGetValue(srcKey, out byte[]? srcHash) ||
                    !entityIdMap.TryGetValue(srcHash, out long srcId))
                {
                    continue;
                }

                foreach (PointerRecord ptr in synset.Pointers)
                {
                    string relation = WordNetParser.PointerSymbolToRelation(ptr.Symbol);
                    char tgtPos = ptr.TargetPos;
                    string tgtKey = $"{ptr.TargetOffset}:{tgtPos}";

                    bool isWordLevel = ptr.SourceWordNum != 0;

                    if (isWordLevel)
                    {
                        // Word-level pointer: resolve to word_sense entities.
                        // We need the sense keys for the specific words.
                        // Skip if we can't resolve — word_sense resolution requires sense key matching.
                        continue;
                    }

                    if (!synsetKeyToHash.TryGetValue(tgtKey, out byte[]? tgtHash) ||
                        !entityIdMap.TryGetValue(tgtHash, out long tgtId))
                    {
                        continue;
                    }

                    batch.AddEdge(relation, ProvenanceCode,
                    [
                        new EdgeMemberSpec(null, srcId, "source", 0),
                        new EdgeMemberSpec(null, tgtId, "target", 1),
                    ]);
                    edgeCount++;

                    if (batch.EdgeCount >= BatchSize)
                    {
                        batchNum++;
                        await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "wordnet-3.0", ct);
                        batch = pipeline.CreateBatch();
                    }
                }
            }

            // ── Step 7: has_sense edges (lemma → synset) ──
            foreach ((SynsetRecord synset, char pos) in allSynsets)
            {
                ct.ThrowIfCancellationRequested();

                string synsetKey = $"{synset.Offset}:{pos}";
                if (!synsetKeyToHash.TryGetValue(synsetKey, out byte[]? synsetHash) ||
                    !entityIdMap.TryGetValue(synsetHash, out long synsetId))
                {
                    continue;
                }

                foreach (SynsetWord word in synset.Words)
                {
                    string lemmaKey = word.Word;
                    if (!lemmaToHash.TryGetValue(lemmaKey, out byte[]? lemmaHash) ||
                        !entityIdMap.TryGetValue(lemmaHash, out long lemmaId))
                    {
                        continue;
                    }

                    // has_sense: lemma → synset (schema edge type id=1).
                    batch.AddEdge("has_sense", ProvenanceCode,
                    [
                        new EdgeMemberSpec(null, lemmaId, "source", 0),
                        new EdgeMemberSpec(null, synsetId, "target", 1),
                    ]);
                    edgeCount++;
                }

                if (batch.EdgeCount >= BatchSize)
                {
                    batchNum++;
                    await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "wordnet-3.0", ct);
                    batch = pipeline.CreateBatch();
                }
            }

            // ── Step 8: Morphological exception edges ──
            foreach (MorphException exc in morphExceptions)
            {
                ct.ThrowIfCancellationRequested();

                string inflKey = exc.InflectedForm;
                if (!lemmaToHash.TryGetValue(inflKey, out byte[]? inflHash) ||
                    !entityIdMap.TryGetValue(inflHash, out long inflId))
                {
                    continue;
                }

                foreach (string baseForm in exc.BaseForms)
                {
                    string baseKey = baseForm;
                    if (!lemmaToHash.TryGetValue(baseKey, out byte[]? baseHash) ||
                        !entityIdMap.TryGetValue(baseHash, out long baseId))
                    {
                        continue;
                    }

                    batch.AddEdge("irregular_morphology", ProvenanceCode,
                    [
                        new EdgeMemberSpec(null, inflId, "source", 0),
                        new EdgeMemberSpec(null, baseId, "target", 1),
                    ]);
                    edgeCount++;
                }

                if (batch.EdgeCount >= BatchSize)
                {
                    batchNum++;
                    await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "wordnet-3.0", ct);
                    batch = pipeline.CreateBatch();
                }
            }

            // ── Step 9: Verb sentence example edges ──
            Dictionary<string, List<int>> senseKeyToSentIds = new(verbSentIdx.Count, StringComparer.Ordinal);
            foreach (VerbSentenceIndex vsi in verbSentIdx)
            {
                senseKeyToSentIds[vsi.SenseKey] = new List<int>(vsi.SentenceIds);
            }

            foreach (KeyValuePair<string, List<int>> kv in senseKeyToSentIds)
            {
                if (!senseKeyToHash.TryGetValue(kv.Key, out byte[]? wsHash) ||
                    !entityIdMap.TryGetValue(wsHash, out long wsId))
                {
                    continue;
                }

                foreach (int sentId in kv.Value)
                {
                    if (!frameIdToHash.TryGetValue(sentId, out byte[]? frameHash) ||
                        !entityIdMap.TryGetValue(frameHash, out long frameId))
                    {
                        continue;
                    }

                    batch.AddEdge("has_verb_example", ProvenanceCode,
                    [
                        new EdgeMemberSpec(null, wsId, "source", 0),
                        new EdgeMemberSpec(null, frameId, "target", 1),
                    ]);
                    edgeCount++;
                }
            }

            // ── Step 10: has_gloss edges (synset → text_composition) ──
            foreach ((string synsetKey, byte[] glossHash) in glossEntries)
            {
                if (!synsetKeyToHash.TryGetValue(synsetKey, out byte[]? synsetHash2) ||
                    !entityIdMap.TryGetValue(synsetHash2, out long synsetId) ||
                    !entityIdMap.TryGetValue(glossHash, out long glossId))
                {
                    continue;
                }

                batch.AddEdge("has_gloss", ProvenanceCode,
                [
                    new EdgeMemberSpec(null, synsetId, "source", 0),
                    new EdgeMemberSpec(null, glossId, "target", 1),
                ]);
                edgeCount++;

                if (batch.EdgeCount >= BatchSize)
                {
                    batchNum++;
                    await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "wordnet-3.0", ct);
                    batch = pipeline.CreateBatch();
                }
            }

            // ── Step 11: has_example edges (synset → text_composition) ──
            foreach ((string synsetKey, byte[] exampleHash) in exampleEntries)
            {
                if (!synsetKeyToHash.TryGetValue(synsetKey, out byte[]? synsetHash3) ||
                    !entityIdMap.TryGetValue(synsetHash3, out long synsetId) ||
                    !entityIdMap.TryGetValue(exampleHash, out long exampleId))
                {
                    continue;
                }

                batch.AddEdge("has_example", ProvenanceCode,
                [
                    new EdgeMemberSpec(null, synsetId, "source", 0),
                    new EdgeMemberSpec(null, exampleId, "target", 1),
                ]);
                edgeCount++;

                if (batch.EdgeCount >= BatchSize)
                {
                    batchNum++;
                    await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "wordnet-3.0", ct);
                    batch = pipeline.CreateBatch();
                }
            }

            // Submit final batch.
            if (batch.EdgeCount > 0 || batch.EntityCount > 0)
            {
                batchNum++;
                await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, "wordnet-3.0", ct);
            }

            Log.DecompositionComplete(Logger, entityCount, edgeCount, allSynsets.Count);
        }
        finally
        {
            await refWriter.DisposeAsync();
        }
    }

    private static char ParseSsTypeFromSenseKey(string senseKey)
    {
        int pctIdx = senseKey.IndexOf('%');
        if (pctIdx < 0 || pctIdx + 1 >= senseKey.Length)
        {
            return 'n';
        }

        return senseKey[pctIdx + 1] switch
        {
            '1' => 'n',
            '2' => 'v',
            '3' or '5' => 'a',
            '4' => 'r',
            _ => 'n',
        };
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

        [LoggerMessage(Level = LogLevel.Information, Message = "Entities created: {Count} in {Batches} batches")]
        public static partial void EntitiesCreated(ILogger logger, long count, int batches);

        [LoggerMessage(Level = LogLevel.Information, Message = "Entity IDs resolved: {Count}")]
        public static partial void IdsResolved(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Sense reference table: {Count} rows")]
        public static partial void SensesPopulated(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "entity_pos junctions: {Count}")]
        public static partial void PosJunctionsWritten(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "entity_sense junctions: {Count}")]
        public static partial void SenseJunctionsWritten(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "entity_language junctions: {Count} (eng)")]
        public static partial void LanguageJunctionsWritten(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "WordNet 3.0 complete: {Entities} entities, {Edges} edges, {Synsets} synsets")]
        public static partial void DecompositionComplete(ILogger logger, long entities, long edges, int synsets);
    }
}
