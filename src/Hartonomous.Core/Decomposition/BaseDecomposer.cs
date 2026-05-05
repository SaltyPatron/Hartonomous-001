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

    /// <summary>
    /// Hash an atomic identifier string — a structured, non-natural-language
    /// token whose UTF-8 bytes ARE the canonical content (e.g. a WordNet
    /// synset offset like "02084071-n", an ISO 639-3 code like "eng", a
    /// language registry code). The token's bytes are the entity's identity;
    /// there is no compositional decomposition to perform.
    ///
    /// **Do NOT call this on user-visible natural-language text.** Sentences,
    /// paragraphs, glosses, captions, transcripts, model config JSON values,
    /// and any other natural-language string MUST be routed through
    /// <see cref="Hartonomous.Core.Text.CanonicalTextDecomposer"/>. That
    /// produces the canonical text-AST hash so the same content from any
    /// source (Tatoeba, WordNet examples, Wiktionary citations, user
    /// prompts, model outputs) collapses to ONE <c>text_composition</c>
    /// entity. Bypassing the text decomposer with this method on natural
    /// language produces phantom duplicate entities and breaks
    /// seed-uses-core (AP-9).
    ///
    /// If you are unsure whether your string is an atomic identifier or
    /// user-visible content, route it through CanonicalTextDecomposer.
    /// </summary>
    public static byte[] ComputeAtomicStringHash(string atomicIdentifier)
        => Blake3.Hash(Encoding.UTF8.GetBytes(atomicIdentifier).AsSpan());

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

    // ── Streaming sink helpers (Phase D) ─────────────────────────────────
    // Decomposers in the streaming-pipeline migration use these to emit
    // records one-at-a-time to an IRecordSink. No batch accumulation in
    // the decomposer; backpressure is the sink's bounded channel filling.
    //
    // These helpers are additive — the old IIngestionBatch path remains
    // until every decomposer is migrated (task F1). During the transition,
    // a decomposer may use either path; the orchestrator picks based on
    // which DecomposeCoreAsync overload it overrides.

    /// <summary>
    /// Emit one entity into the streaming sink. Returns the EntityHandle so
    /// downstream emissions (edges that reference this entity, junctions on
    /// it, physicality, sequences) can carry the (type, hash) FK around
    /// without recomputing.
    /// </summary>
    protected static async ValueTask<EntityHandle> EmitEntityAsync(
        IRecordSink sink,
        byte[] hash,
        string entityTypeCode,
        string provenanceCode,
        CancellationToken ct)
    {
        await sink.EmitAsync(new EntityRecord(entityTypeCode, hash, provenanceCode), ct);
        return new EntityHandle(hash, entityTypeCode);
    }

    /// <summary>
    /// Emit one edge plus its members. Computes EdgeHash from the role-ordered
    /// participant hashes (matching the on-disk substrate.edge identity
    /// computation). Members are emitted as separate EdgeMemberRecords; the
    /// sink routes them to a different staging table than the EdgeRecord, so
    /// the order of emission within this method preserves "edge first, then
    /// members" — the background drain worker drains substrate.staging_edge
    /// before substrate.staging_edge_member to keep the composite-FK
    /// reachable when the member rows land.
    /// </summary>
    protected static async ValueTask EmitEdgeAsync(
        IRecordSink sink,
        string edgeTypeCode,
        string provenanceCode,
        int edgeTypeId,
        IReadOnlyList<EdgeMemberSpec> members,
        CancellationToken ct)
    {
        // Sort by Position so EdgeHash is deterministic regardless of caller
        // emission order (matches the AddEdge path's behavior in
        // IngestionBatch).
        EdgeMemberSpec[] sorted = new EdgeMemberSpec[members.Count];
        for (int i = 0; i < members.Count; i++)
        {
            sorted[i] = members[i];
        }
        System.Array.Sort(sorted, (a, b) => a.Position.CompareTo(b.Position));

        byte[][] orderedHashes = new byte[sorted.Length][];
        for (int j = 0; j < sorted.Length; j++)
        {
            orderedHashes[j] = sorted[j].Entity.Hash;
        }
        byte[] edgeHash = ComputeEdgeHash(edgeTypeId, orderedHashes);

        await sink.EmitAsync(new EdgeRecord(edgeTypeCode, edgeHash, provenanceCode), ct);
        for (int j = 0; j < sorted.Length; j++)
        {
            await sink.EmitAsync(new EdgeMemberRecord(
                edgeTypeCode,
                edgeHash,
                sorted[j].Entity.Hash,
                sorted[j].RoleCode,
                sorted[j].Position), ct);
        }
    }

    /// <summary>
    /// Emit one junction row into the streaming sink. Mu is non-null only
    /// for Glicko-bearing junctions (entity_pos, entity_sense, pattern_deprel).
    /// </summary>
    protected static ValueTask EmitJunctionAsync(
        IRecordSink sink,
        string junctionTable,
        EntityHandle entity,
        int referenceId,
        double? mu,
        CancellationToken ct)
        => sink.EmitAsync(new JunctionRecord(
            junctionTable, entity.Hash, referenceId, mu), ct);

    /// <summary>
    /// Emit one physicality row. The Wkb bytes are the binary WKB encoding
    /// of the geometry (POINTZM, LINESTRINGZM, MULTILINESTRINGZM, etc.) —
    /// see PostGisWkbBuilder. ContentHash is BLAKE3 of the WKB so identical
    /// geometries deduplicate at the substrate level.
    /// </summary>
    protected static ValueTask EmitPhysicalityAsync(
        IRecordSink sink,
        string physicalityTypeCode,
        EntityHandle entity,
        byte[] wkb,
        CancellationToken ct)
    {
        byte[] contentHash = Blake3.Hash(wkb.AsSpan());
        return sink.EmitAsync(new PhysicalityRecord(
            physicalityTypeCode,
            entity.Hash,
            contentHash,
            wkb), ct);
    }

    /// <summary>
    /// Emit one sequence row. Composition ordering — parent contains child at
    /// ordinal position N for RleCount consecutive positions (RLE preserves
    /// refrains: "the the the" stores once with rle_count=3, not 3 rows).
    /// </summary>
    protected static ValueTask EmitSequenceAsync(
        IRecordSink sink,
        EntityHandle parent,
        int ordinal,
        EntityHandle child,
        int rleCount,
        CancellationToken ct)
        => sink.EmitAsync(new SequenceRecord(
            parent.Hash,
            ordinal,
            child.Hash,
            rleCount), ct);

    /// <summary>
    /// Emit one entity_significance row with an initial Mu. Sigma, volatility,
    /// games default at the substrate side.
    /// </summary>
    protected static ValueTask EmitEntitySignificanceAsync(
        IRecordSink sink,
        EntityHandle entity,
        string contextTypeCode,
        double initialMu,
        CancellationToken ct)
        => sink.EmitAsync(new EntitySignificanceRecord(
            contextTypeCode, entity.Hash, initialMu), ct);

    /// <summary>
    /// Emit one entity_model_source lineage row.
    /// </summary>
    protected static ValueTask EmitEntityModelSourceAsync(
        IRecordSink sink,
        EntityHandle entity,
        long modelSourceId,
        CancellationToken ct)
        => sink.EmitAsync(new EntityModelSourceRecord(
            entity.Hash, modelSourceId), ct);

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

    // EmitWordFormMerkle removed — replaced by Hartonomous.Core.Text.CanonicalTextDecomposer.Emit
    // and the BaseDecomposer.EmitText helper. See docs/specs/text-decomposer-unification.md.

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

    // EmitLemmaMaybeCompound and EmitLexicalizedCompound removed — replaced by
    // Hartonomous.Core.Text.CanonicalTextDecomposer.Emit and the BaseDecomposer.EmitText
    // helper. Compound handling (lexicalized_compound edges between the whole
    // form and its constituent word_forms) is now caller-controlled: emit the
    // whole as a lemma via EmitText, emit each constituent as a word_form via
    // EmitText, then call batch.AddEdge("lexicalized_compound", ...) explicitly.
    // See docs/specs/text-decomposer-unification.md.

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

    protected IReadOnlyCollection<string>? LanguageFilter => _config.LanguageFilter;

    /// <summary>
    /// THE single text-emission helper for all decomposers. Routes
    /// <paramref name="text"/> through <see cref="Hartonomous.Core.Text.CanonicalTextDecomposer.Emit"/>
    /// — the substrate's canonical text decomposer per
    /// <c>docs/specs/text-decomposer-unification.md</c>. Replaces every prior
    /// per-decomposer text-emit path (<see cref="EmitWordFormMerkle"/>,
    /// <see cref="EmitLemmaMaybeCompound"/>, <see cref="EmitLexicalizedCompound"/>,
    /// <c>TextSegmentationEmitter.EmitTextComposition</c>,
    /// <c>TextDecomposer.IngestUtf8DocumentIntoBatch</c>). Same content from
    /// any decomposer collapses to the same hash — content IS the entity.
    /// </summary>
    /// <param name="batch">Producer batch.</param>
    /// <param name="text">UTF-16 string. Encoded to UTF-8 bytes for hashing.</param>
    /// <param name="codepointProperties">UAX #29 properties source; substrate-table-driven.</param>
    /// <param name="topEntityType">e.g. <c>"text_composition"</c>, <c>"lemma"</c>, <c>"language_name"</c>.</param>
    /// <param name="trustMu">Per-call <c>source_authority</c> arena prior. See
    /// <c>substrate.provenance.initial_mu</c> for canonical values.</param>
    protected (Hartonomous.Core.Ingestion.EntityHandle Handle, byte[] Hash, (double X, double Y, double Z, double M) Centroid)
        EmitText(
            Hartonomous.Core.Ingestion.IIngestionBatch batch,
            string text,
            Hartonomous.Core.Text.Segmentation.ICodepointProperties codepointProperties,
            string topEntityType,
            double trustMu)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        // Routes through libhartonomous's in-process native pipeline
        // (hartonomous_text_decompose + UCD blob), NOT a per-text Npgsql
        // roundtrip. The codepointProperties argument is ignored — the
        // native walker consults the same UCD 17.0.0 tables compiled into
        // libhartonomous. Kept in the signature so existing call sites
        // don't change shape.
        Hartonomous.Core.Text.TextDecomposeResult r =
            Hartonomous.Core.Text.SubstrateTextDecomposer.EmitStatic(
                batch, utf8,
                new Hartonomous.Core.Text.TextDecomposeOptions(
                    ProvenanceCode: ProvenanceCode,
                    TopEntityType: topEntityType,
                    TrustMu: trustMu));
        return (r.RootHandle, r.RootHash, r.RootCentroid);
    }

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
