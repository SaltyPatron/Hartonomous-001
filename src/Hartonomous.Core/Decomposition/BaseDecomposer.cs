using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Errors;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Core.Decomposition;

public abstract partial class BaseDecomposer : IDecomposer
{
    private readonly DecomposerConfig _config;
    private readonly ILogger _logger;

    protected ILogger Logger => _logger;

    public abstract string ProvenanceCode { get; }
    public abstract string DisplayName { get; }
    public abstract IReadOnlyList<Phase> Phases { get; }

    protected BaseDecomposer(DecomposerConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    public virtual Task ValidateSourceAsync(CancellationToken ct)
    {
        foreach (string path in GetSourcePaths())
        {
            if (!Path.Exists(path))
            {
                throw new SourceValidationException($"[{ProvenanceCode}] Source not found: {path}");
            }
        }
        return Task.CompletedTask;
    }

    public async Task DecomposeAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        Log.DecompositionStarting(_logger, DisplayName);
        await DecomposeCoreAsync(pipeline, reporter, ct);
        Log.DecompositionCompleted(_logger, DisplayName);
    }

    protected abstract Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct);

    protected abstract IReadOnlyList<string> GetSourcePaths();

    protected static byte[] ComputeHash(ReadOnlySpan<byte> content) => Blake3.Hash(content);

    protected static byte[] ComputeHash(string content)
        => Blake3.Hash(Encoding.UTF8.GetBytes(content).AsSpan());

    protected static byte[] ComputeMerkleHash(ReadOnlySpan<byte[]> childHashes)
    {
        byte[] concat = new byte[childHashes.Length * Blake3.HashLen];
        for (int i = 0; i < childHashes.Length; i++)
        {
            childHashes[i].CopyTo(concat.AsSpan(i * Blake3.HashLen));
        }
        return Merkle.Hash(concat.AsSpan());
    }

    protected static byte[] ComputeEdgeHash(int edgeTypeId, ReadOnlySpan<byte[]> participantHashes)
    {
        byte[] buffer = new byte[4 + participantHashes.Length * 32];
        BitConverter.TryWriteBytes(buffer, edgeTypeId);
        for (int i = 0; i < participantHashes.Length; i++)
        {
            participantHashes[i].CopyTo(buffer.AsSpan(4 + i * 32));
        }
        return ComputeHash(buffer.AsSpan());
    }

    protected static async Task SubmitAndReportAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        IIngestionBatch batch,
        ProgressSnapshot snapshot,
        CancellationToken ct)
    {
        await pipeline.SubmitBatchAsync(batch, ct);
        await reporter.ReportAsync(snapshot, ct);
    }

    /// <summary>
    /// Submit <paramref name="batch"/> and report a <see cref="ProgressSnapshot"/> built from the
    /// decomposer's own <see cref="ProvenanceCode"/>. This is the single authoritative progress-reporting
    /// path for every seed decomposer — do not re-implement per-decomposer wrappers.
    /// </summary>
    protected async Task ReportProgressAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        IIngestionBatch batch,
        long entityCount,
        long edgeCount,
        int batchNum,
        string currentFile,
        CancellationToken ct,
        string phase = "ingestion")
    {
        await SubmitAndReportAsync(pipeline, reporter, batch,
            new ProgressSnapshot
            {
                DecomposerCode = ProvenanceCode,
                CurrentPhase = phase,
                EntitiesCreated = entityCount,
                EdgesCreated = edgeCount,
                CurrentFile = currentFile,
                CurrentBatch = batchNum,
            }, ct);
    }

    /// <summary>
    /// Builds a word_form entity as a Merkle DAG: codepoints → grapheme_clusters → word_form.
    /// Creates all intermediate entities (codepoint, grapheme_cluster) and sequence relationships.
    /// Returns the word_form <see cref="EntityHandle"/> and its Merkle hash.
    /// <para>
    /// Every decomposer that needs a word_form entity calls this single method. UAX #29 grapheme
    /// cluster segmentation ensures that combining characters (accents, Devanagari conjuncts, emoji
    /// sequences) are grouped correctly. Same surface form from any decomposer produces the same
    /// Merkle hash → one entity.
    /// </para>
    /// </summary>
    protected static (EntityHandle Handle, byte[] Hash) EmitWordFormMerkle(
        IIngestionBatch batch,
        string form,
        string entityType = "word_form")
    {
        // Most words have ≤64 grapheme clusters and ≤4 codepoints per cluster.
        // Rent pooled arrays to avoid per-call List allocations.
        const int InitialCapacity = 64;

        byte[][] gcHashBuf = ArrayPool<byte[]>.Shared.Rent(InitialCapacity);
        EntityHandle[] gcHandleBuf = ArrayPool<EntityHandle>.Shared.Rent(InitialCapacity);
        int gcCount = 0;

        byte[][] cpHashBuf = ArrayPool<byte[]>.Shared.Rent(8);
        EntityHandle[] cpHandleBuf = ArrayPool<EntityHandle>.Shared.Rent(8);

        try
        {
            TextElementEnumerator tee = StringInfo.GetTextElementEnumerator(form);
            while (tee.MoveNext())
            {
                string gc = tee.GetTextElement();

                // Codepoints within this grapheme cluster.
                int cpCount = 0;
                foreach (Rune rune in gc.EnumerateRunes())
                {
                    if (cpCount >= cpHashBuf.Length)
                    {
                        GrowPooledArray(ref cpHashBuf, cpCount);
                        GrowPooledArray(ref cpHandleBuf, cpCount);
                    }
                    byte[] cpHash = HashCodepoint(rune.Value);
                    EntityHandle cpHandle = batch.AddEntity(cpHash, "codepoint");
                    cpHashBuf[cpCount] = cpHash;
                    cpHandleBuf[cpCount] = cpHandle;
                    cpCount++;
                }

                // Grapheme cluster = Merkle(codepoint hashes).
                byte[] gcHash = ComputeMerkleHash(cpHashBuf.AsSpan(0, cpCount));
                EntityHandle gcHandle = batch.AddEntity(gcHash, "grapheme_cluster");

                // Sequence: grapheme_cluster → codepoints in order.
                for (int i = 0; i < cpCount; i++)
                {
                    batch.AddSequence(gcHandle, cpHandleBuf[i], i, 1);
                }

                if (gcCount >= gcHashBuf.Length)
                {
                    GrowPooledArray(ref gcHashBuf, gcCount);
                    GrowPooledArray(ref gcHandleBuf, gcCount);
                }
                gcHashBuf[gcCount] = gcHash;
                gcHandleBuf[gcCount] = gcHandle;
                gcCount++;
            }

            // Word form = Merkle(grapheme cluster hashes).
            byte[] wfHash = ComputeMerkleHash(gcHashBuf.AsSpan(0, gcCount));
            EntityHandle wfHandle = batch.AddEntity(wfHash, entityType);

            // Sequence: word_form → grapheme clusters in order.
            for (int i = 0; i < gcCount; i++)
            {
                batch.AddSequence(wfHandle, gcHandleBuf[i], i, 1);
            }

            return (wfHandle, wfHash);
        }
        finally
        {
            ArrayPool<byte[]>.Shared.Return(gcHashBuf);
            ArrayPool<EntityHandle>.Shared.Return(gcHandleBuf);
            ArrayPool<byte[]>.Shared.Return(cpHashBuf);
            ArrayPool<EntityHandle>.Shared.Return(cpHandleBuf);
        }
    }

    /// <summary>
    /// Grows a pooled array by returning the old one and renting a larger one.
    /// </summary>
    private static void GrowPooledArray<T>(ref T[] array, int currentCount)
    {
        T[] larger = ArrayPool<T>.Shared.Rent(array.Length * 2);
        Array.Copy(array, larger, currentCount);
        ArrayPool<T>.Shared.Return(array);
        array = larger;
    }

    /// <summary>
    /// Emit physicality geometry for a surface-form string: LINESTRINGZM contour when ≥2 codepoints,
    /// POINTZM s3_position for a single codepoint, nothing for empty strings. This is the single
    /// authoritative contour-physicality path; every decomposer calls it, none reimplement it.
    /// </summary>
    protected static void EmitContourPhysicality(IIngestionBatch batch, EntityHandle entity, string surfaceForm)
    {
        List<(double X, double Y, double Z, double M)> vertices =
            PhysicalityEmitter.SurfaceFormVertices(surfaceForm);
        if (vertices.Count >= 2)
        {
            batch.AddPhysicality(entity, "contour", PhysicalityEmitter.LineStringZmWkb(vertices));
        }
        else if (vertices.Count == 1)
        {
            (double x, double y, double z, double m) = vertices[0];
            batch.AddPhysicality(entity, "s3_position", PhysicalityEmitter.PointZmWkb(x, y, z, m));
        }
    }

    /// <summary>
    /// Emit a character sequence for a surface-form string. For each Rune in <paramref name="surfaceForm"/>
    /// the codepoint entity (content-addressed by the 4-byte big-endian codepoint value) is added to the
    /// batch and a <c>sequence</c> row is emitted with parent=<paramref name="entity"/>, child=codepoint,
    /// and position=Rune index. Enables character-level traversal at inference and content-deduplicates
    /// with ISO 639-3, WordNet, OMW, and every other decomposer that emits codepoints.
    /// </summary>
    protected static void EmitSurfaceFormSequence(IIngestionBatch batch, EntityHandle entity, string surfaceForm)
    {
        if (string.IsNullOrEmpty(surfaceForm))
        {
            return;
        }
        int position = 0;
        foreach (Rune rune in surfaceForm.EnumerateRunes())
        {
            byte[] cpHash = HashCodepoint(rune.Value);
            EntityHandle cpHandle = batch.AddEntity(cpHash, "codepoint");
            batch.AddSequence(entity, cpHandle, position, 1);
            position++;
        }
    }

    /// <summary>
    /// Compute the canonical Merkle hash for a surface form string without emitting entities.
    /// This produces the same hash as <see cref="EmitWordFormMerkle"/> so that any decomposer
    /// can pre-compute a word's identity hash for dedup lookups before deciding whether to emit.
    /// </summary>
    protected static byte[] ComputeWordFormHash(string form)
    {
        List<byte[]> gcHashes = [];
        TextElementEnumerator tee = StringInfo.GetTextElementEnumerator(form);
        while (tee.MoveNext())
        {
            string gc = tee.GetTextElement();
            List<byte[]> cpHashes = [];
            foreach (Rune rune in gc.EnumerateRunes())
            {
                cpHashes.Add(HashCodepoint(rune.Value));
            }
            gcHashes.Add(ComputeMerkleHash(cpHashes.ToArray().AsSpan()));
        }
        return ComputeMerkleHash(gcHashes.ToArray().AsSpan());
    }

    /// <summary>
    /// Content-hash for a Unicode codepoint. 4 big-endian bytes → BLAKE3. Shared across every
    /// decomposer so that "a" from ISO 639-3, "a" from WordNet, and "a" from Wiktionary all
    /// deduplicate to the same codepoint entity.
    /// </summary>
    protected static byte[] HashCodepoint(int cpValue)
    {
        Span<byte> cpBytes = stackalloc byte[4];
        cpBytes[0] = (byte)(cpValue >> 24);
        cpBytes[1] = (byte)(cpValue >> 16);
        cpBytes[2] = (byte)(cpValue >> 8);
        cpBytes[3] = (byte)cpValue;
        return ComputeHash(cpBytes);
    }

    protected int BatchSize => _config.BatchSize;

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Starting decomposition: {Decomposer}")]
        public static partial void DecompositionStarting(ILogger logger, string decomposer);

        [LoggerMessage(Level = LogLevel.Information, Message = "Completed decomposition: {Decomposer}")]
        public static partial void DecompositionCompleted(ILogger logger, string decomposer);
    }
}
