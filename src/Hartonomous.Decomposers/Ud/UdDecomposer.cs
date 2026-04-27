using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Hartonomous.Core;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Data;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Ud;

public sealed partial class UdDecomposer : BaseDecomposer
{
    public override string ProvenanceCode => "universaldependencies";
    public override string DisplayName => "Universal Dependencies v2.17";
    public override IReadOnlyList<Phase> Phases => [Phase.UniversalDeps];

    private const double TrustPriorMu = 92000.0;

    /// <summary>
    /// Process files in chunks to bound junction accumulator memory. Each chunk goes
    /// through the full produce→consume→junction-flush cycle, then accumulators are cleared.
    /// UD 2.17 has ~686 .conllu files across 339 treebanks; 10 files per chunk
    /// keeps junction memory bounded even for the largest treebanks (e.g. Czech-PDTC ≈ 343 MB).
    /// </summary>
    private const int FileChunkSize = 10;

    private readonly string _rootDir;
    private readonly IReferenceDataReader? _referenceDataReader;
    private readonly IJunctionWriter? _junctionWriter;
    private readonly IReferenceDataWriter? _referenceDataWriter;

    public UdDecomposer(
        DecomposerConfig config,
        ILogger<UdDecomposer> logger,
        IReferenceDataReader? referenceDataReader = null,
        IJunctionWriter? junctionWriter = null,
        IReferenceDataWriter? referenceDataWriter = null)
        : base(config, logger)
    {
        _rootDir = config.SourceDirectory;
        _referenceDataReader = referenceDataReader;
        _junctionWriter = junctionWriter;
        _referenceDataWriter = referenceDataWriter;
    }

    protected override IReadOnlyList<string> GetSourcePaths() => [_rootDir];

    protected override async Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        List<UdTreebankInfo> banksAll = UdTreebankScanner.Scan(_rootDir);
        if (banksAll.Count == 0)
        {
            // Fail loud: a clean exit with 0 treebanks would silently mark the
            // phase complete and leave the substrate without ud_sentence /
            // ud_token / has_lemma / dependency edges — every downstream phase
            // (Wiktionary morph propagation, Tatoeba syntactic alignment,
            // Glicko-2 syntactic_role_fitness arena) would then run on an empty
            // UD foundation. Refusing to proceed forces the operator to fix
            // the source path or treebank availability before continuing.
            throw new InvalidOperationException(
                $"UD source root contained zero UD_* treebank directories: {_rootDir}. "
                + "Verify config.psd1's Seed.UniversalDepsRoot points at the actual ud-treebanks-v2.17 dir "
                + "and that the C# CLI's udConfig.SourceDirectory joined the same subpath.");
        }
        List<UdTreebankInfo> banks = new(banksAll.Count);
        foreach (UdTreebankInfo b in banksAll)
        {
            if (LanguageAllowed(b.LanguageCode))
            {
                banks.Add(b);
            }
        }
        Log.TreebanksDiscovered(Logger, banks.Count);
        if (banks.Count == 0)
        {
            throw new InvalidOperationException(
                $"UD scanned {banksAll.Count} treebanks but LanguageFilter rejected all of them. "
                + "Either drop LanguageFilter or include at least one ISO code present in the discovered banks.");
        }

        await using UdReferenceTableWriter refWriter = new(_referenceDataReader!, _junctionWriter!, _referenceDataWriter!);

        // ── Pass 1: scan for distinct deprels, morph features (parallel). ──
        ConcurrentDictionary<string, byte> deprelsBag = new(StringComparer.Ordinal);
        ConcurrentDictionary<(string Key, string Value), byte> morphFeatsBag = new();
        long pass1Sentences = 0;
        long pass1Tokens = 0;

        // Build flat list of all files with their bank context for parallel iteration.
        List<(UdTreebankInfo Bank, string File)> allFiles = new(banks.Count * 10);
        foreach (UdTreebankInfo bank in banks)
        {
            foreach (string file in bank.ConlluFiles)
            {
                allFiles.Add((bank, file));
            }
        }

        Parallel.ForEach(allFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
            () => (Sentences: 0L, Tokens: 0L),
            (item, _, local) =>
            {
                foreach (UdSentenceRecord sent in UdConllUParser.Parse(item.File))
                {
                    local.Sentences++;
                    foreach (UdTokenRecord tok in sent.Tokens)
                    {
                        local.Tokens++;
                        if (tok.Deprel is not null)
                        {
                            deprelsBag.TryAdd(tok.Deprel, 0);
                        }
                        foreach (UdMorphFeature f in tok.Feats)
                        {
                            morphFeatsBag.TryAdd((f.Key, f.Value), 0);
                        }
                    }
                }
                return local;
            },
            local =>
            {
                Interlocked.Add(ref pass1Sentences, local.Sentences);
                Interlocked.Add(ref pass1Tokens, local.Tokens);
            });

        HashSet<string> deprels = new(deprelsBag.Keys, StringComparer.Ordinal);
        HashSet<(string Key, string Value)> morphFeats = new(morphFeatsBag.Keys);
        Log.Pass1Scanned(Logger, pass1Sentences, pass1Tokens, deprels.Count, morphFeats.Count);

        // ── Populate reference tables + edge types. ──
        await refWriter.PopulateDeprelsAsync(deprels, ct);
        await refWriter.PopulateMorphFeaturesAsync(morphFeats, ct);
        await refWriter.UpsertDeprelEdgeTypesAsync(deprels, ct);

        Dictionary<string, int> posMap = await refWriter.LoadPosMapAsync(ct);
        Dictionary<string, int> deprelMap = await refWriter.LoadDeprelMapAsync(ct);
        Dictionary<(string, string), int> morphFeatMap = await refWriter.LoadMorphFeatureMapAsync(ct);
        Dictionary<string, int> languageMap = await refWriter.LoadLanguageCodeMapAsync(ct);

        Log.ReferenceDataPopulated(Logger, deprelMap.Count, morphFeatMap.Count);

        // ── Pass 2: emit entities via parallel producers, serial DB consumer. ──
        // Files are processed in chunks to bound junction accumulator memory.
        // Each chunk goes through produce→consume→junction-flush, then accumulators clear.
        long entityCount = 0;
        long edgeCount = 0;
        int batchNum = 0;
        long totalPosWritten = 0;
        long totalMorphWritten = 0;
        long totalLangWritten = 0;

        int totalFiles = allFiles.Count;
        int maxProducers = Math.Max(1, Environment.ProcessorCount - 1);

        for (int chunkStart = 0; chunkStart < totalFiles; chunkStart += FileChunkSize)
        {
            int chunkEnd = Math.Min(chunkStart + FileChunkSize, totalFiles);
            var chunkFiles = allFiles.GetRange(chunkStart, chunkEnd - chunkStart);

            ConcurrentBag<(byte[] Hash, string Upos)> perTokenUpos = [];
            ConcurrentBag<(byte[] Hash, List<int> Feats)> perTokenMorphFeats = [];
            ConcurrentBag<(byte[] Hash, int LangId)> perSentenceLang = [];
            ConcurrentDictionary<byte[], byte> needIdsDict = new(ByteArrayEqualityComparer.Instance);

            // Bounded channel: producers fill batches, consumer submits to DB.
            Channel<(IIngestionBatch Batch, string BankName)> batchChannel =
                Channel.CreateBounded<(IIngestionBatch, string)>(
                    new BoundedChannelOptions(Environment.ProcessorCount * 2)
                    {
                        SingleReader = true,
                        FullMode = BoundedChannelFullMode.Wait,
                    });

            // ── Consumer: serial batch submission. ──
            Task consumerTask = Task.Run(async () =>
            {
                await foreach ((IIngestionBatch b, string bankName) in batchChannel.Reader.ReadAllAsync(ct))
                {
                    int num = Interlocked.Increment(ref batchNum);
                    long ents = Interlocked.Read(ref entityCount);
                    long edgs = Interlocked.Read(ref edgeCount);
                    await ReportProgressAsync(pipeline, reporter, b, ents, edgs, num, bankName, ct);
                }
            }, ct);

            // ── Producers: parallel file processing for this chunk. ──
            await Parallel.ForEachAsync(chunkFiles, new ParallelOptions { MaxDegreeOfParallelism = maxProducers, CancellationToken = ct },
                async (item, innerCt) =>
                {
                    UdTreebankInfo bank = item.Bank;
                    string file = item.File;

                    int? langId = bank.LanguageCode is not null && languageMap.TryGetValue(bank.LanguageCode, out int lid)
                        ? lid
                        : null;
                    string subProvenance = $"{ProvenanceCode}/v2.17/{bank.DirectoryName}";
                    string fileKey = Path.GetFileName(file);

                    // Thread-local accumulators to avoid contention.
                    List<(byte[] Hash, string Upos)> localUpos = new(4096);
                    List<(byte[] Hash, List<int> Feats)> localMorphFeats = new(4096);
                    List<(byte[] Hash, int LangId)> localLang = new(256);
                    List<byte[]> localNeedIds = new(4096);
                    long localEntities = 0;
                    long localEdges = 0;

                    IIngestionBatch batch = pipeline.CreateBatch();

                    foreach (UdSentenceRecord sent in UdConllUParser.Parse(file))
                    {
                        innerCt.ThrowIfCancellationRequested();

                        if (batch.EntityCount >= BatchSize || batch.EdgeCount >= BatchSize)
                        {
                            await batchChannel.Writer.WriteAsync((batch, bank.DirectoryName), innerCt);
                            batch = pipeline.CreateBatch();
                        }

                        EmitSentence(
                            batch, bank, fileKey, sent, subProvenance,
                            posMap, morphFeatMap, langId,
                            localUpos, localMorphFeats, localLang, localNeedIds,
                            ref localEntities, ref localEdges);
                    }

                    if (batch.EntityCount > 0 || batch.EdgeCount > 0)
                    {
                        await batchChannel.Writer.WriteAsync((batch, bank.DirectoryName), innerCt);
                    }

                    // Merge thread-local accumulators into concurrent collections.
                    foreach (var entry in localUpos)
                    {
                        perTokenUpos.Add(entry);
                    }
                    foreach (var entry in localMorphFeats)
                    {
                        perTokenMorphFeats.Add(entry);
                    }
                    foreach (var entry in localLang)
                    {
                        perSentenceLang.Add(entry);
                    }
                    foreach (byte[] h in localNeedIds)
                    {
                        needIdsDict.TryAdd(h, 0);
                    }
                    Interlocked.Add(ref entityCount, localEntities);
                    Interlocked.Add(ref edgeCount, localEdges);
                });

            batchChannel.Writer.Complete();
            await consumerTask;

            // ── Flush junctions for this chunk. ──
            HashSet<byte[]> needIds = new(needIdsDict.Keys, ByteArrayEqualityComparer.Instance);
            (int pos, int morph, int lang) = await FlushJunctionsAsync(
                pipeline, refWriter, posMap, perTokenUpos, perTokenMorphFeats, perSentenceLang, needIds, ct);
            totalPosWritten += pos;
            totalMorphWritten += morph;
            totalLangWritten += lang;

            Log.ChunkJunctionsFlushed(Logger, chunkStart, chunkEnd, pos, morph, lang);
        }

        Log.EntitiesEmitted(Logger, entityCount, edgeCount);
        Log.JunctionsWritten(Logger, (int)totalPosWritten, (int)totalMorphWritten, (int)totalLangWritten);
    }

    private static async Task<(int Pos, int Morph, int Lang)> FlushJunctionsAsync(
        IIngestionPipeline pipeline,
        BaseReferenceTableWriter refWriter,
        Dictionary<string, int> posMap,
        ConcurrentBag<(byte[] Hash, string Upos)> perTokenUpos,
        ConcurrentBag<(byte[] Hash, List<int> Feats)> perTokenMorphFeats,
        ConcurrentBag<(byte[] Hash, int LangId)> perSentenceLang,
        HashSet<byte[]> needIds,
        CancellationToken ct)
    {
        IReadOnlyDictionary<byte[], long> ids = await pipeline.ResolveEntityIdsAsync(
            [.. needIds], ct);

        List<(long EntityId, int PosId)> posEntries = new(perTokenUpos.Count);
        foreach ((byte[] hash, string upos) in perTokenUpos)
        {
            if (ids.TryGetValue(hash, out long eid) && posMap.TryGetValue(upos, out int pid))
            {
                posEntries.Add((eid, pid));
            }
        }
        await refWriter.WriteEntityPosJunctionsAsync(posEntries, ct);

        List<(long EntityId, int MorphFeatureId)> morphEntries = new(perTokenMorphFeats.Count * 2);
        foreach ((byte[] hash, List<int> feats) in perTokenMorphFeats)
        {
            if (!ids.TryGetValue(hash, out long eid))
            {
                continue;
            }
            foreach (int mfId in feats)
            {
                morphEntries.Add((eid, mfId));
            }
        }
        await refWriter.WriteEntityMorphFeatureJunctionsAsync(morphEntries, ct);

        List<(long EntityId, int LangId)> langEntries = new(perSentenceLang.Count);
        foreach ((byte[] hash, int lang) in perSentenceLang)
        {
            if (ids.TryGetValue(hash, out long eid))
            {
                langEntries.Add((eid, lang));
            }
        }
        await refWriter.WriteEntityLanguageJunctionsAsync(langEntries, ct);

        return (posEntries.Count, morphEntries.Count, langEntries.Count);
    }

    private static void EmitSentence(
        IIngestionBatch batch,
        UdTreebankInfo bank,
        string fileKey,
        UdSentenceRecord sent,
        string subProvenance,
        Dictionary<string, int> posMap,
        Dictionary<(string, string), int> morphFeatMap,
        int? langId,
        List<(byte[] Hash, string Upos)> perTokenUpos,
        List<(byte[] Hash, List<int> Feats)> perTokenMorphFeats,
        List<(byte[] Hash, int LangId)> perSentenceLang,
        List<byte[]> needIds,
        ref long entityCount,
        ref long edgeCount)
    {
        List<(double X, double Y, double Z, double M)> sentVertices = new(sent.Tokens.Count);
        EntityHandle[] tokenHandles = new EntityHandle[sent.Tokens.Count];
        byte[][] tokenHashes = new byte[sent.Tokens.Count][];
        Dictionary<string, int> tokenIdIndex = new(sent.Tokens.Count, StringComparer.Ordinal);

        for (int ti = 0; ti < sent.Tokens.Count; ti++)
        {
            UdTokenRecord tok = sent.Tokens[ti];
            tokenIdIndex[tok.Id] = ti;

            // Word form entity: Merkle DAG (codepoints → grapheme_clusters → word_form).
            (EntityHandle wfHandle, byte[] wfHash) = EmitWordFormMerkle(batch, tok.Form);
            EmitContourPhysicality(batch, wfHandle, tok.Form);
            needIds.Add(wfHash);
            entityCount++;

            // ud_token entity: same Merkle hash as word_form, different entity type.
            // Same content = same hash. ud_token is the syntactic role; word_form is the morphological form.
            EntityHandle tokEntity = batch.AddEntity(wfHash, "ud_token");
            tokenHandles[ti] = tokEntity;
            tokenHashes[ti] = wfHash;
            batch.AddSignificance(tokEntity, "source_authority", TrustPriorMu);
            // ud_token gets its own contour physicality row. Physicality is
            // keyed by entity_id (not just hash); without this row, head→
            // dependent edges (the bulk of UD's edge volume) have no
            // resolvable endpoint geometry in substrate.entity_pointzm and
            // therefore can never receive a trajectory.
            EmitContourPhysicality(batch, tokEntity, tok.Form);
            entityCount++;

            // Lemma entity: Merkle hash (codepoints → grapheme clusters → lemma) for convergence
            // with WordNet/Wiktionary. has_lemma edge: word_form → lemma (schema edge type id=3).
            if (tok.Lemma is not null && tok.Lemma.Length > 0)
            {
                string lemmaForm = tok.Lemma;
                (EntityHandle lemmaEntity, byte[] lemmaHash) = EmitWordFormMerkle(batch, lemmaForm, "lemma");
                needIds.Add(lemmaHash);
                EmitContourPhysicality(batch, lemmaEntity, lemmaForm);
                entityCount++;

                batch.AddEdge("has_lemma", subProvenance,
                [
                    new EdgeMemberSpec(wfHandle, null, "source", 0),
                    new EdgeMemberSpec(lemmaEntity, null, "target", 1),
                ]);
                edgeCount++;
            }

            // POS and morph feature evidence — keyed by word_form hash so junction
            // records accumulate on the structural form entity, not placement-specific tokens.
            if (tok.Upos is not null && posMap.ContainsKey(tok.Upos))
            {
                perTokenUpos.Add((wfHash, tok.Upos));
            }

            if (tok.Feats.Count > 0)
            {
                List<int> featIds = new(tok.Feats.Count);
                foreach (UdMorphFeature f in tok.Feats)
                {
                    if (morphFeatMap.TryGetValue((f.Key, f.Value), out int mfId))
                    {
                        featIds.Add(mfId);
                    }
                }
                if (featIds.Count > 0)
                {
                    perTokenMorphFeats.Add((wfHash, featIds));
                }
            }

            sentVertices.Add(PhysicalityEmitter.CodepointS3Position(
                ComputeIntDigest(wfHash)));
        }

        // ud_sentence entity: Merkle hash of ordered token hashes (content-addressed).
        byte[] sentHash = ComputeMerkleHash(tokenHashes.AsSpan());
        EntityHandle sentEntity = batch.AddEntity(sentHash, "ud_sentence");
        batch.AddSignificance(sentEntity, "source_authority", TrustPriorMu);
        entityCount++;
        needIds.Add(sentHash);

        if (langId is int sentLang)
        {
            perSentenceLang.Add((sentHash, sentLang));
        }

        // Sequence: sentence → tokens in order.
        for (int ti = 0; ti < tokenHandles.Length; ti++)
        {
            batch.AddSequence(sentEntity, tokenHandles[ti], ti, 1);
        }

        if (sentVertices.Count >= 2)
        {
            (double, double, double, double)[] arr = new (double, double, double, double)[sentVertices.Count];
            for (int i = 0; i < sentVertices.Count; i++)
            {
                arr[i] = sentVertices[i];
            }
            batch.AddPhysicalityLineString4d(sentEntity, "contour", arr.AsSpan());
        }
        else if (sentVertices.Count == 1)
        {
            (double x, double y, double z, double m) = sentVertices[0];
            batch.AddPhysicalityPoint4d(sentEntity, "s3_position", x, y, z, m);
        }

        // Dependency edges: head != 0 and deprel != null.
        for (int ti = 0; ti < sent.Tokens.Count; ti++)
        {
            UdTokenRecord tok = sent.Tokens[ti];
            if (tok.Head is null || tok.Deprel is null)
            {
                continue;
            }
            if (tok.Head == "0")
            {
                continue;
            }
            if (!tokenIdIndex.TryGetValue(tok.Head, out int headIdx))
            {
                continue;
            }

            batch.AddEdge(tok.Deprel, subProvenance,
            [
                new EdgeMemberSpec(tokenHandles[ti], null, "dependent", 0),
                new EdgeMemberSpec(tokenHandles[headIdx], null, "head", 1),
            ]);
            edgeCount++;
        }
    }

    /// <summary>
    /// Deterministic index into Unicode codepoint space from a hash byte-array — pure
    /// dispersal for Super-Fibonacci projection of sentence token trajectories. Not a
    /// cryptographic digest; just a stable map from content hash to a repeatable S³
    /// anchor point for the LINESTRINGZM vertex representing that token in the sentence.
    /// </summary>
    private static int ComputeIntDigest(byte[] hash)
    {
        uint acc = 0;
        int lim = hash.Length < 8 ? hash.Length : 8;
        for (int i = 0; i < lim; i++)
        {
            acc = (acc * 31u) + hash[i];
        }
        return (int)(acc % (uint)PhysicalityEmitter.UnicodeCodepointSpace);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "UD: discovered {Count} treebanks")]
        public static partial void TreebanksDiscovered(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "UD pass 1: {Sentences} sentences, {Tokens} tokens; {Deprels} distinct deprels, {MorphFeats} distinct (key,value) features")]
        public static partial void Pass1Scanned(ILogger logger, long sentences, long tokens, int deprels, int morphFeats);

        [LoggerMessage(Level = LogLevel.Information, Message = "UD reference data: {Deprels} deprels, {MorphFeats} morph features populated")]
        public static partial void ReferenceDataPopulated(ILogger logger, int deprels, int morphFeats);

        [LoggerMessage(Level = LogLevel.Information, Message = "UD emitted: {Entities} entities, {Edges} edges")]
        public static partial void EntitiesEmitted(ILogger logger, long entities, long edges);

        [LoggerMessage(Level = LogLevel.Information, Message = "UD junctions: {Pos} entity_pos, {Morph} entity_morph_feature, {Lang} entity_language")]
        public static partial void JunctionsWritten(ILogger logger, int pos, int morph, int lang);

        [LoggerMessage(Level = LogLevel.Information, Message = "UD chunk [{Start}..{End}] junction flush: {Pos} pos, {Morph} morph, {Lang} lang")]
        public static partial void ChunkJunctionsFlushed(ILogger logger, int start, int end, int pos, int morph, int lang);
    }
}
