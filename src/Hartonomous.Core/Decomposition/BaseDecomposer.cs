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

    public static byte[] ComputeHash(ReadOnlySpan<byte> content) => Blake3.Hash(content);

    public static byte[] ComputeHash(string content)
        => Blake3.Hash(Encoding.UTF8.GetBytes(content).AsSpan());

    public static byte[] ComputeMerkleHash(ReadOnlySpan<byte[]> childHashes)
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
    /// Builds a word_form entity as a Merkle DAG: codepoints → grapheme_clusters → word_form,
    /// emitting recursive child-centroid trajectories at each tier per
    /// <c>.claude/rules/25-physicality-4d.md § "Recursive centroid composition"</c>:
    /// <list type="bullet">
    ///   <item>codepoint atom → POINTZM at its <c>CodepointS3Position</c>; centroid = that point.</item>
    ///   <item>grapheme_cluster (multi-codepoint) → LINESTRINGZM whose vertices are its
    ///     constituent codepoint centroids in order; centroid = mean of those vertices.
    ///     Single-codepoint graphemes ARE the codepoint and reuse its centroid.</item>
    ///   <item>word_form → LINESTRINGZM whose vertices are grapheme_cluster centroids in
    ///     order; centroid = mean of those vertices.</item>
    /// </list>
    /// The trajectory IS the geometry of the entity — Fréchet/Hausdorff queries hit
    /// <c>substrate.physicality.geom</c> directly. Returning the centroid lets a parent
    /// composition (text_composition, document, lemma) build its own trajectory through
    /// THIS word_form's centroid as one of its vertices, without recomputing.
    /// </summary>
    protected static (EntityHandle Handle, byte[] Hash, (double X, double Y, double Z, double M) Centroid)
        EmitWordFormMerkle(
            IIngestionBatch batch,
            string form,
            string entityType = "word_form")
    {
        const int InitialCapacity = 64;

        byte[][] gcHashBuf = ArrayPool<byte[]>.Shared.Rent(InitialCapacity);
        EntityHandle[] gcHandleBuf = ArrayPool<EntityHandle>.Shared.Rent(InitialCapacity);
        (double X, double Y, double Z, double M)[] gcCentroidBuf =
            ArrayPool<(double, double, double, double)>.Shared.Rent(InitialCapacity);
        int gcCount = 0;

        byte[][] cpHashBuf = ArrayPool<byte[]>.Shared.Rent(8);
        EntityHandle[] cpHandleBuf = ArrayPool<EntityHandle>.Shared.Rent(8);
        (double X, double Y, double Z, double M)[] cpCentroidBuf =
            ArrayPool<(double, double, double, double)>.Shared.Rent(8);

        try
        {
            TextElementEnumerator tee = StringInfo.GetTextElementEnumerator(form);
            while (tee.MoveNext())
            {
                string gc = tee.GetTextElement();

                int cpCount = 0;
                foreach (Rune rune in gc.EnumerateRunes())
                {
                    if (cpCount >= cpHashBuf.Length)
                    {
                        GrowPooledArray(ref cpHashBuf, cpCount);
                        GrowPooledArray(ref cpHandleBuf, cpCount);
                        GrowPooledArray(ref cpCentroidBuf, cpCount);
                    }
                    byte[] cpHash = HashCodepoint(rune.Value);
                    EntityHandle cpHandle = batch.AddEntity(cpHash, "codepoint");
                    (double cx, double cy, double cz, double cm) =
                        PhysicalityEmitter.CodepointS3Position(rune.Value);
                    // Codepoint atom: POINTZM at its S³ position. Centroid = the point.
                    batch.AddPhysicalityPoint4d(cpHandle, "s3_position", cx, cy, cz, cm);
                    cpHashBuf[cpCount] = cpHash;
                    cpHandleBuf[cpCount] = cpHandle;
                    cpCentroidBuf[cpCount] = (cx, cy, cz, cm);
                    cpCount++;
                }

                byte[] gcHash = ComputeMerkleHash(cpHashBuf.AsSpan(0, cpCount));
                EntityHandle gcHandle = batch.AddEntity(gcHash, "grapheme_cluster");

                (double X, double Y, double Z, double M) gcCentroid;
                if (cpCount == 1)
                {
                    // Single-codepoint grapheme is the codepoint geometrically.
                    gcCentroid = cpCentroidBuf[0];
                }
                else
                {
                    // grapheme_cluster trajectory = LINESTRINGZM through codepoint centroids.
                    (double, double, double, double)[] verts =
                        new (double, double, double, double)[cpCount];
                    Array.Copy(cpCentroidBuf, verts, cpCount);
                    batch.AddPhysicalityLineString4d(gcHandle, "contour", verts.AsSpan());
                    gcCentroid = MeanCentroid(verts.AsSpan());
                }

                // Emit substrate.sequence rows: grapheme_cluster → codepoints
                // in left-to-right order, 1-based ordinal. Repetitions
                // ("aaa") are preserved by distinct ordinals pointing to the
                // same codepoint entity. Single-codepoint graphemes ARE the
                // codepoint geometrically, but still record one sequence row
                // so the walk surface is uniform.
                EmitSequence(batch, gcHandle, cpHandleBuf.AsSpan(0, cpCount));

                if (gcCount >= gcHashBuf.Length)
                {
                    GrowPooledArray(ref gcHashBuf, gcCount);
                    GrowPooledArray(ref gcHandleBuf, gcCount);
                    GrowPooledArray(ref gcCentroidBuf, gcCount);
                }
                gcHashBuf[gcCount] = gcHash;
                gcHandleBuf[gcCount] = gcHandle;
                gcCentroidBuf[gcCount] = gcCentroid;
                gcCount++;
            }

            byte[] wfHash = ComputeMerkleHash(gcHashBuf.AsSpan(0, gcCount));
            EntityHandle wfHandle = batch.AddEntity(wfHash, entityType);

            (double X, double Y, double Z, double M) wfCentroid;
            if (gcCount == 1)
            {
                wfCentroid = gcCentroidBuf[0];
            }
            else
            {
                (double, double, double, double)[] verts =
                    new (double, double, double, double)[gcCount];
                Array.Copy(gcCentroidBuf, verts, gcCount);
                // word_form trajectory = LINESTRINGZM through grapheme_cluster centroids.
                batch.AddPhysicalityLineString4d(wfHandle, "contour", verts.AsSpan());
                wfCentroid = MeanCentroid(verts.AsSpan());
            }

            // Emit substrate.sequence rows: word_form → grapheme_clusters in
            // left-to-right order. The substrate.recompose_text recursive walk
            // bottoms out at codepoint leaves through this sequence chain.
            EmitSequence(batch, wfHandle, gcHandleBuf.AsSpan(0, gcCount));

            return (wfHandle, wfHash, wfCentroid);
        }
        finally
        {
            ArrayPool<byte[]>.Shared.Return(gcHashBuf);
            ArrayPool<EntityHandle>.Shared.Return(gcHandleBuf);
            ArrayPool<(double, double, double, double)>.Shared.Return(gcCentroidBuf);
            ArrayPool<byte[]>.Shared.Return(cpHashBuf);
            ArrayPool<EntityHandle>.Shared.Return(cpHandleBuf);
            ArrayPool<(double, double, double, double)>.Shared.Return(cpCentroidBuf);
        }
    }

    /// <summary>
    /// Emit substrate.sequence rows recording the ordered children of
    /// <paramref name="parent"/> as supplied in <paramref name="children"/>,
    /// 1-based ordinals. Contiguous runs of the same child are NOT
    /// auto-collapsed here — pass distinct rows; if a decomposer wants RLE
    /// compression for a long refrain it should call
    /// <see cref="IIngestionBatch.AddSequence"/> directly with rleCount &gt; 1.
    /// Repeated entities at distinct ordinals (the "green eggs and ham × 3"
    /// case) are preserved exactly because the sequence row PK is
    /// (parent_type, parent_hash, ordinal) — repeats don't collide.
    /// </summary>
    public static void EmitSequence(
        IIngestionBatch batch,
        EntityHandle parent,
        ReadOnlySpan<EntityHandle> children)
    {
        for (int i = 0; i < children.Length; i++)
        {
            batch.AddSequence(parent, ordinal: i + 1, child: children[i], rleCount: 1);
        }
    }

    /// <summary>
    /// 4D arithmetic mean of an ordered vertex span — the recursive-centroid law
    /// from <c>.claude/rules/25-physicality-4d.md</c>. Routes through the native
    /// <c>hartonomous_centroid_4d</c> primitive via <see cref="S3Geometry.Mean4d"/>
    /// per <c>.claude/rules/30-native-and-determinism.md</c> (no C# duplication
    /// of numerical primitives — keeps reductions cross-platform deterministic).
    /// </summary>
    public static (double X, double Y, double Z, double M) MeanCentroid(
        ReadOnlySpan<(double X, double Y, double Z, double M)> vertices)
    {
        if (vertices.Length == 0)
        {
            throw new ArgumentException("MeanCentroid requires at least one vertex.", nameof(vertices));
        }
        // Pack 4D tuples into the contiguous double[] layout the native call expects.
        double[] flat = new double[vertices.Length * 4];
        for (int i = 0; i < vertices.Length; i++)
        {
            flat[i * 4 + 0] = vertices[i].X;
            flat[i * 4 + 1] = vertices[i].Y;
            flat[i * 4 + 2] = vertices[i].Z;
            flat[i * 4 + 3] = vertices[i].M;
        }
        Span<double> result = stackalloc double[4];
        S3Geometry.Mean4d(flat, vertices.Length, result);
        return (result[0], result[1], result[2], result[3]);
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
    /// DELETED: emitting a contour through an entity's raw codepoints is wrong
    /// at every tier above codepoint. A word_form's geometry is a LINESTRINGZM
    /// through its <em>grapheme_cluster centroids</em>; a sentence's geometry is
    /// through its <em>word_form centroids</em>; a document's geometry is through
    /// its <em>text_composition centroids</em> — the recursive-centroid law
    /// from <c>.claude/rules/25-physicality-4d.md</c>. The proper emission path
    /// runs inside <see cref="EmitWordFormMerkle"/>, <see cref="EmitLemmaMaybeCompound"/>,
    /// <see cref="EmitLexicalizedCompound"/>, and (for sentence/paragraph/document
    /// tiers) inside the text decomposer's composition pass. Decomposers MUST NOT
    /// emit contour physicality on higher-tier entities themselves.
    ///
    /// This stub remains so old callers fail loudly during the migration. Remove
    /// the call; rely on the structured emission methods to populate physicality
    /// at every tier.
    /// </summary>
    [Obsolete("Use EmitWordFormMerkle / EmitLemmaMaybeCompound / EmitLexicalizedCompound — they emit recursive child-centroid trajectories at every tier. Sentence and document tiers are handled inside the text decomposer's composition pass.", error: true)]
    public static void EmitContourPhysicality(IIngestionBatch batch, EntityHandle entity, string surfaceForm)
        => throw new InvalidOperationException(
            "EmitContourPhysicality removed. Use the recursive child-centroid emission path.");

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
            position++;
        }
    }

    /// <summary>
    /// Emit a lemma that may be a lexicalized compound. If <paramref name="surfaceForm"/>
    /// contains <c>_</c> or U+0020 SPACE, splits on that boundary and routes through
    /// <see cref="EmitLexicalizedCompound"/> so both the whole and each constituent
    /// word_form become first-class Merkle entities. Otherwise routes through
    /// <see cref="EmitWordFormMerkle"/> for the simple monolexical case. Returns
    /// the whole-form handle and Merkle hash either way.
    /// <para>
    /// Splitting on space + underscore covers WordNet ("high_rise"), OMW (same),
    /// and Wiktionary ("ice cream", "open up", "rock 'n' roll" — though apostrophe
    /// is not a separator, so that one stays a single word_form). Empty parts are
    /// dropped (e.g. leading or trailing separators).
    /// </para>
    /// </summary>
    protected static (EntityHandle Handle, byte[] Hash, (double X, double Y, double Z, double M) Centroid)
        EmitLemmaMaybeCompound(
            IIngestionBatch batch,
            string surfaceForm,
            string provenanceCode)
    {
        if (surfaceForm.IndexOf('_') < 0 && surfaceForm.IndexOf(' ') < 0)
        {
            return EmitWordFormMerkle(batch, surfaceForm, "lemma");
        }
        string[] rawParts = surfaceForm.Split(['_', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (rawParts.Length < 2)
        {
            return EmitWordFormMerkle(batch, surfaceForm, "lemma");
        }
        return EmitLexicalizedCompound(batch, surfaceForm, rawParts, provenanceCode, "lemma");
    }

    /// <summary>
    /// Emit a lexicalized compound (semantic regression case #2 — "highrise"):
    /// the whole surface form AND each constituent word_form get their own
    /// Merkle entities, joined by a single n-ary <c>lexicalized_compound</c>
    /// edge with the whole as <c>source</c> @ position 0 and each part as
    /// <c>target</c> @ positions 1..N in left-to-right order.
    /// <para>
    /// This preserves both retrieval paths required by inference:
    /// (a) the whole-form path — "high_rise" as a single lemma carrying
    ///     its own senses, glosses, and inflections; and
    /// (b) the parts-composition path — "high" and "rise" as independent
    ///     word_form entities that converge by Merkle identity with their
    ///     monomorphemic occurrences elsewhere in the substrate.
    /// </para>
    /// <para>
    /// The whole's surface form is hashed via the standard Merkle path over
    /// its codepoint sequence (preserving any internal separator like
    /// underscore or space as a real codepoint child), so the whole entity
    /// converges with any other Merkle-hashed occurrence of the same string.
    /// </para>
    /// </summary>
    /// <param name="batch">Batch to append entities and edges to.</param>
    /// <param name="surfaceForm">Whole-form surface string (e.g. "high_rise" or "ice cream").</param>
    /// <param name="parts">Constituent strings in left-to-right order (e.g. ["high", "rise"]).</param>
    /// <param name="provenanceCode">Provenance code for the lexicalized_compound edge.</param>
    /// <param name="wholeEntityType">Entity type of the whole (default "lemma").</param>
    /// <returns>The whole entity's handle and Merkle hash.</returns>
    protected static (EntityHandle Handle, byte[] Hash, (double X, double Y, double Z, double M) Centroid)
        EmitLexicalizedCompound(
            IIngestionBatch batch,
            string surfaceForm,
            IReadOnlyList<string> parts,
            string provenanceCode,
            string wholeEntityType = "lemma")
    {
        if (parts.Count < 2)
        {
            throw new ArgumentException(
                $"EmitLexicalizedCompound requires ≥2 parts; got {parts.Count} for '{surfaceForm}'.",
                nameof(parts));
        }

        (EntityHandle wholeHandle, byte[] wholeHash, (double X, double Y, double Z, double M) wholeCentroid) =
            EmitWordFormMerkle(batch, surfaceForm, wholeEntityType);

        EdgeMemberSpec[] members = new EdgeMemberSpec[parts.Count + 1];
        members[0] = new EdgeMemberSpec(wholeHandle, "source", 0);
        for (int i = 0; i < parts.Count; i++)
        {
            (EntityHandle partHandle, byte[] _, (double, double, double, double) _) =
                EmitWordFormMerkle(batch, parts[i], "word_form");
            members[i + 1] = new EdgeMemberSpec(partHandle, "target", (short)(i + 1));
        }

        batch.AddEdge("lexicalized_compound", provenanceCode, members.AsSpan());
        return (wholeHandle, wholeHash, wholeCentroid);
    }

    /// <summary>
    /// Compute the canonical Merkle hash for a surface form string without emitting entities.
    /// This produces the same hash as <see cref="EmitWordFormMerkle"/> so that any decomposer
    /// can pre-compute a word's identity hash for dedup lookups before deciding whether to emit.
    /// </summary>
    public static byte[] ComputeWordFormHash(string form)
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
    public static byte[] HashCodepoint(int cpValue)
    {
        Span<byte> cpBytes = stackalloc byte[4];
        cpBytes[0] = (byte)(cpValue >> 24);
        cpBytes[1] = (byte)(cpValue >> 16);
        cpBytes[2] = (byte)(cpValue >> 8);
        cpBytes[3] = (byte)cpValue;
        return ComputeHash(cpBytes);
    }

    protected int BatchSize => _config.BatchSize;

    /// <summary>
    /// Per-decomposer ISO 639-3 allowlist resolved from <see cref="DecomposerConfig.LanguageFilter"/>.
    /// Returns true when <paramref name="languageCode"/> is in the filter, or when no filter is set
    /// (<c>null</c> = unfiltered). Multi-language seed sources (UD, OMW, Wiktionary, Tatoeba) call
    /// this at the source boundary (per-row, per-file, per-treebank, per-JSONL-entry) to bound
    /// ingestion volume per tier — T0 = English only, T1 = next batch, etc. Outbound cross-lingual
    /// edges whose target language is not allowed are dropped to avoid phantom hash-only entities.
    /// </summary>
    protected bool LanguageAllowed(string? languageCode)
    {
        if (_config.LanguageFilter is null)
        {
            return true;
        }
        if (languageCode is null)
        {
            return false;
        }
        foreach (string allowed in _config.LanguageFilter)
        {
            if (string.Equals(allowed, languageCode, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

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
