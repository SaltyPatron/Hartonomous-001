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

namespace Hartonomous.Decomposers.Text;

/// <summary>
/// Decomposes arbitrary UTF-8 text into the substrate using the UAX #29
/// segmentation stack already built in <c>Hartonomous.Core.Text.Segmentation</c>.
///
/// Levels:
///   1. Codepoints — content-addressed via BLAKE3, dedup against UCD-seeded entities.
///   2. Grapheme clusters — UAX #29 extended grapheme clusters. Multi-codepoint
///      clusters are compositions; single-codepoint clusters ARE the codepoint entity.
///   3. Words — UAX #29 word boundaries. Each word is a composition of its grapheme clusters.
///   4. Sentences — UAX #29 sentence boundaries. Each sentence is a composition of its words.
///   5. Document — top-level composition of all sentences.
///
/// Physicality: POINTZM for single-codepoint atoms, LINESTRINGZM contour for
/// compositions (through constituent S3 positions). All via <see cref="PhysicalityEmitter"/>.
///
/// Provenance: <c>user_session</c>. Trust prior mu=1000 (session-scoped).
/// </summary>
public sealed partial class TextDecomposer : BaseDecomposer
{
    private const double SessionTrustMu = 1000.0;

    private readonly string _sourcePath;
    private readonly ICodepointProperties _codepointProperties;

    public override string ProvenanceCode => "user_session";
    public override string DisplayName => "Text Decomposer";
    public override IReadOnlyList<Phase> Phases => [Phase.TextDecomp];

    public TextDecomposer(
        DecomposerConfig config,
        ILogger<TextDecomposer> logger,
        ICodepointProperties codepointProperties,
        IReferenceDataReader? referenceDataReader = null,
        IJunctionWriter? junctionWriter = null,
        IReferenceDataWriter? referenceDataWriter = null)
        : base(config, logger)
    {
        _sourcePath = config.SourceDirectory;
        _codepointProperties = codepointProperties;
    }

    protected override IReadOnlyList<string> GetSourcePaths() => [_sourcePath];

    protected override async Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        byte[] utf8Bytes = await File.ReadAllBytesAsync(_sourcePath, ct);
        Log.FileRead(Logger, _sourcePath, utf8Bytes.Length);

        // ── Synchronous segmentation (spans cannot cross await) ──
        SegmentationResult seg = Segment(utf8Bytes, _codepointProperties);
        Log.CodepointsParsed(Logger, seg.Codepoints.Count);
        Log.GraphemeClustersSegmented(Logger, seg.GraphemeClusters.Count);
        Log.WordsSegmented(Logger, seg.Words.Count);
        Log.SentencesSegmented(Logger, seg.Sentences.Count);

        // ── Emit entities into the pipeline (async batching) ──
        long entityCount = 0;
        long edgeCount = 0;
        int batchNum = 0;
        IIngestionBatch batch = pipeline.CreateBatch();

        // One document is emitted as one closed composition graph in one batch.
        // The document-local dictionaries below deduplicate repeated content so
        // a long text like Moby Dick does not create a fresh entity per occurrence.
        Dictionary<int, EntityHandle> cpHandles = new(seg.Codepoints.Count);
        Dictionary<long, EntityHandle> cpHandleByOffset = new(seg.Codepoints.Count);
        Dictionary<long, byte[]> cpHashByOffset = new(seg.Codepoints.Count);
        Dictionary<long, int> codepointIndexByOffset = new(seg.Codepoints.Count);
        Dictionary<long, EntityHandle> gcHandleByOffset = new(seg.GraphemeClusters.Count);
        Dictionary<long, byte[]> gcHashByOffset = new(seg.GraphemeClusters.Count);
        Dictionary<byte[], EntityHandle> gcHandlesByHash = new(ByteArrayEqualityComparer.Instance);
        Dictionary<byte[], EntityHandle> wordHandlesByHash = new(ByteArrayEqualityComparer.Instance);
        Dictionary<long, EntityHandle> wordHandleByOffset = new(seg.Words.Count);
        Dictionary<long, byte[]> wordHashByOffset = new(seg.Words.Count);
        Dictionary<byte[], EntityHandle> sentenceHandlesByHash = new(ByteArrayEqualityComparer.Instance);
        Dictionary<long, EntityHandle> sentenceHandleByOffset = new(seg.Sentences.Count);
        Dictionary<long, byte[]> sentenceHashByOffset = new(seg.Sentences.Count);
        Dictionary<byte[], EntityHandle> rawSpanHandlesByHash = new(ByteArrayEqualityComparer.Instance);

        // --- Codepoint entities (dedup with UCD) ---
        for (int codepointIndex = 0; codepointIndex < seg.Codepoints.Count; codepointIndex++)
        {
            (int cp, int byteOffset, _) = seg.Codepoints[codepointIndex];
            codepointIndexByOffset[byteOffset] = codepointIndex;

            byte[] hash = HashCodepoint(cp);
            if (cpHandles.TryGetValue(cp, out EntityHandle existingHandle))
            {
                cpHandleByOffset[byteOffset] = existingHandle;
                cpHashByOffset[byteOffset] = hash;
                continue;
            }

            EntityHandle handle = batch.AddEntity(hash, "codepoint");
            cpHandles[cp] = handle;
            cpHandleByOffset[byteOffset] = handle;
            cpHashByOffset[byteOffset] = hash;
            entityCount++;
        }

        // --- Grapheme cluster entities ---
        foreach (GraphemeRange gc in seg.GraphemeClusters)
        {
            ct.ThrowIfCancellationRequested();

            if (gc.CodepointLength == 1)
            {
                int cp = DecodeSingleCodepoint(utf8Bytes, (int)gc.ByteOffset, gc.ByteLength);
                EntityHandle cpHandle = cpHandles.TryGetValue(cp, out EntityHandle existing)
                    ? existing
                    : batch.AddEntity(HashCodepoint(cp), "codepoint");
                gcHandleByOffset[gc.ByteOffset] = cpHandle;
                gcHashByOffset[gc.ByteOffset] = HashCodepoint(cp);
            }
            else
            {
                byte[] gcHash = ComputeGraphemeClusterHash(utf8Bytes, gc, cpHandles, out EntityHandle[] cpSequence);
                if (!gcHandlesByHash.TryGetValue(gcHash, out EntityHandle gcEntity))
                {
                    gcEntity = batch.AddEntity(gcHash, "grapheme_cluster");
                    batch.AddSignificance(gcEntity, "source_authority", SessionTrustMu);
                    for (int i = 0; i < cpSequence.Length; i++)
                    {
                        batch.AddSequence(gcEntity, cpSequence[i], i, 1);
                    }

                    string gcText = Encoding.UTF8.GetString(utf8Bytes, (int)gc.ByteOffset, gc.ByteLength);
                    EmitContourPhysicality(batch, gcEntity, gcText);
                    gcHandlesByHash[gcHash] = gcEntity;
                    entityCount++;
                }

                gcHandleByOffset[gc.ByteOffset] = gcEntity;
                gcHashByOffset[gc.ByteOffset] = gcHash;
            }
        }

        // --- Word entities ---
        int graphemeIndex = 0;
        foreach (WordRange word in seg.Words)
        {
            ct.ThrowIfCancellationRequested();

            string wordText = Encoding.UTF8.GetString(utf8Bytes, (int)word.ByteOffset, word.ByteLength);
            List<EntityHandle> childHandles = [];
            List<byte[]> childHashes = [];

            while (graphemeIndex < seg.GraphemeClusters.Count)
            {
                GraphemeRange gc = seg.GraphemeClusters[graphemeIndex];
                if (gc.ByteOffset + gc.ByteLength <= word.ByteOffset)
                {
                    graphemeIndex++;
                    continue;
                }

                if (gc.ByteOffset >= word.ByteOffset + word.ByteLength)
                {
                    break;
                }

                if (gcHandleByOffset.TryGetValue(gc.ByteOffset, out EntityHandle gcHandle) &&
                    gcHashByOffset.TryGetValue(gc.ByteOffset, out byte[]? gcHash))
                {
                    childHandles.Add(gcHandle);
                    childHashes.Add(gcHash);
                }

                graphemeIndex++;
            }

            if (childHashes.Count == 0)
            {
                continue;
            }

            byte[] wordHash = ComputeMerkleHash(childHashes.ToArray().AsSpan());
            if (!wordHandlesByHash.TryGetValue(wordHash, out EntityHandle wordEntity))
            {
                wordEntity = batch.AddEntity(wordHash, "word_form");
                batch.AddSignificance(wordEntity, "source_authority", SessionTrustMu);
                for (int i = 0; i < childHandles.Count; i++)
                {
                    batch.AddSequence(wordEntity, childHandles[i], i, 1);
                }

                EmitContourPhysicality(batch, wordEntity, wordText);
                wordHandlesByHash[wordHash] = wordEntity;
                entityCount++;
            }

            wordHandleByOffset[word.ByteOffset] = wordEntity;
            wordHashByOffset[word.ByteOffset] = wordHash;
        }

        Log.WordEntitiesEmitted(Logger, wordHandlesByHash.Count);

        // --- Sentence entities ---
        int wordIndex = 0;
        foreach (SentenceRange sent in seg.Sentences)
        {
            ct.ThrowIfCancellationRequested();

            List<EntityHandle> sentChildHandles = [];
            List<byte[]> sentChildHashes = [];
            long cursor = sent.ByteOffset;
            long sentenceEnd = sent.ByteOffset + sent.ByteLength;

            while (wordIndex < seg.Words.Count)
            {
                WordRange word = seg.Words[wordIndex];
                if (word.ByteOffset + word.ByteLength <= sent.ByteOffset)
                {
                    wordIndex++;
                    continue;
                }

                if (word.ByteOffset >= sent.ByteOffset + sent.ByteLength)
                {
                    break;
                }

                AppendRawSpanIfAny(
                    batch,
                    utf8Bytes,
                    cursor,
                    word.ByteOffset,
                    seg.Codepoints,
                    codepointIndexByOffset,
                    cpHandleByOffset,
                    cpHashByOffset,
                    rawSpanHandlesByHash,
                    ref entityCount,
                    sentChildHandles,
                    sentChildHashes);

                if (wordHandleByOffset.TryGetValue(word.ByteOffset, out EntityHandle wordHandle) &&
                    wordHashByOffset.TryGetValue(word.ByteOffset, out byte[]? wordHash))
                {
                    sentChildHandles.Add(wordHandle);
                    sentChildHashes.Add(wordHash);
                }

                cursor = word.ByteOffset + word.ByteLength;
                wordIndex++;
            }

            AppendRawSpanIfAny(
                batch,
                utf8Bytes,
                cursor,
                sentenceEnd,
                seg.Codepoints,
                codepointIndexByOffset,
                cpHandleByOffset,
                cpHashByOffset,
                rawSpanHandlesByHash,
                ref entityCount,
                sentChildHandles,
                sentChildHashes);

            if (sentChildHashes.Count == 0)
            {
                continue;
            }

            byte[] sentHash = ComputeMerkleHash(sentChildHashes.ToArray().AsSpan());
            if (!sentenceHandlesByHash.TryGetValue(sentHash, out EntityHandle sentEntity))
            {
                sentEntity = batch.AddEntity(sentHash, "text_composition");
                batch.AddSignificance(sentEntity, "source_authority", SessionTrustMu);
                for (int i = 0; i < sentChildHandles.Count; i++)
                {
                    batch.AddSequence(sentEntity, sentChildHandles[i], i, 1);
                }

                string sentText = Encoding.UTF8.GetString(utf8Bytes, (int)sent.ByteOffset, sent.ByteLength);
                EmitContourPhysicality(batch, sentEntity, sentText);
                sentenceHandlesByHash[sentHash] = sentEntity;
                entityCount++;
            }

            sentenceHandleByOffset[sent.ByteOffset] = sentEntity;
            sentenceHashByOffset[sent.ByteOffset] = sentHash;
        }

        Log.SentenceEntitiesEmitted(Logger, sentenceHandlesByHash.Count);

        // --- Document entity ---
        List<EntityHandle> documentChildHandles = [];
        List<byte[]> documentChildHashes = [];
        long documentCursor = 0;

        foreach (SentenceRange sent in seg.Sentences)
        {
            AppendRawSpanIfAny(
                batch,
                utf8Bytes,
                documentCursor,
                sent.ByteOffset,
                seg.Codepoints,
                codepointIndexByOffset,
                cpHandleByOffset,
                cpHashByOffset,
                rawSpanHandlesByHash,
                ref entityCount,
                documentChildHandles,
                documentChildHashes);

            if (sentenceHandleByOffset.TryGetValue(sent.ByteOffset, out EntityHandle sentHandle) &&
                sentenceHashByOffset.TryGetValue(sent.ByteOffset, out byte[]? sentHash))
            {
                documentChildHandles.Add(sentHandle);
                documentChildHashes.Add(sentHash);
            }

            documentCursor = sent.ByteOffset + sent.ByteLength;
        }

        AppendRawSpanIfAny(
            batch,
            utf8Bytes,
            documentCursor,
            utf8Bytes.Length,
            seg.Codepoints,
            codepointIndexByOffset,
            cpHandleByOffset,
            cpHashByOffset,
            rawSpanHandlesByHash,
            ref entityCount,
            documentChildHandles,
            documentChildHashes);

        byte[] docHash = ComputeMerkleHash(documentChildHashes.ToArray().AsSpan());
        EntityHandle docEntity = batch.AddEntity(docHash, "document");
        batch.AddSignificance(docEntity, "source_authority", SessionTrustMu);
        entityCount++;

        for (int i = 0; i < documentChildHandles.Count; i++)
        {
            batch.AddSequence(docEntity, documentChildHandles[i], i, 1);
        }

        EmitContourPhysicality(batch, docEntity, Encoding.UTF8.GetString(utf8Bytes));

        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            batchNum++;
            await ReportProgressAsync(pipeline, reporter, batch,
                entityCount, edgeCount, batchNum, _sourcePath, ct, "document");
        }

        Log.DecompositionComplete(Logger, entityCount, edgeCount, batchNum);
    }

    private static void AppendRawSpanIfAny(
        IIngestionBatch batch,
        byte[] utf8Bytes,
        long startOffset,
        long endOffset,
        IReadOnlyList<(int Codepoint, int ByteOffset, int ByteLength)> codepoints,
        IReadOnlyDictionary<long, int> codepointIndexByOffset,
        IReadOnlyDictionary<long, EntityHandle> cpHandleByOffset,
        IReadOnlyDictionary<long, byte[]> cpHashByOffset,
        Dictionary<byte[], EntityHandle> rawSpanHandlesByHash,
        ref long entityCount,
        List<EntityHandle> targetHandles,
        List<byte[]> targetHashes)
    {
        if (endOffset <= startOffset)
        {
            return;
        }

        (EntityHandle handle, byte[] hash) = GetRawSpanHandle(
            batch,
            utf8Bytes,
            startOffset,
            checked((int)(endOffset - startOffset)),
            codepoints,
            codepointIndexByOffset,
            cpHandleByOffset,
            cpHashByOffset,
            rawSpanHandlesByHash,
            ref entityCount);

        targetHandles.Add(handle);
        targetHashes.Add(hash);
    }

    private static (EntityHandle Handle, byte[] Hash) GetRawSpanHandle(
        IIngestionBatch batch,
        byte[] utf8Bytes,
        long byteOffset,
        int byteLength,
        IReadOnlyList<(int Codepoint, int ByteOffset, int ByteLength)> codepoints,
        IReadOnlyDictionary<long, int> codepointIndexByOffset,
        IReadOnlyDictionary<long, EntityHandle> cpHandleByOffset,
        IReadOnlyDictionary<long, byte[]> cpHashByOffset,
        Dictionary<byte[], EntityHandle> rawSpanHandlesByHash,
        ref long entityCount)
    {
        if (!codepointIndexByOffset.TryGetValue(byteOffset, out int codepointIndex))
        {
            throw new InvalidOperationException($"Raw span start offset {byteOffset} is not aligned to a codepoint boundary.");
        }

        long endOffset = byteOffset + byteLength;
        List<EntityHandle> childHandles = [];
        List<byte[]> childHashes = [];

        while (codepointIndex < codepoints.Count)
        {
            (int _, int cpOffset, int cpByteLength) = codepoints[codepointIndex];
            if (cpOffset >= endOffset)
            {
                break;
            }

            if (cpOffset + cpByteLength > endOffset)
            {
                throw new InvalidOperationException(
                    $"Raw span [{byteOffset}, {endOffset}) splits a codepoint at offset {cpOffset}.");
            }

            childHandles.Add(cpHandleByOffset[cpOffset]);
            childHashes.Add(cpHashByOffset[cpOffset]);
            codepointIndex++;
        }

        if (childHashes.Count == 0)
        {
            throw new InvalidOperationException($"Raw span [{byteOffset}, {endOffset}) resolved to no codepoints.");
        }

        if (childHashes.Count == 1)
        {
            return (childHandles[0], childHashes[0]);
        }

        byte[] spanHash = ComputeMerkleHash(childHashes.ToArray().AsSpan());
        if (!rawSpanHandlesByHash.TryGetValue(spanHash, out EntityHandle spanEntity))
        {
            spanEntity = batch.AddEntity(spanHash, "text_composition");
            batch.AddSignificance(spanEntity, "source_authority", SessionTrustMu);
            for (int i = 0; i < childHandles.Count; i++)
            {
                batch.AddSequence(spanEntity, childHandles[i], i, 1);
            }

            string spanText = Encoding.UTF8.GetString(utf8Bytes, (int)byteOffset, byteLength);
            EmitContourPhysicality(batch, spanEntity, spanText);
            rawSpanHandlesByHash[spanHash] = spanEntity;
            entityCount++;
        }

        return (spanEntity, spanHash);
    }

    // ── Synchronous helpers (span-safe, no async) ──

    private sealed record SegmentationResult(
        List<(int Codepoint, int ByteOffset, int ByteLength)> Codepoints,
        List<GraphemeRange> GraphemeClusters,
        List<WordRange> Words,
        List<SentenceRange> Sentences);

    private static SegmentationResult Segment(byte[] utf8Bytes, ICodepointProperties props)
    {
        ReadOnlySpan<byte> utf8 = utf8Bytes.AsSpan();
        return new SegmentationResult(
            DecodeCodepoints(utf8),
            GraphemeClusters.Enumerate(utf8, props),
            WordBoundaries.EnumerateWords(utf8, props),
            SentenceBoundaries.Enumerate(utf8, props));
    }

    private static List<(int Codepoint, int ByteOffset, int ByteLength)> DecodeCodepoints(ReadOnlySpan<byte> utf8)
    {
        List<(int, int, int)> result = new(utf8.Length);
        int idx = 0;
        while (idx < utf8.Length)
        {
            (int cp, int consumed) = Utf8.DecodeOne(utf8[idx..]);
            if (consumed == 0)
            {
                break;
            }
            result.Add((cp, idx, consumed));
            idx += consumed;
        }
        return result;
    }

    private static int DecodeSingleCodepoint(byte[] utf8Bytes, int offset, int length)
    {
        ReadOnlySpan<byte> span = utf8Bytes.AsSpan(offset, length);
        (int cp, _) = Utf8.DecodeOne(span);
        return cp;
    }

    private static byte[] ComputeGraphemeClusterHash(
        byte[] utf8Bytes,
        GraphemeRange gc,
        Dictionary<int, EntityHandle> cpHandles,
        out EntityHandle[] cpSequence)
    {
        List<byte[]> cpHashes = [];
        List<EntityHandle> handles = [];
        int idx = (int)gc.ByteOffset;
        int end = idx + gc.ByteLength;
        while (idx < end)
        {
            (int cp, int consumed) = Utf8.DecodeOne(utf8Bytes.AsSpan(idx));
            if (consumed == 0)
            {
                break;
            }

            byte[] cpHash = HashCodepoint(cp);
            cpHashes.Add(cpHash);
            EntityHandle cpHandle = cpHandles.TryGetValue(cp, out EntityHandle existing)
                ? existing
                : throw new InvalidOperationException($"Codepoint {cp} missing from document-local cache.");
            handles.Add(cpHandle);
            idx += consumed;
        }

        cpSequence = handles.ToArray();
        return ComputeMerkleHash(cpHashes.ToArray().AsSpan());
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Text: read {Path} ({Bytes} bytes)")]
        public static partial void FileRead(ILogger logger, string path, int bytes);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Text: {Count} codepoints decoded")]
        public static partial void CodepointsParsed(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Text: {Count} grapheme clusters (UAX #29)")]
        public static partial void GraphemeClustersSegmented(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Text: {Count} words (UAX #29)")]
        public static partial void WordsSegmented(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Text: {Count} sentences (UAX #29)")]
        public static partial void SentencesSegmented(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Text: {Count} word entities emitted")]
        public static partial void WordEntitiesEmitted(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Text: {Count} sentence entities emitted")]
        public static partial void SentenceEntitiesEmitted(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Text: complete — {Entities} entities, {Edges} edges, {Batches} batches")]
        public static partial void DecompositionComplete(ILogger logger, long entities, long edges, int batches);
    }
}
