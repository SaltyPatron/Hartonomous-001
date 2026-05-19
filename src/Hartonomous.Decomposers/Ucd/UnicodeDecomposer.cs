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
using NpgsqlTypes;

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

        // §3 EmitCodepointAtomsAsync emits codepoint entity + POINTZM physicality
        // + 9 has_cp_* typed edges to content-hashed reference-vocab entities
        // (general_category / script / block / bidi_class / east_asian_width /
        // grapheme_break / word_break / sentence_break / line_break) + the
        // corresponding 9 narrow per-property analytics-cache junction rows.
        // Per Gate 1 #38 refactor (2026-05-18): the wide flat
        // substrate.codepoint_property junction is deleted; substrate truth is
        // the typed edges on substrate.edge, narrow junctions are denormalized
        // for index-locality. All emission goes through IIngestionPipeline.
        long codepointAtoms = await EmitCodepointAtomsAsync(pipeline, reporter, connection, ct);
        await pipeline.DrainPendingAsync(ct);

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
            0L,  // codepointProperties — deleted Gate 1 #38; narrow per-property junctions land inline in §3
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
    // substrate.general_category / script / block / break_property are seeded
    // statically by sql/schema/seed/{general_category,script,block,break_property}.sql
    // (generated from ext/hartonomous_pg/src/generated/pg_ucd_inventory.c).
    // The native blob's byte / ushort enum codes line up with the
    // reference-vocabulary id by the +1 convention documented on each
    // hartonomous_ucd_cp_* native export, so §4 codepoint_property FK
    // resolution is a pure arithmetic shift without per-row lookups.
    //
    // This method's job is now verification: the reference tables MUST be
    // populated before §4 runs. Throwing here surfaces a missing-seed
    // configuration before the COPY would attempt to violate the NOT NULL
    // FK constraints with garbage ids.

    private async Task PopulateReferenceVocabulariesAsync(
        NpgsqlConnection connection, IProgressReporter reporter, CancellationToken ct)
    {
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(
            connection, SubstrateFunctionNames.UcdReferenceVocabularyCounts);
        command.CommandTimeout = 0;
        await using NpgsqlDataReader rdr = await command.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                "Reference vocabulary verification returned no rows.");
        }
        long gc = rdr.GetInt64(0);
        long script = rdr.GetInt64(1);
        long block = rdr.GetInt64(2);
        long bidi = rdr.GetInt64(3);
        long eaw = rdr.GetInt64(4);
        long bp = rdr.GetInt64(5);
        if (gc == 0 || script == 0 || block == 0 || bidi == 0 || eaw == 0 || bp == 0)
        {
            throw new InvalidOperationException(
                $"UCD reference vocabulary missing seed rows " +
                $"(gc={gc}, script={script}, block={block}, bidi={bidi}, eaw={eaw}, break_property={bp}). " +
                $"Run scripts/hart db reset --force to apply sql/schema/seed/*.sql.");
        }

        await ReportAsync(reporter, "unicode.reference_vocabularies", gc + script + block + bidi + eaw + bp, 0, ct);
    }

    // ── §3 Codepoint atoms ───────────────────────────────────────────────
    //
    // Per codepoint, §3 emits:
    //   * The codepoint entity itself (BLAKE3 over the codepoint integer)
    //     with centroid + hilbert index inline on substrate.entity
    //   * Atom POINTZM physicality on the "entity" partition
    //   * 9 generic has_classification typed edges (one per UCD property
    //     dimension) targeting content-hashed reference-vocab entities
    //     (Lu / Latn / "Basic Latin" / AL / W / "GCB:CR" / ...). Arena
    //     routing per (edge_type × target_entity_type) via
    //     EdgeArenaRouter.EventsFor overload — unicode_version_consensus
    //     plus the universal pair fire on every classification.
    //   * 9 narrow per-property junction rows (cp_general_category /
    //     cp_script / cp_block / cp_bidi_class / cp_east_asian_width /
    //     cp_grapheme_break / cp_word_break / cp_sentence_break /
    //     cp_line_break) — denormalized analytics caches for
    //     index-locality lookups; substrate truth is the typed edges.
    //
    // Reference-vocab entities (general_category / script / block /
    // bidi_class / east_asian_width / break_property) are emitted idempotently
    // per batch — a chunk-scoped HashSet skips duplicate AddEntity calls
    // within a flush. Cross-batch dedup happens via the pipeline's AP-19
    // existence probe + ON CONFLICT belt-and-suspenders.
    //
    // Per AP-30 / AP-38 collapse principle: one generic has_classification
    // edge type, dimensions discriminated by target entity_type + (provenance
    // × arena), not per-dimension edge_type proliferation.

    private async Task<long> EmitCodepointAtomsAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        NpgsqlConnection connection,
        CancellationToken ct)
    {
        // Load id→code dictionaries once at section start. Cross-products
        // with BlobUcdPropertyAccessor's enum-code returns via +1 convention
        // (native byte/ushort enum code + 1 = reference-vocab id).
        Dictionary<int, string> gcCodes      = await LoadIdCodeMapAsync(connection, "substrate.general_category",  ct);
        Dictionary<int, string> scriptCodes  = await LoadIdCodeMapAsync(connection, "substrate.script",            ct);
        Dictionary<int, string> blockCodes   = await LoadIdCodeMapAsync(connection, "substrate.block",             ct);
        Dictionary<int, string> bidiCodes    = await LoadIdCodeMapAsync(connection, "substrate.bidi_class",        ct);
        Dictionary<int, string> eawCodes     = await LoadIdCodeMapAsync(connection, "substrate.east_asian_width",  ct);
        Dictionary<(string Cat, int EnumId), (int Id, string Code)> breakProps =
            await LoadBreakPropertyMapAsync(connection, ct);

        long entityCount = 0;
        IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);
        HashSet<Hash32> chunkSeenRefVocab = new();
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
                    pipeline, batch, pendingHashes, pendingCodepoints,
                    gcCodes, scriptCodes, blockCodes, bidiCodes, eawCodes, breakProps,
                    chunkSeenRefVocab, ct);

                if (batch.EntityCount + batch.EdgeCount >= BatchFlushSize)
                {
                    await pipeline.SubmitBatchAsync(batch, ct);
                    batch = pipeline.CreateBatch(ProvenanceCode);
                    chunkSeenRefVocab.Clear();
                }
            }
        }

        if (pendingHashes.Count > 0)
        {
            entityCount += await FlushCodepointAtomsAsync(
                pipeline, batch, pendingHashes, pendingCodepoints,
                gcCodes, scriptCodes, blockCodes, bidiCodes, eawCodes, breakProps,
                chunkSeenRefVocab, ct);
        }
        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await pipeline.SubmitBatchAsync(batch, ct);
        }

        Log.CodepointAtoms(Logger, entityCount);
        await ReportAsync(reporter, "unicode.codepoint_atoms", entityCount, 0, ct);
        return entityCount;
    }

    private long FlushCodepointAtomsInner(
        IIngestionBatch batch,
        List<Hash32> hashes,
        List<int> codepoints,
        HashSet<HashKey> existing,
        Dictionary<int, string> gcCodes,
        Dictionary<int, string> scriptCodes,
        Dictionary<int, string> blockCodes,
        Dictionary<int, string> bidiCodes,
        Dictionary<int, string> eawCodes,
        Dictionary<(string Cat, int EnumId), (int Id, string Code)> breakProps,
        HashSet<Hash32> chunkSeenRefVocab)
    {
        long emitted = 0;
        for (int i = 0; i < hashes.Count; i++)
        {
            if (existing.Contains(new HashKey(hashes[i]))) { continue; }
            int cp = codepoints[i];
            (double x, double y, double z, double m) = PhysicalityEmitter.CodepointS3Position(cp);
            double[] point4 = [x, y, z, m];
            ulong hilbert = Hilbert.Index(point4, 16);
            EntityHandle cpHandle = batch.AddEntity(hashes[i], "codepoint", x, y, z, m, (long)hilbert);
            batch.AddPhysicalityPoint4d(cpHandle, "entity", x, y, z, m);

            // 5 categorical UCD-property classifications via has_classification
            // typed edges + narrow per-property junctions.
            EmitClassification(batch, cpHandle, _ucd.GetGeneralCategoryCode(cp) + 1, gcCodes,
                ReferenceVocabularyHashes.GeneralCategoryEntityHash,
                "general_category", "cp_general_category", chunkSeenRefVocab);
            EmitClassification(batch, cpHandle, _ucd.GetScriptCode(cp) + 1, scriptCodes,
                ReferenceVocabularyHashes.ScriptEntityHash,
                "script", "cp_script", chunkSeenRefVocab);
            EmitClassification(batch, cpHandle, _ucd.GetBlockCode(cp) + 1, blockCodes,
                ReferenceVocabularyHashes.BlockEntityHash,
                "block", "cp_block", chunkSeenRefVocab);
            EmitClassification(batch, cpHandle, _ucd.GetBidiClassCode(cp) + 1, bidiCodes,
                ReferenceVocabularyHashes.BidiClassEntityHash,
                "bidi_class", "cp_bidi_class", chunkSeenRefVocab);
            EmitClassification(batch, cpHandle, _ucd.GetEastAsianWidthCode(cp) + 1, eawCodes,
                ReferenceVocabularyHashes.EastAsianWidthEntityHash,
                "east_asian_width", "cp_east_asian_width", chunkSeenRefVocab);

            // 4 break-property classifications (GCB / WB / SB / LB). The
            // break_property reference table uses composite (category, enum_id)
            // → id lookup because the per-category enum value space overlaps
            // across categories.
            EmitBreakClassification(batch, cpHandle, "GCB", (int)_ucd.GetGcb(cp),
                breakProps, "cp_grapheme_break", chunkSeenRefVocab);
            EmitBreakClassification(batch, cpHandle, "WB", (int)_ucd.GetWb(cp),
                breakProps, "cp_word_break", chunkSeenRefVocab);
            EmitBreakClassification(batch, cpHandle, "SB", (int)_ucd.GetSb(cp),
                breakProps, "cp_sentence_break", chunkSeenRefVocab);
            EmitBreakClassification(batch, cpHandle, "LB", (int)_ucd.GetLb(cp),
                breakProps, "cp_line_break", chunkSeenRefVocab);

            emitted++;
        }
        hashes.Clear();
        codepoints.Clear();
        return emitted;
    }

    private async Task<long> FlushCodepointAtomsAsync(
        IIngestionPipeline pipeline,
        IIngestionBatch batch,
        List<Hash32> hashes,
        List<int> codepoints,
        Dictionary<int, string> gcCodes,
        Dictionary<int, string> scriptCodes,
        Dictionary<int, string> blockCodes,
        Dictionary<int, string> bidiCodes,
        Dictionary<int, string> eawCodes,
        Dictionary<(string Cat, int EnumId), (int Id, string Code)> breakProps,
        HashSet<Hash32> chunkSeenRefVocab,
        CancellationToken ct)
    {
        HashSet<HashKey> existing = await pipeline.GetExistingEntityHashesAsync(hashes, ct);
        return FlushCodepointAtomsInner(
            batch, hashes, codepoints, existing,
            gcCodes, scriptCodes, blockCodes, bidiCodes, eawCodes, breakProps,
            chunkSeenRefVocab);
    }

    private void EmitClassification(
        IIngestionBatch batch,
        EntityHandle cpHandle,
        int refId,
        Dictionary<int, string> codeMap,
        Func<string, Hash32> hashFn,
        string targetEntityTypeCode,
        string junctionTable,
        HashSet<Hash32> chunkSeenRefVocab)
    {
        if (!codeMap.TryGetValue(refId, out string? code)) { return; }
        Hash32 refHash = hashFn(code);
        EntityHandle refHandle;
        if (chunkSeenRefVocab.Add(refHash))
        {
            refHandle = batch.AddEntity(refHash, targetEntityTypeCode);
        }
        else
        {
            refHandle = new EntityHandle(refHash, targetEntityTypeCode);
        }
        batch.AddEdge(
            "has_classification",
            ProvenanceCode,
            [
                new EdgeMemberSpec(cpHandle, "source", 0),
                new EdgeMemberSpec(refHandle, "target", 1),
            ],
            ReadOnlySpan<EdgeSignificanceSpec>.Empty,
            EdgeArenaRouter.EventsFor("has_classification", targetEntityTypeCode));
        batch.AddJunction(junctionTable, cpHandle, refId);
    }

    private void EmitBreakClassification(
        IIngestionBatch batch,
        EntityHandle cpHandle,
        string category,
        int enumId,
        Dictionary<(string Cat, int EnumId), (int Id, string Code)> breakProps,
        string junctionTable,
        HashSet<Hash32> chunkSeenRefVocab)
    {
        if (!breakProps.TryGetValue((category, enumId), out var bp)) { return; }
        Hash32 refHash = ReferenceVocabularyHashes.BreakPropertyEntityHash(category, bp.Code);
        EntityHandle refHandle;
        if (chunkSeenRefVocab.Add(refHash))
        {
            refHandle = batch.AddEntity(refHash, "break_property");
        }
        else
        {
            refHandle = new EntityHandle(refHash, "break_property");
        }
        batch.AddEdge(
            "has_classification",
            ProvenanceCode,
            [
                new EdgeMemberSpec(cpHandle, "source", 0),
                new EdgeMemberSpec(refHandle, "target", 1),
            ],
            ReadOnlySpan<EdgeSignificanceSpec>.Empty,
            EdgeArenaRouter.EventsFor("has_classification", "break_property"));
        batch.AddJunction(junctionTable, cpHandle, bp.Id);
    }

    private static async Task<Dictionary<int, string>> LoadIdCodeMapAsync(
        NpgsqlConnection connection, string tableName, CancellationToken ct)
    {
        Dictionary<int, string> map = new(256);
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(
            connection,
            SubstrateFunctionNames.ReferenceCodeMap,
            new object?[] { tableName });
        command.CommandTimeout = 0;
        await using NpgsqlDataReader rdr = await command.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            int id = rdr.GetInt32(0);
            string code = rdr.GetString(1);
            map[id] = code;
        }
        return map;
    }

    private static async Task<Dictionary<(string Cat, int EnumId), (int Id, string Code)>>
        LoadBreakPropertyMapAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        Dictionary<(string, int), (int, string)> map = new(128);
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(
            connection,
            SubstrateFunctionNames.BreakPropertyFullMap);
        command.CommandTimeout = 0;
        await using NpgsqlDataReader rdr = await command.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            int id = rdr.GetInt32(0);
            string category = rdr.GetString(1);
            int enumId = rdr.GetInt32(2);
            string code = rdr.GetString(3);
            map[(category, enumId)] = (id, code);
        }
        return map;
    }


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
        batch.AddEdge(
            edgeTypeCode,
            ProvenanceCode,
            members,
            ReadOnlySpan<EdgeSignificanceSpec>.Empty,
            EdgeArenaRouter.EventsFor(edgeTypeCode));
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
            batch.AddEdge(
                "has_full_case_mapping",
                ProvenanceCode,
                members,
                ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                EdgeArenaRouter.EventsFor("has_full_case_mapping"));
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
            batch.AddEdge(
                edgeCode,
                ProvenanceCode,
                members,
                ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                EdgeArenaRouter.EventsFor(edgeCode));
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
                batch.AddEdge(
                    "canonical_composes_to",
                    ProvenanceCode,
                    composeMembers,
                    ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                    EdgeArenaRouter.EventsFor("canonical_composes_to"));
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
            batch.AddEdge(
                "confusable_with",
                ProvenanceCode,
                members,
                ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                EdgeArenaRouter.EventsFor("confusable_with"));
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
            batch.AddEdge(
                "has_named_sequence",
                ProvenanceCode,
                members,
                ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                EdgeArenaRouter.EventsFor("has_named_sequence"));
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
        batch.AddEdge(
            edgeCode,
            ProvenanceCode,
            members,
            ReadOnlySpan<EdgeSignificanceSpec>.Empty,
            EdgeArenaRouter.EventsFor(edgeCode));
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
                batch.AddEdge(
                    "has_standardized_variant",
                    ProvenanceCode,
                    members,
                    ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                    EdgeArenaRouter.EventsFor("has_standardized_variant"));
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
            batch.AddEdge(
                "has_radical_stroke",
                ProvenanceCode,
                members,
                ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                EdgeArenaRouter.EventsFor("has_radical_stroke"));
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
            batch.AddEdge(
                "has_ideographic_variant_in_collection",
                provenance,
                members,
                ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                EdgeArenaRouter.EventsFor("has_ideographic_variant_in_collection"));
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
    // The prior populate_*_from_ext catalog removal (Gate 1 Task #22) also
    // dropped substrate.ucd_materialization_counts; the function has been
    // restored as a read-only validation probe to honor AP-2 (no inline raw
    // SQL in C#). codepoint_property population is deferred (the denormalized
    // cache is no longer populated; properties live in the native blob); the
    // codepoint-property check is therefore omitted from the validation set.

    private async Task ValidateMaterializationAsync(
        NpgsqlConnection connection, IProgressReporter reporter, CancellationToken ct)
    {
        await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(
            connection, SubstrateFunctionNames.UcdMaterializationCounts);
        command.CommandTimeout = 0;
        await using NpgsqlDataReader rdr = await command.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            throw new InvalidOperationException("ucd materialization validation: no rows.");
        }
        long codepointClassifications = rdr.GetInt64(0);
        long simpleCaseEdges = rdr.GetInt64(1);
        long simpleCaseEdgesWithoutGeometry = rdr.GetInt64(2);
        long arenas = rdr.GetInt64(3);
        long simpleCaseEdgeSignificance = rdr.GetInt64(4);

        if (codepointClassifications < MaxCodepoints)
        {
            throw new InvalidOperationException(
                $"UCD/UCA materialization incomplete: expected at least {MaxCodepoints:N0} unicode_consortium codepoint classifications, found {codepointClassifications:N0}.");
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
        if (arenas <= 0)
        {
            throw new InvalidOperationException(
                "UCD/UCA materialization incomplete: significance_context has no arenas.");
        }
        long expected = simpleCaseEdges * arenas;
        if (simpleCaseEdgeSignificance < expected)
        {
            throw new InvalidOperationException(
                $"UCD/UCA materialization incomplete: expected {expected:N0} Unicode case edge significance rows, found {simpleCaseEdgeSignificance:N0}.");
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
