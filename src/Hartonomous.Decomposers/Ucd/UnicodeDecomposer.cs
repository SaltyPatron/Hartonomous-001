using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Compute.Common.Ucd;
using Hartonomous.Core.Data;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Geometry;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// One decomposer for the entire UCD source corpus. Replaces 16+ prior pass
/// files. Per Principle 1 (blob and substrate are siblings), per-codepoint
/// property reads route through <see cref="BlobUcdPropertyAccessor"/> against
/// the embedded UCD blob — the blob and the runtime substrate are derived from
/// the same UCD source files at codegen time, so byte-for-byte agreement is by
/// construction. Multi-codepoint files (confusables, NamedSequences, emoji
/// sequences, StandardizedVariants, CJKRadicals, IVD_Sequences, ucd.unihan)
/// are parsed inline.
///
/// Producer-pattern: CreateBatch → Add* → SubmitBatchAsync, with AP-19 bulk
/// pre-dedupe via GetExistingEntityHashesAsync before COPY. Each section
/// flushes at <see cref="BatchFlushSize"/>.
///
/// Sections emitted:
///   1. Extension catalog verification.
///   2. Reference vocabularies (general_category, script, block, break_property).
///   3. Codepoint atoms (entity + POINTZM physicality).
///   4. Codepoint properties (junction infrastructure).
///   5. Simple case edges (maps_to_lowercase / uppercase / titlecase, case_folds_to).
///   6. Full case fold edges (has_full_case_mapping for multi-cp case folds).
///   7. Decomposition edges (canonical + compatibility + canonical_composes_to).
///   8. UTS #39 confusables.
///   9. NamedSequences.
///   10. Emoji + emoji-ZWJ sequences.
///   11. Standardized variants.
///   12. CJK radical-stroke.
///   13. IVD per-collection.
///   14. Post-pass materialization validation.
///
/// Per-version Unicode attestation events across multiple staged UCD versions
/// are deferred until the build-time UCD parser (`gen_ucd_flat.c`) exposes a
/// per-version blob accessor; the current blob carries only the deployed
/// Unicode version.
/// </summary>
public sealed partial class UnicodeDecomposer : BaseDecomposer
{
    private const int BatchFlushSize = 25_000;
    private const int PreDedupeChunk = 4_096;
    private const int MaxCodepoints = 0x110000;

    private readonly string _connectionString;
    private readonly string _sourceDirectory;
    private readonly BlobUcdPropertyAccessor _ucd;

    public UnicodeDecomposer(DecomposerConfig config, ILogger<UnicodeDecomposer> logger)
        : base(config, logger)
    {
        _connectionString = config.ConnectionString;
        _sourceDirectory = config.SourceDirectory;
        _ucd = new BlobUcdPropertyAccessor();
    }

    public override string ProvenanceCode => "unicode_consortium";
    public override string DisplayName => "Unicode (UCD / UCA / UTS #37 / UTS #39 / Emoji)";
    public override IReadOnlyList<Phase> Phases => [Phase.UcdUca];

    protected override IReadOnlyList<string> GetSourcePaths()
        => string.IsNullOrWhiteSpace(_sourceDirectory) ? [] : [_sourceDirectory];

    public override Task ValidateSourceAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_sourceDirectory) && !Path.Exists(_sourceDirectory))
        {
            Log.SourceDirectoryNotFound(Logger, _sourceDirectory);
        }
        return Task.CompletedTask;
    }

    protected override async Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        await pipeline.DrainPendingAsync(ct);

        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(_connectionString);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(ct);

        await VerifyExtensionCatalogAsync(connection, reporter, ct);
        await PopulateReferenceVocabulariesAsync(connection, reporter, ct);

        long codepointAtoms = await EmitCodepointAtomsAsync(pipeline, reporter, ct);
        await pipeline.DrainPendingAsync(ct);

        // codepoint_property junction: not populated server-side. Per-codepoint
        // UAX #44 properties are available client-side via BlobUcdPropertyAccessor
        // (native blob, O(1) lookup) and server-side via the substrate.cp_* C
        // wrappers. The denormalized substrate.codepoint_property cache is
        // populated lazily by downstream phases that need it.
        long codepointProperties = 0;

        long simpleCaseEdges = await EmitSimpleCaseEdgesAsync(pipeline, reporter, ct);
        long fullCaseFoldEdges = await EmitFullCaseFoldEdgesAsync(pipeline, reporter, ct);
        long decompositionEdges = await EmitDecompositionEdgesAsync(pipeline, reporter, ct);
        long confusableEdges = await EmitConfusablesAsync(pipeline, reporter, ct);
        long namedSequenceEdges = await EmitNamedSequencesAsync(pipeline, reporter, ct);
        long emojiSequenceEdges = await EmitEmojiSequencesAsync(pipeline, reporter, ct);
        long standardizedVariantEdges = await EmitStandardizedVariantsAsync(pipeline, reporter, ct);
        long radicalStrokeEdges = await EmitRadicalStrokeAsync(pipeline, reporter, ct);
        long ivdEdges = await EmitIvdPerCollectionAsync(pipeline, reporter, ct);

        await pipeline.DrainPendingAsync(ct);
        await ValidateMaterializationAsync(connection, reporter, ct);

        Log.Materialized(
            Logger,
            codepointAtoms,
            codepointProperties,
            simpleCaseEdges,
            fullCaseFoldEdges,
            decompositionEdges,
            confusableEdges,
            namedSequenceEdges,
            emojiSequenceEdges,
            standardizedVariantEdges,
            radicalStrokeEdges,
            ivdEdges);
    }

    // ── §1 Extension catalog verification ────────────────────────────────

    private async Task VerifyExtensionCatalogAsync(
        NpgsqlConnection connection, IProgressReporter reporter, CancellationToken ct)
    {
        string version = await ExecuteScalarStringAsync(
            connection, SubstrateFunctionNames.UcdVersion, ct);
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException(
                "substrate.ucd_version() returned empty; hartonomous extension UCD catalog is not available.");
        }
        Log.ExtensionVersion(Logger, version);
        await ReportAsync(reporter, "unicode.extension_catalog", 0, 0, ct);
    }

    // ── §2 Reference vocabularies ────────────────────────────────────────
    // Server-side populate_*_from_ext path retired 2026-05-17 (Gate 1 Task
    // #22). Client-side population of substrate.general_category / script /
    // block / break_property reference tables requires new native exports
    // to enumerate the uc_general_category_name[] / uc_script_name[] /
    // uc_block_name[] arrays from libhartonomous, which the blob accessor
    // doesn't yet expose. Until those land, the reference tables stay
    // empty; downstream decomposers route property lookups through
    // BlobUcdPropertyAccessor (native blob → C# enum codes) rather than
    // JOIN-ing against substrate.* reference rows.

    private async Task PopulateReferenceVocabulariesAsync(
        NpgsqlConnection connection, IProgressReporter reporter, CancellationToken ct)
    {
        _ = connection;
        await ReportAsync(reporter, "unicode.reference_vocabularies", 0, 0, ct);
    }

    // ── §3 Codepoint atoms ───────────────────────────────────────────────

    private async Task<long> EmitCodepointAtomsAsync(
        IIngestionPipeline pipeline, IProgressReporter reporter, CancellationToken ct)
    {
        long entityCount = 0;
        IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);
        List<Hash32> pendingHashes = new(PreDedupeChunk);
        List<int> pendingCodepoints = new(PreDedupeChunk);

        for (int cp = 0; cp < MaxCodepoints; cp++)
        {
            ct.ThrowIfCancellationRequested();
            Hash32 hash = Blake3.HashCodepoint(cp);
            pendingHashes.Add(hash);
            pendingCodepoints.Add(cp);

            if (pendingHashes.Count >= PreDedupeChunk)
            {
                entityCount += await FlushCodepointAtomsAsync(
                    pipeline, batch, pendingHashes, pendingCodepoints, ct);

                if (batch.EntityCount >= BatchFlushSize)
                {
                    await pipeline.SubmitBatchAsync(batch, ct);
                    batch = pipeline.CreateBatch(ProvenanceCode);
                }
            }
        }

        if (pendingHashes.Count > 0)
        {
            entityCount += await FlushCodepointAtomsAsync(
                pipeline, batch, pendingHashes, pendingCodepoints, ct);
        }
        if (batch.EntityCount > 0)
        {
            await pipeline.SubmitBatchAsync(batch, ct);
        }

        Log.CodepointAtoms(Logger, entityCount);
        await ReportAsync(reporter, "unicode.codepoint_atoms", entityCount, 0, ct);
        return entityCount;
    }

    private static async Task<long> FlushCodepointAtomsAsync(
        IIngestionPipeline pipeline,
        IIngestionBatch batch,
        List<Hash32> hashes,
        List<int> codepoints,
        CancellationToken ct)
    {
        HashSet<HashKey> existing = await pipeline.GetExistingEntityHashesAsync(hashes, ct);
        long emitted = 0;
        for (int i = 0; i < hashes.Count; i++)
        {
            if (existing.Contains(new HashKey(hashes[i]))) { continue; }
            int cp = codepoints[i];
            (double x, double y, double z, double m) = PhysicalityEmitter.CodepointS3Position(cp);
            double[] point4 = [x, y, z, m];
            ulong hilbert = Hilbert.Index(point4, 16);
            batch.AddEntity(hashes[i], "codepoint", x, y, z, m, (long)hilbert);
            emitted++;
        }
        hashes.Clear();
        codepoints.Clear();
        return emitted;
    }

    // §4 Codepoint properties — deleted 2026-05-17 per Gate 1 Task #22.
    // The server-side populate_codepoint_property_range_from_ext path was
    // wrong-direction (SQL reading the client-side perf-cache blob to
    // populate substrate). Per-codepoint UAX #44 properties live in the
    // native blob, accessed via BlobUcdPropertyAccessor (C#) or
    // substrate.cp_* C wrappers (SQL). The denormalized
    // substrate.codepoint_property cache is no longer populated.

    // ── §5 Simple case edges ─────────────────────────────────────────────

    private async Task<long> EmitSimpleCaseEdgesAsync(
        IIngestionPipeline pipeline, IProgressReporter reporter, CancellationToken ct)
    {
        long edgeCount = 0;
        IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);

        for (int cp = 0; cp < MaxCodepoints; cp++)
        {
            ct.ThrowIfCancellationRequested();
            if (!_ucd.IsCodepointAvailable(cp)) { continue; }

            Hash32 srcHash = Blake3.HashCodepoint(cp);

            edgeCount += EmitSimpleCaseEdge(batch, srcHash, cp, _ucd.GetSimpleLowercase(cp), "maps_to_lowercase");
            edgeCount += EmitSimpleCaseEdge(batch, srcHash, cp, _ucd.GetSimpleUppercase(cp), "maps_to_uppercase");
            edgeCount += EmitSimpleCaseEdge(batch, srcHash, cp, _ucd.GetSimpleTitlecase(cp), "maps_to_titlecase");

            int? fold = _ucd.SimpleCaseFold(cp);
            if (fold.HasValue)
            {
                edgeCount += EmitSimpleCaseEdge(batch, srcHash, cp, fold.Value, "case_folds_to");
            }

            if (batch.EntityCount + batch.EdgeCount >= BatchFlushSize)
            {
                await pipeline.SubmitBatchAsync(batch, ct);
                batch = pipeline.CreateBatch(ProvenanceCode);
            }
        }
        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await pipeline.SubmitBatchAsync(batch, ct);
        }

        Log.SimpleCaseEdges(Logger, edgeCount);
        await ReportAsync(reporter, "unicode.case_edges", 0, edgeCount, ct);
        return edgeCount;
    }

    private long EmitSimpleCaseEdge(
        IIngestionBatch batch, Hash32 srcHash, int srcCp, int targetCp, string edgeTypeCode)
    {
        if (targetCp == 0 || targetCp == srcCp) { return 0; }
        Hash32 tgtHash = Blake3.HashCodepoint(targetCp);
        EntityHandle srcHandle = new(srcHash, "codepoint");
        EntityHandle tgtHandle = new(tgtHash, "codepoint");
        EdgeMemberSpec[] members =
        [
            new EdgeMemberSpec(srcHandle, "source", 0),
            new EdgeMemberSpec(tgtHandle, "target", 1),
        ];
        batch.AddEdge(edgeTypeCode, ProvenanceCode, members);
        return 1;
    }

    // ── §6 Full case fold edges ──────────────────────────────────────────

    private async Task<long> EmitFullCaseFoldEdgesAsync(
        IIngestionPipeline pipeline, IProgressReporter reporter, CancellationToken ct)
    {
        long edgeCount = 0;
        IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);

        for (int cp = 0; cp < MaxCodepoints; cp++)
        {
            ct.ThrowIfCancellationRequested();
            if (!_ucd.IsCodepointAvailable(cp)) { continue; }

            ReadOnlySpan<int> fold = _ucd.FullCaseFold(cp);
            if (fold.Length == 0) { continue; }

            Hash32 srcHash = Blake3.HashCodepoint(cp);
            EntityHandle srcHandle = new(srcHash, "codepoint");

            Hash32[] childHashes = new Hash32[fold.Length];
            for (int i = 0; i < fold.Length; i++) { childHashes[i] = Blake3.HashCodepoint(fold[i]); }
            Hash32 compHash = ComputeMerkleHash(childHashes);
            EntityHandle compHandle = batch.AddEntity(compHash, "text_composition");

            EdgeMemberSpec[] members =
            [
                new EdgeMemberSpec(srcHandle, "source", 0),
                new EdgeMemberSpec(compHandle, "target", 1),
            ];
            batch.AddEdge("has_full_case_mapping", ProvenanceCode, members);
            edgeCount++;

            if (batch.EntityCount + batch.EdgeCount >= BatchFlushSize)
            {
                await pipeline.SubmitBatchAsync(batch, ct);
                batch = pipeline.CreateBatch(ProvenanceCode);
            }
        }
        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await pipeline.SubmitBatchAsync(batch, ct);
        }

        Log.FullCaseFoldEdges(Logger, edgeCount);
        await ReportAsync(reporter, "unicode.full_case_mapping_edges", 0, edgeCount, ct);
        return edgeCount;
    }

    // ── §7 Decomposition edges ───────────────────────────────────────────

    private async Task<long> EmitDecompositionEdgesAsync(
        IIngestionPipeline pipeline, IProgressReporter reporter, CancellationToken ct)
    {
        long edgeCount = 0;
        IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);

        for (int cp = 0; cp < MaxCodepoints; cp++)
        {
            ct.ThrowIfCancellationRequested();
            if (!_ucd.IsCodepointAvailable(cp)) { continue; }

            byte dt = _ucd.GetDecompositionType(cp);
            // 0 = None per IUcdPropertyAccessor §"GetDecompositionType"
            if (dt == 0) { continue; }
            ReadOnlySpan<int> mapping = _ucd.GetDecompositionMapping(cp);
            if (mapping.Length == 0) { continue; }

            Hash32[] childHashes = new Hash32[mapping.Length];
            for (int i = 0; i < mapping.Length; i++) { childHashes[i] = Blake3.HashCodepoint(mapping[i]); }
            Hash32 compHash = ComputeMerkleHash(childHashes);
            EntityHandle compHandle = batch.AddEntity(compHash, "text_composition");

            // 1 = Canonical per UAX #44 §5.7.3
            string edgeCode = dt == 1 ? "has_canonical_decomposition" : "has_compatibility_decomposition";
            Hash32 srcHash = Blake3.HashCodepoint(cp);
            EntityHandle srcHandle = new(srcHash, "codepoint");
            EdgeMemberSpec[] members =
            [
                new EdgeMemberSpec(srcHandle, "source", 0),
                new EdgeMemberSpec(compHandle, "target", 1),
            ];
            batch.AddEdge(edgeCode, ProvenanceCode, members);
            edgeCount++;

            // canonical_composes_to reverse edge: canonical 2-element decompositions
            // are eligible. Composition_Exclusion property check is deferred to the
            // composition-time NFC implementation rather than encoded as a per-edge
            // skip — the substrate records all canonical mappings, NFC composition
            // queries filter excluded pairs at traversal time.
            if (edgeCode == "has_canonical_decomposition" && mapping.Length == 2)
            {
                EdgeMemberSpec[] composeMembers =
                [
                    new EdgeMemberSpec(compHandle, "source", 0),
                    new EdgeMemberSpec(srcHandle, "target", 1),
                ];
                batch.AddEdge("canonical_composes_to", ProvenanceCode, composeMembers);
                edgeCount++;
            }

            if (batch.EntityCount + batch.EdgeCount >= BatchFlushSize)
            {
                await pipeline.SubmitBatchAsync(batch, ct);
                batch = pipeline.CreateBatch(ProvenanceCode);
            }
        }
        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await pipeline.SubmitBatchAsync(batch, ct);
        }

        Log.DecompositionEdges(Logger, edgeCount);
        await ReportAsync(reporter, "unicode.decomposition_edges", 0, edgeCount, ct);
        return edgeCount;
    }

    // ── §8 Confusables ───────────────────────────────────────────────────

    private async Task<long> EmitConfusablesAsync(
        IIngestionPipeline pipeline, IProgressReporter reporter, CancellationToken ct)
    {
        string? path = ResolveSource("security", "confusables.txt");
        if (path is null) { Log.SourceMissing(Logger, "confusables.txt"); return 0; }

        long edges = 0;
        IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);
        foreach (string raw in File.ReadLines(path))
        {
            ct.ThrowIfCancellationRequested();
            string line = StripComment(raw);
            if (string.IsNullOrWhiteSpace(line)) { continue; }
            string[] parts = line.Split(';');
            if (parts.Length < 2) { continue; }
            int[] src = ParseCpSeq(parts[0]);
            int[] tgt = ParseCpSeq(parts[1]);
            if (src.Length == 0 || tgt.Length == 0) { continue; }

            EntityHandle srcComp = EmitTextCompositionFromCps(batch, src);
            EntityHandle tgtComp = EmitTextCompositionFromCps(batch, tgt);

            EdgeMemberSpec[] members =
            [
                new EdgeMemberSpec(srcComp, "source", 0),
                new EdgeMemberSpec(tgtComp, "target", 1),
            ];
            batch.AddEdge("confusable_with", ProvenanceCode, members);
            edges++;

            if (batch.EntityCount + batch.EdgeCount >= BatchFlushSize)
            {
                await pipeline.SubmitBatchAsync(batch, ct);
                batch = pipeline.CreateBatch(ProvenanceCode);
            }
        }
        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await pipeline.SubmitBatchAsync(batch, ct);
        }
        Log.ConfusableEdges(Logger, edges);
        await ReportAsync(reporter, "unicode.confusables", 0, edges, ct);
        return edges;
    }

    // ── §9 Named sequences ───────────────────────────────────────────────

    private async Task<long> EmitNamedSequencesAsync(
        IIngestionPipeline pipeline, IProgressReporter reporter, CancellationToken ct)
    {
        string? path = ResolveSource("ucd", "NamedSequences.txt");
        if (path is null) { Log.SourceMissing(Logger, "NamedSequences.txt"); return 0; }

        long edges = 0;
        IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);
        foreach (string raw in File.ReadLines(path))
        {
            ct.ThrowIfCancellationRequested();
            string line = StripComment(raw);
            if (string.IsNullOrWhiteSpace(line)) { continue; }
            int semi = line.IndexOf(';');
            if (semi <= 0) { continue; }
            string name = line.Substring(0, semi).Trim();
            int[] cps = ParseCpSeq(line.Substring(semi + 1));
            if (string.IsNullOrEmpty(name) || cps.Length == 0) { continue; }

            EntityHandle seqComp = EmitTextCompositionFromCps(batch, cps);
            Hash32 nameHash = ComputeHash(Encoding.UTF8.GetBytes(name));
            EntityHandle nameComp = batch.AddEntity(nameHash, "text_composition");

            EdgeMemberSpec[] members =
            [
                new EdgeMemberSpec(nameComp, "source", 0),
                new EdgeMemberSpec(seqComp, "target", 1),
            ];
            batch.AddEdge("has_named_sequence", ProvenanceCode, members);
            edges++;

            if (batch.EntityCount + batch.EdgeCount >= BatchFlushSize)
            {
                await pipeline.SubmitBatchAsync(batch, ct);
                batch = pipeline.CreateBatch(ProvenanceCode);
            }
        }
        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await pipeline.SubmitBatchAsync(batch, ct);
        }
        Log.NamedSequenceEdges(Logger, edges);
        await ReportAsync(reporter, "unicode.named_sequences", 0, edges, ct);
        return edges;
    }

    // ── §10 Emoji sequences ──────────────────────────────────────────────

    private async Task<long> EmitEmojiSequencesAsync(
        IIngestionPipeline pipeline, IProgressReporter reporter, CancellationToken ct)
    {
        long edges = 0;
        IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);

        (string?, string)[] sources =
        [
            (ResolveSource("emoji", "emoji-sequences.txt")
                ?? ResolveSource("ucd", "emoji", "emoji-sequences.txt"), "has_emoji_sequence"),
            (ResolveSource("emoji", "emoji-zwj-sequences.txt")
                ?? ResolveSource("ucd", "emoji", "emoji-zwj-sequences.txt"), "has_emoji_zwj_sequence"),
        ];

        foreach ((string? path, string edgeCode) in sources)
        {
            if (path is null) { continue; }
            foreach (string raw in File.ReadLines(path))
            {
                ct.ThrowIfCancellationRequested();
                string line = StripComment(raw);
                if (string.IsNullOrWhiteSpace(line)) { continue; }
                string[] parts = line.Split(';');
                if (parts.Length < 2) { continue; }
                string cpsField = parts[0].Trim();
                string name = parts.Length >= 3 ? parts[2].Trim() : "";

                if (cpsField.Contains(".."))
                {
                    int dotdot = cpsField.IndexOf("..", StringComparison.Ordinal);
                    int lo = int.Parse(cpsField.AsSpan(0, dotdot), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    int hi = int.Parse(cpsField.AsSpan(dotdot + 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    for (int cp = lo; cp <= hi; cp++)
                    {
                        edges += EmitEmojiSequence(batch, new[] { cp }, name, edgeCode);
                    }
                }
                else
                {
                    int[] cps = ParseCpSeq(cpsField);
                    if (cps.Length == 0) { continue; }
                    edges += EmitEmojiSequence(batch, cps, name, edgeCode);
                }

                if (batch.EntityCount + batch.EdgeCount >= BatchFlushSize)
                {
                    await pipeline.SubmitBatchAsync(batch, ct);
                    batch = pipeline.CreateBatch(ProvenanceCode);
                }
            }
        }
        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await pipeline.SubmitBatchAsync(batch, ct);
        }
        Log.EmojiSequenceEdges(Logger, edges);
        await ReportAsync(reporter, "unicode.emoji_sequences", 0, edges, ct);
        return edges;
    }

    private long EmitEmojiSequence(IIngestionBatch batch, int[] cps, string name, string edgeCode)
    {
        EntityHandle seqComp = EmitTextCompositionFromCps(batch, cps);
        EntityHandle nameComp;
        if (!string.IsNullOrEmpty(name))
        {
            Hash32 nameHash = ComputeHash(Encoding.UTF8.GetBytes(name));
            nameComp = batch.AddEntity(nameHash, "text_composition");
        }
        else
        {
            nameComp = seqComp;
        }
        EdgeMemberSpec[] members =
        [
            new EdgeMemberSpec(nameComp, "source", 0),
            new EdgeMemberSpec(seqComp, "target", 1),
        ];
        batch.AddEdge(edgeCode, ProvenanceCode, members);
        return 1;
    }

    // ── §11 Standardized variants ────────────────────────────────────────

    private async Task<long> EmitStandardizedVariantsAsync(
        IIngestionPipeline pipeline, IProgressReporter reporter, CancellationToken ct)
    {
        long edges = 0;
        IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);

        string?[] paths =
        [
            ResolveSource("ucd", "StandardizedVariants.txt"),
            ResolveSource("ucd", "emoji", "emoji-variation-sequences.txt"),
            ResolveSource("emoji", "emoji-variation-sequences.txt"),
        ];

        foreach (string? path in paths)
        {
            if (path is null) { continue; }
            foreach (string raw in File.ReadLines(path))
            {
                ct.ThrowIfCancellationRequested();
                string line = StripComment(raw);
                if (string.IsNullOrWhiteSpace(line)) { continue; }
                string[] parts = line.Split(';');
                if (parts.Length < 1) { continue; }
                int[] cps = ParseCpSeq(parts[0]);
                if (cps.Length != 2) { continue; }

                EntityHandle comp = EmitTextCompositionFromCps(batch, cps);
                Hash32 baseHash = Blake3.HashCodepoint(cps[0]);
                EntityHandle baseHandle = new(baseHash, "codepoint");
                EdgeMemberSpec[] members =
                [
                    new EdgeMemberSpec(baseHandle, "source", 0),
                    new EdgeMemberSpec(comp, "target", 1),
                ];
                batch.AddEdge("has_standardized_variant", ProvenanceCode, members);
                edges++;

                if (batch.EntityCount + batch.EdgeCount >= BatchFlushSize)
                {
                    await pipeline.SubmitBatchAsync(batch, ct);
                    batch = pipeline.CreateBatch(ProvenanceCode);
                }
            }
        }
        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await pipeline.SubmitBatchAsync(batch, ct);
        }
        Log.StandardizedVariantEdges(Logger, edges);
        await ReportAsync(reporter, "unicode.standardized_variants", 0, edges, ct);
        return edges;
    }

    // ── §12 Radical/stroke ───────────────────────────────────────────────

    private async Task<long> EmitRadicalStrokeAsync(
        IIngestionPipeline pipeline, IProgressReporter reporter, CancellationToken ct)
    {
        string? path = ResolveSource("ucd", "CJKRadicals.txt");
        if (path is null) { Log.SourceMissing(Logger, "CJKRadicals.txt"); return 0; }

        long edges = 0;
        IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);
        foreach (string raw in File.ReadLines(path))
        {
            ct.ThrowIfCancellationRequested();
            string line = StripComment(raw);
            if (string.IsNullOrWhiteSpace(line)) { continue; }
            string[] parts = line.Split(';');
            if (parts.Length < 3) { continue; }
            string radicalNum = parts[0].Trim();
            string unifiedHex = parts[1].Trim();
            if (string.IsNullOrEmpty(radicalNum) || string.IsNullOrEmpty(unifiedHex)) { continue; }
            int unifiedCp = int.Parse(unifiedHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            Hash32 compHash = ComputeHash(Encoding.UTF8.GetBytes(radicalNum));
            EntityHandle comp = batch.AddEntity(compHash, "text_composition");

            Hash32 srcHash = Blake3.HashCodepoint(unifiedCp);
            EntityHandle srcHandle = new(srcHash, "codepoint");
            EdgeMemberSpec[] members =
            [
                new EdgeMemberSpec(srcHandle, "source", 0),
                new EdgeMemberSpec(comp, "target", 1),
            ];
            batch.AddEdge("has_radical_stroke", ProvenanceCode, members);
            edges++;

            if (batch.EntityCount + batch.EdgeCount >= BatchFlushSize)
            {
                await pipeline.SubmitBatchAsync(batch, ct);
                batch = pipeline.CreateBatch(ProvenanceCode);
            }
        }
        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await pipeline.SubmitBatchAsync(batch, ct);
        }
        Log.RadicalStrokeEdges(Logger, edges);
        await ReportAsync(reporter, "unicode.radical_stroke", 0, edges, ct);
        return edges;
    }

    // ── §13 IVD per-collection ───────────────────────────────────────────

    private static readonly (string Dir, string Provenance)[] IvdCollections =
    [
        ("adobe-japan1", "ivd_adobe_japan1"),
        ("hanyo-denshi", "ivd_hanyo_denshi"),
        ("krname",       "ivd_krname"),
        ("moji_joho",    "ivd_moji_joho"),
        ("msarg",        "ivd_msarg"),
    ];

    private async Task<long> EmitIvdPerCollectionAsync(
        IIngestionPipeline pipeline, IProgressReporter reporter, CancellationToken ct)
    {
        string? sequencesPath = ResolveSource("ivd", "IVD_Sequences.txt")
            ?? ResolveSource("Unicode", "ivd", "IVD_Sequences.txt");
        if (sequencesPath is null) { Log.SourceMissing(Logger, "ivd/IVD_Sequences.txt"); return 0; }

        long edges = 0;
        IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);

        foreach (string raw in File.ReadLines(sequencesPath))
        {
            ct.ThrowIfCancellationRequested();
            string line = StripComment(raw);
            if (string.IsNullOrWhiteSpace(line)) { continue; }
            string[] parts = line.Split(';');
            if (parts.Length < 3) { continue; }
            int[] cps = ParseCpSeq(parts[0]);
            if (cps.Length != 2) { continue; }
            string collection = parts[1].Trim();
            string seqId = parts[2].Trim();

            string provenance = MapIvdCollectionProvenance(collection) ?? ProvenanceCode;

            string variantLabel = $"{collection}:{seqId}";
            Hash32 variantHash = ComputeHash(Encoding.UTF8.GetBytes(variantLabel));
            EntityHandle variantHandle = batch.AddEntity(variantHash, "text_composition");

            Hash32 baseHash = Blake3.HashCodepoint(cps[0]);
            EntityHandle baseHandle = new(baseHash, "codepoint");

            EdgeMemberSpec[] members =
            [
                new EdgeMemberSpec(baseHandle, "source", 0),
                new EdgeMemberSpec(variantHandle, "target", 1),
            ];
            batch.AddEdge("has_ideographic_variant_in_collection", provenance, members);
            edges++;

            if (batch.EntityCount + batch.EdgeCount >= BatchFlushSize)
            {
                await pipeline.SubmitBatchAsync(batch, ct);
                batch = pipeline.CreateBatch(ProvenanceCode);
            }
        }
        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await pipeline.SubmitBatchAsync(batch, ct);
        }
        Log.IvdEdges(Logger, edges);
        await ReportAsync(reporter, "unicode.ivd_per_collection", 0, edges, ct);
        return edges;
    }

    private static string? MapIvdCollectionProvenance(string collection)
    {
        string normalized = collection.Trim().ToLowerInvariant().Replace("-", "_", StringComparison.Ordinal);
        foreach ((string dir, string provenance) in IvdCollections)
        {
            if (dir.Replace("-", "_", StringComparison.Ordinal) == normalized) { return provenance; }
        }
        return null;
    }

    // ── §14 Materialization validation ───────────────────────────────────

    private async Task ValidateMaterializationAsync(
        NpgsqlConnection connection, IProgressReporter reporter, CancellationToken ct)
    {
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(
            connection, SubstrateFunctionNames.UcdMaterializationCounts);
        command.CommandTimeout = 0;
        await using NpgsqlDataReader rdr = await command.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            throw new InvalidOperationException("substrate.ucd_materialization_counts() returned no rows.");
        }
        long codepointClassifications = rdr.GetInt64(0);
        long codepointProperties = rdr.GetInt64(1);
        long simpleCaseEdges = rdr.GetInt64(2);
        long simpleCaseEdgesWithoutGeometry = rdr.GetInt64(3);
        long significanceContexts = rdr.GetInt64(4);
        long simpleCaseEdgeSignificance = rdr.GetInt64(5);

        if (codepointClassifications < MaxCodepoints)
        {
            throw new InvalidOperationException(
                $"UCD/UCA materialization incomplete: expected at least {MaxCodepoints:N0} unicode_consortium codepoint classifications, found {codepointClassifications:N0}.");
        }
        if (codepointProperties < MaxCodepoints)
        {
            throw new InvalidOperationException(
                $"UCD/UCA materialization incomplete: expected at least {MaxCodepoints:N0} codepoint_property rows, found {codepointProperties:N0}.");
        }
        if (simpleCaseEdges <= 0)
        {
            throw new InvalidOperationException(
                "UCD/UCA materialization incomplete: expected Unicode case mapping edges, found none.");
        }
        if (simpleCaseEdgesWithoutGeometry > 0)
        {
            throw new InvalidOperationException(
                $"UCD/UCA materialization incomplete: {simpleCaseEdgesWithoutGeometry:N0} Unicode case mapping edges are missing trajectory geometry.");
        }
        if (significanceContexts <= 0)
        {
            throw new InvalidOperationException(
                "UCD/UCA materialization incomplete: significance_context has no arenas.");
        }
        long expectedSimpleCaseEdgeSignificance = simpleCaseEdges * significanceContexts;
        if (simpleCaseEdgeSignificance < expectedSimpleCaseEdgeSignificance)
        {
            throw new InvalidOperationException(
                $"UCD/UCA materialization incomplete: expected {expectedSimpleCaseEdgeSignificance:N0} Unicode case edge significance rows, found {simpleCaseEdgeSignificance:N0}.");
        }

        await ReportAsync(reporter, "unicode.materialization_validation", codepointClassifications, 0, ct);
    }

    // ── Shared helpers ──────────────────────────────────────────────────

    private string? ResolveSource(params string[] subPath)
    {
        string[] candidates =
        [
            Path.Combine(new[] { _sourceDirectory, "Unicode", "Public", "17.0.0" }.Concat(subPath).ToArray()),
            Path.Combine(new[] { _sourceDirectory, "Public", "17.0.0" }.Concat(subPath).ToArray()),
            Path.Combine(new[] { _sourceDirectory, "17.0.0" }.Concat(subPath).ToArray()),
            Path.Combine(new[] { _sourceDirectory, "Unicode" }.Concat(subPath).ToArray()),
            Path.Combine(new[] { _sourceDirectory }.Concat(subPath).ToArray()),
        ];
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate)) { return candidate; }
        }
        return null;
    }

    private static string StripComment(string raw)
    {
        int hash = raw.IndexOf('#');
        return (hash >= 0 ? raw.Substring(0, hash) : raw).Trim();
    }

    private static int[] ParseCpSeq(string s)
    {
        s = s.Trim();
        if (string.IsNullOrEmpty(s)) { return []; }
        string[] toks = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int[] cps = new int[toks.Length];
        for (int i = 0; i < toks.Length; i++)
        {
            cps[i] = int.Parse(toks[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return cps;
    }

    private static EntityHandle EmitTextCompositionFromCps(IIngestionBatch batch, int[] cps)
    {
        Hash32[] childHashes = new Hash32[cps.Length];
        for (int i = 0; i < cps.Length; i++) { childHashes[i] = Blake3.HashCodepoint(cps[i]); }
        Hash32 compHash = ComputeMerkleHash(childHashes);
        return batch.AddEntity(compHash, "text_composition");
    }

    private static async Task<string> ExecuteScalarStringAsync(
        NpgsqlConnection connection, string functionName, CancellationToken ct)
    {
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(connection, functionName);
        command.CommandTimeout = 0;
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<long> ExecuteScalarLongAsync(
        NpgsqlConnection connection, string functionName, CancellationToken ct)
    {
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(connection, functionName);
        command.CommandTimeout = 0;
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private Task ReportAsync(IProgressReporter reporter, string section, long entities, long edges, CancellationToken ct)
        => reporter.ReportAsync(new ProgressSnapshot
        {
            DecomposerCode = ProvenanceCode,
            CurrentPhase = $"section:{section}",
            EntitiesCreated = entities,
            EdgesCreated = edges,
            CurrentFile = _sourceDirectory,
        }, ct);

    // ── Structured logging ──────────────────────────────────────────────

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "UCD/UCA source directory not found; substrate ingestion will fail without source files: {Path}")]
        public static partial void SourceDirectoryNotFound(ILogger logger, string path);

        [LoggerMessage(Level = LogLevel.Information, Message = "UCD/UCA extension catalog version {Version}")]
        public static partial void ExtensionVersion(ILogger logger, string version);

        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode codepoint atoms emitted: {Count}")]
        public static partial void CodepointAtoms(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode codepoint properties populated: {Count}")]
        public static partial void CodepointProperties(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode simple case edges emitted: {Count}")]
        public static partial void SimpleCaseEdges(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode full-case-fold edges emitted: {Count}")]
        public static partial void FullCaseFoldEdges(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode decomposition edges emitted: {Count}")]
        public static partial void DecompositionEdges(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode confusable edges emitted: {Count}")]
        public static partial void ConfusableEdges(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode named-sequence edges emitted: {Count}")]
        public static partial void NamedSequenceEdges(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode emoji-sequence edges emitted: {Count}")]
        public static partial void EmojiSequenceEdges(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode standardized-variant edges emitted: {Count}")]
        public static partial void StandardizedVariantEdges(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode radical-stroke edges emitted: {Count}")]
        public static partial void RadicalStrokeEdges(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode IVD per-collection edges emitted: {Count}")]
        public static partial void IvdEdges(ILogger logger, long count);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Source file not found, skipping: {File}")]
        public static partial void SourceMissing(ILogger logger, string file);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Unicode materialization complete — codepoints: {Codepoints}, properties: {Properties}, simple-case: {SimpleCase}, full-case-fold: {FullCaseFold}, decomposition: {Decomposition}, confusables: {Confusables}, named-sequences: {NamedSequences}, emoji-sequences: {EmojiSequences}, standardized-variants: {StandardizedVariants}, radical-stroke: {RadicalStroke}, ivd: {Ivd}")]
        public static partial void Materialized(
            ILogger logger,
            long codepoints,
            long properties,
            long simpleCase,
            long fullCaseFold,
            long decomposition,
            long confusables,
            long namedSequences,
            long emojiSequences,
            long standardizedVariants,
            long radicalStroke,
            long ivd);
    }
}
