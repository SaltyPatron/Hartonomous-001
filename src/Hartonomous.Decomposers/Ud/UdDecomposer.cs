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
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Ud;

public sealed partial class UdDecomposer : BaseDecomposer
{
    public override string ProvenanceCode => "universaldependencies";
    public override string DisplayName => "Universal Dependencies v2.17";
    public override IReadOnlyList<Phase> Phases => [Phase.UniversalDeps];

    private const double TrustPriorMu = 92000.0;

    private readonly string _rootDir;
    private readonly ICodepointProperties _codepointProperties;
    private readonly IReferenceDataReader? _referenceDataReader;
    private readonly IJunctionWriter? _junctionWriter;
    private readonly IReferenceDataWriter? _referenceDataWriter;

    public UdDecomposer(
        DecomposerConfig config,
        ILogger<UdDecomposer> logger,
        ICodepointProperties codepointProperties,
        IReferenceDataReader? referenceDataReader = null,
        IJunctionWriter? junctionWriter = null,
        IReferenceDataWriter? referenceDataWriter = null)
        : base(config, logger)
    {
        _rootDir = config.SourceDirectory;
        _codepointProperties = codepointProperties;
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
        bool rootExists = System.IO.Directory.Exists(_rootDir);
        Log.DiagRoot(Logger, _rootDir, rootExists);
        if (rootExists)
        {
            int allDirs = 0;
            int udDirs = 0;
            foreach (string d in System.IO.Directory.EnumerateDirectories(_rootDir, "*", System.IO.SearchOption.TopDirectoryOnly))
            {
                allDirs++;
                if (allDirs <= 3) { string n = System.IO.Path.GetFileName(d); Log.DiagAny(Logger, allDirs, n); }
            }
            foreach (string d in System.IO.Directory.EnumerateDirectories(_rootDir, "UD_*", System.IO.SearchOption.TopDirectoryOnly))
            {
                udDirs++;
                if (udDirs <= 3) { string n = System.IO.Path.GetFileName(d); Log.DiagUd(Logger, udDirs, n); }
            }
            Log.DiagPreScan(Logger, allDirs, udDirs);
        }
        List<UdTreebankInfo> banksAll = UdTreebankScanner.Scan(_rootDir);
        Log.DiagScanned(Logger, banksAll.Count);
        if (banksAll.Count == 0)
        {
            // Fail loud: a clean exit with 0 treebanks would silently mark the
            // phase complete and leave the substrate without sentence/token text,
            // has_lemma, or dependency edges — every downstream phase
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

        // ── Pass 1: scan for distinct deprels, morph features.
        // Sequential walk per the substrate's banned-pattern rule
        // (".claude/rules/00-hartonomous-core.md" — decomposers must NOT own
        // Channel.CreateBounded / Parallel.ForEachAsync; the pipeline owns
        // batching + threading). This is metadata discovery only, no DB writes,
        // so the cost is bounded by parser throughput. ──
        HashSet<string> deprels = new(StringComparer.Ordinal);
        HashSet<(string Key, string Value)> morphFeats = new();
        long pass1Sentences = 0;
        long pass1Tokens = 0;

        List<(UdTreebankInfo Bank, string File)> allFiles = new(banks.Count * 10);
        foreach (UdTreebankInfo bank in banks)
        {
            foreach (string file in bank.ConlluFiles)
            {
                allFiles.Add((bank, file));
            }
        }

        foreach ((UdTreebankInfo _, string file) in allFiles)
        {
            ct.ThrowIfCancellationRequested();
            foreach (UdSentenceRecord sent in UdConllUParser.Parse(file))
            {
                pass1Sentences++;
                foreach (UdTokenRecord tok in sent.Tokens)
                {
                    pass1Tokens++;
                    if (tok.Deprel is not null)
                    {
                        deprels.Add(tok.Deprel);
                    }
                    foreach (UdMorphFeature f in tok.Feats)
                    {
                        morphFeats.Add((f.Key, f.Value));
                    }
                }
            }
        }

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

        // ── Pass 2: serial file iteration, per-sentence inline emission.
        // Each batch carries entities + their edges + their junctions all in
        // the same flush — no phase-wide pipeline.ResolveEntityIdsAsync, no
        // decomposer-owned channels. The pipeline's ON CONFLICT (hash, type)
        // dedupe handles repeated word_form / lemma hashes across batches. ──
        long entityCount = 0;
        long edgeCount = 0;
        int batchNum = 0;
        long totalPosWritten = 0;
        long totalMorphWritten = 0;
        long totalLangWritten = 0;

        IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);
        string lastBank = string.Empty;

        foreach ((UdTreebankInfo bank, string file) in allFiles)
        {
            ct.ThrowIfCancellationRequested();

            int? langId = bank.LanguageCode is not null && languageMap.TryGetValue(bank.LanguageCode, out int lid)
                ? lid
                : null;
            lastBank = bank.DirectoryName;

            foreach (UdSentenceRecord sent in UdConllUParser.Parse(file))
            {
                ct.ThrowIfCancellationRequested();

                if (batch.EntityCount >= BatchSize || batch.EdgeCount >= BatchSize)
                {
                    batchNum++;
                    await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, lastBank, ct);
                    batch = pipeline.CreateBatch(ProvenanceCode);
                }

                (int posWritten, int morphWritten, int langWritten) = EmitSentenceInline(
                    batch, sent,
                    posMap, morphFeatMap, langId, bank.LanguageCode,
                    ref entityCount, ref edgeCount);
                totalPosWritten += posWritten;
                totalMorphWritten += morphWritten;
                totalLangWritten += langWritten;
            }
        }

        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            batchNum++;
            await ReportProgressAsync(pipeline, reporter, batch, entityCount, edgeCount, batchNum, lastBank, ct);
        }

        Log.EntitiesEmitted(Logger, entityCount, edgeCount);
        Log.JunctionsWritten(Logger, (int)totalPosWritten, (int)totalMorphWritten, (int)totalLangWritten);
    }

    private (int Pos, int Morph, int Lang) EmitSentenceInline(
        IIngestionBatch batch,
        UdSentenceRecord sent,
        Dictionary<string, int> posMap,
        Dictionary<(string, string), int> morphFeatMap,
        int? langId,
        string? langCode,
        ref long entityCount,
        ref long edgeCount)
    {
        EntityHandle[] tokenHandles = new EntityHandle[sent.Tokens.Count];
        Hash32[] tokenWordFormHashes = new Hash32[sent.Tokens.Count];
        Dictionary<string, int> tokenIdIndex = new(sent.Tokens.Count, StringComparer.Ordinal);

        int posWritten = 0;
        int morphWritten = 0;
        int langWritten = 0;

        for (int ti = 0; ti < sent.Tokens.Count; ti++)
        {
            UdTokenRecord tok = sent.Tokens[ti];
            tokenIdIndex[tok.Id] = ti;

            // Word form entity: native text DAG (codepoints → grapheme_clusters → word_form).
            // The shared text decomposer emits the recursive child-centroid trajectory
            // at every tier (codepoint POINTZM, grapheme_cluster LINESTRINGZM through
            // codepoint centroids, word_form LINESTRINGZM through grapheme centroids).
            (EntityHandle wfHandle, Hash32 _, (double X, double Y, double Z, double M) wfCentroid) =
                EmitText(batch, tok.Form, _codepointProperties, "word_form", TrustPriorMu);
            entityCount++;

            // The token IS the word_form. UD's per-token analysis (POS,
            // morph features, syntactic role via dependency edges, sentence
            // membership via composition metadata) is metadata on edges/
            // junctions — never a separate entity type. Content-addressing
            // means the same surface form across UD/WordNet/Tatoeba/user
            // prompt collapses to ONE entity.
            tokenHandles[ti] = wfHandle;
            tokenWordFormHashes[ti] = wfHandle.Hash;
            batch.AddSignificance(wfHandle, "source_authority", TrustPriorMu);

            // Lemma entity: Merkle hash (codepoints → grapheme clusters → lemma) for convergence
            // with WordNet/Wiktionary. has_lemma edge: word_form → lemma (schema edge type id=3).
            if (tok.Lemma is not null && tok.Lemma.Length > 0)
            {
                string lemmaForm = tok.Lemma;
                (EntityHandle lemmaEntity, Hash32 _, _) = EmitText(batch, lemmaForm, _codepointProperties, "lemma", TrustPriorMu);
                entityCount++;

                batch.AddEdge("has_lemma", ProvenanceCode,
                [
                    new EdgeMemberSpec(wfHandle, "source", 0),
                    new EdgeMemberSpec(lemmaEntity, "target", 1),
                ],
                ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                EdgeArenaRouter.EventsFor("has_lemma"));
                edgeCount++;
            }

            // POS and morph feature evidence — written inline as junction rows
            // keyed by the in-batch word_form hash. No phase-wide hash list,
            // no separate FlushJunctionsAsync pass.
            if (tok.Upos is not null && posMap.TryGetValue(tok.Upos, out int posId))
            {
                batch.AddJunction("entity_pos", wfHandle, posId, TrustPriorMu);
                posWritten++;
            }

            if (tok.Feats.Count > 0)
            {
                foreach (UdMorphFeature f in tok.Feats)
                {
                    if (morphFeatMap.TryGetValue((f.Key, f.Value), out int mfId))
                    {
                        batch.AddJunction("entity_morph_feature", wfHandle, mfId);
                        morphWritten++;
                    }
                }
            }

        }

        // The sentence itself is user-visible text, so its root identity must
        // come from the native text DAG rather than from UD token placement.
        string sentenceText = sent.Text ?? ReconstructSentenceText(sent.Tokens);
        (EntityHandle sentEntity, Hash32 _, _) =
            EmitText(batch, sentenceText, _codepointProperties, "text_composition", TrustPriorMu);
        entityCount++;

        if (langId is int sentLang && langCode is not null)
        {
            CrossLinkAttestation.EmitLanguageAttestation(batch, sentEntity, langCode, sentLang, ProvenanceCode);
            langWritten++;
        }

        // Sentence's structural-identity child manifest emits as a
        // mantissa-packed LINESTRINGZM into the content
        // physicality partition. Vertices reference the per-token word_form
        // entities by 104-bit hash prefix encoded in (X, Z) mantissas, with
        // (Y) carrying ordinal+RLE. substrate.get_composition_children /
        // composition_at / recompose_text / entity_by_hash_prefix decode
        // this geometry as the canonical child manifest — no separate
        // substrate.sequence table, no real-coord trajectory; the geometry
        // IS the indexed manifest.
        if (sent.Tokens.Count >= 1)
        {
            Hartonomous.Core.Ingestion.TrajectoryVertex[] verts =
                new Hartonomous.Core.Ingestion.TrajectoryVertex[sent.Tokens.Count];
            for (int i = 0; i < sent.Tokens.Count; i++)
            {
                verts[i] = Hartonomous.Core.Ingestion.TrajectoryVertex.FromHash(
                    tokenWordFormHashes[i], ordinal: i + 1, rle: 1, metadata: 0L);
            }
            batch.AddIngestionTrajectory(sentEntity, verts.AsSpan());
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

            batch.AddEdge(tok.Deprel, ProvenanceCode,
            [
                new EdgeMemberSpec(tokenHandles[ti], "dependent", 0),
                new EdgeMemberSpec(tokenHandles[headIdx], "head", 1),
            ],
            ReadOnlySpan<EdgeSignificanceSpec>.Empty,
            EdgeArenaRouter.EventsFor(tok.Deprel));
            edgeCount++;
        }

        return (posWritten, morphWritten, langWritten);
    }

    private static string ReconstructSentenceText(IReadOnlyList<UdTokenRecord> tokens)
    {
        StringBuilder builder = new();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }
            builder.Append(tokens[i].Form);
        }

        return builder.ToString();
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

        [LoggerMessage(Level = LogLevel.Information, Message = "UD scanner diagnostic: _rootDir='{Root}', Exists={Exists}")]
        public static partial void DiagRoot(ILogger logger, string root, bool exists);

        [LoggerMessage(Level = LogLevel.Information, Message = "  any dir [{Idx}]: {Name}")]
        public static partial void DiagAny(ILogger logger, int idx, string name);

        [LoggerMessage(Level = LogLevel.Information, Message = "  UD_* [{Idx}]: {Name}")]
        public static partial void DiagUd(ILogger logger, int idx, string name);

        [LoggerMessage(Level = LogLevel.Information, Message = "UD pre-scan: total={Total}, UD_*={UdMatches}")]
        public static partial void DiagPreScan(ILogger logger, int total, int udMatches);

        [LoggerMessage(Level = LogLevel.Information, Message = "UD scanner returned {Count} banks")]
        public static partial void DiagScanned(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "UD chunk [{Start}..{End}] junction flush: {Pos} pos, {Morph} morph, {Lang} lang")]
        public static partial void ChunkJunctionsFlushed(ILogger logger, int start, int end, int pos, int morph, int lang);
    }
}
