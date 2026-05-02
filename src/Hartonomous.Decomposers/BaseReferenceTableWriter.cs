using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;

namespace Hartonomous.Decomposers;

/// <summary>
/// Shared Npgsql plumbing for every decomposer's reference-table/junction/edge-type
/// writer. Generic loaders delegate to <see cref="IReferenceDataReader"/> and junction
/// writers delegate to <see cref="IJunctionWriter"/> so the SQL is in one place
/// (the Engine implementations). All database access flows through those injected
/// services, which share the pipeline's single <see cref="NpgsqlDataSource"/> —
/// no second connection pool is opened here (audit A.3).
/// </summary>
internal abstract class BaseReferenceTableWriter
{
    protected const double AuthoritativeMu = 2000.0;
    protected const double AuthoritativeSigma = 50.0;
    protected const int ChunkSize = 50_000;

    private readonly IReferenceDataReader _reader;
    private readonly IJunctionWriter _junctionWriter;
    private readonly IReferenceDataWriter _writer;

    protected BaseReferenceTableWriter(IReferenceDataReader reader, IJunctionWriter junctionWriter, IReferenceDataWriter referenceDataWriter)
    {
        _reader = reader;
        _junctionWriter = junctionWriter;
        _writer = referenceDataWriter;
    }

    // ── Generic loaders (delegate to IReferenceDataReader) ─────────────────

    protected Task<Dictionary<string, int>> LoadCodeMapAsync(
        string tableName, int initialCapacity, CancellationToken ct) =>
        _reader.LoadCodeMapAsync(tableName, initialCapacity, ct);

    protected Task<Dictionary<(string Key, string Value), int>> LoadKeyValueMapAsync(
        string tableName, string keyColumn, string valueColumn, int initialCapacity, CancellationToken ct) =>
        _reader.LoadKeyValueMapAsync(tableName, keyColumn, valueColumn, initialCapacity, ct);

    protected Task<Dictionary<string, string>> LoadCodeTextMapAsync(
        string tableName, string valueColumn, int initialCapacity, CancellationToken ct) =>
        _reader.LoadCodeTextMapAsync(tableName, valueColumn, initialCapacity, ct);

    protected Task<HashSet<long>> LoadInt64SetAsync(
        string tableName, string columnName, CancellationToken ct) =>
        _reader.LoadInt64SetAsync(tableName, columnName, ct);

    protected Task<int> LoadIdByCodeAsync(
        string tableName, string code, CancellationToken ct) =>
        _reader.LoadIdByCodeAsync(tableName, code, ct);

    // ── Named public loaders (thin wrappers used directly by decomposers) ───

    public Task<Dictionary<string, int>> LoadLanguageCodeMapAsync(CancellationToken ct) =>
        LoadCodeMapAsync("substrate.language", 8000, ct);

    public Task<Dictionary<string, int>> LoadPosMapAsync(CancellationToken ct) =>
        LoadCodeMapAsync("substrate.pos", 24, ct);

    public Task<Dictionary<(string Key, string Value), int>> LoadMorphFeatureMapAsync(CancellationToken ct) =>
        LoadKeyValueMapAsync("substrate.morph_feature", "key", "value", 2048, ct);

    // ── Generic junction writers (delegate to IJunctionWriter) ──────────────
    // All entries reference entities via composite hash FK
    // (entity_type_id, entity_hash); there is no surrogate id.

    protected Task WriteGlickoJunctionAsync(
        string tableName, string refColumn,
        IReadOnlyList<(byte[] EntityHash, int RefId)> entries,
        double mu, double sigma, CancellationToken ct) =>
        _junctionWriter.WriteGlickoJunctionAsync(tableName, refColumn, entries, mu, sigma, ct);

    protected Task WriteGlickoJunctionAsync(
        string tableName, string refColumn,
        IReadOnlyList<(byte[] EntityHash, int RefId, double Mu)> entries,
        CancellationToken ct) =>
        _junctionWriter.WriteGlickoJunctionAsync(tableName, refColumn, entries, ct);

    protected Task WritePlainJunctionAsync(
        string tableName, string refColumn,
        IReadOnlyList<(byte[] EntityHash, int RefId)> entries,
        CancellationToken ct) =>
        _junctionWriter.WritePlainJunctionAsync(tableName, refColumn, entries, ct);

    // ── Named public junction writers ────────────────────────────────────────

    public Task WriteEntityLanguageJunctionsAsync(
        IReadOnlyList<(byte[] EntityHash, int LangId)> entries, CancellationToken ct) =>
        // entity_language is not Glicko-tracked: pure (entity, language) link.
        WritePlainJunctionAsync("substrate.entity_language", "language_id", entries, ct);

    public Task WriteEntityPosJunctionsAsync(
        IReadOnlyList<(byte[] EntityHash, int PosId)> entries, CancellationToken ct) =>
        WriteGlickoJunctionAsync("substrate.entity_pos", "pos_id",
            entries, AuthoritativeMu, AuthoritativeSigma, ct);

    public Task WriteEntityMorphFeatureJunctionsAsync(
        IReadOnlyList<(byte[] EntityHash, int MorphFeatureId)> entries, CancellationToken ct) =>
        WritePlainJunctionAsync("substrate.entity_morph_feature", "morph_feature_id",
            entries, ct);

    // ── edge_type upsert ─────────────────────────────────────────────────────

    public Task UpsertEdgeTypeAsync(
        string code, string category,
        string sourceEntityType, string targetEntityType,
        CancellationToken ct) =>
        _writer.UpsertEdgeTypeAsync(code, category, sourceEntityType, targetEntityType, ct);

    public Task UpsertStructuralEdgeTypeAsync(
        string code, string sourceEntityType, string targetEntityType, CancellationToken ct) =>
        UpsertEdgeTypeAsync(code, "structural", sourceEntityType, targetEntityType, ct);

    public Task UpsertCrossLingualEdgeTypeAsync(
        string code, string sourceEntityType, string targetEntityType, CancellationToken ct) =>
        UpsertEdgeTypeAsync(code, "cross_lingual", sourceEntityType, targetEntityType, ct);

    // ── Populate morph_feature (shared UD + Wiktionary) ──────────────────────

    public Task PopulateMorphFeaturesAsync(
        IReadOnlyCollection<(string Key, string Value)> feats, CancellationToken ct) =>
        _writer.PopulateMorphFeaturesAsync(feats, ct);

    protected Task PopulateDeprelsCoreAsync(
        IReadOnlyCollection<string> deprels, CancellationToken ct) =>
        _writer.PopulateDeprelsAsync(deprels, ct);

    protected Task PopulateLanguagesCoreAsync(
        IReadOnlyList<(
            string Code,
            string Name,
            string Scope,
            string Type,
            string? Part1,
            string? Part2B,
            string? Part2T)> records,
        CancellationToken ct) =>
        _writer.PopulateLanguagesAsync(records, ct);

    protected Task UpdateLanguageNameEntityIdsCoreAsync(
        IReadOnlyList<(string Code, byte[] NameHash)> updates,
        CancellationToken ct) =>
        _writer.UpdateLanguageNameEntityIdsAsync(updates, ct);

    protected Task<int> EnsureArchitectureClassCoreAsync(
        string code,
        CancellationToken ct) =>
        _writer.EnsureArchitectureClassAsync(code, ct);

    protected Task<int> EnsureModelRegistryCoreAsync(
        string code,
        string displayName,
        CancellationToken ct) =>
        _writer.EnsureModelRegistryAsync(code, displayName, ct);

    protected Task<int> EnsureModelPublisherCoreAsync(
        int registryId,
        string slug,
        string? displayName,
        CancellationToken ct) =>
        _writer.EnsureModelPublisherAsync(registryId, slug, displayName, ct);

    protected Task<long> EnsureModelSourceCoreAsync(
        int registryId,
        int publisherId,
        string modelSlug,
        byte[] revision,
        CancellationToken ct) =>
        _writer.EnsureModelSourceAsync(registryId, publisherId, modelSlug, revision, ct);

    protected Task PopulateGeneralCategoriesCoreAsync(
        IReadOnlyCollection<(string Code, string GroupCode, string Description)> categories,
        CancellationToken ct) =>
        _writer.PopulateGeneralCategoriesAsync(categories, ct);

    protected Task PopulateScriptsCoreAsync(
        IReadOnlyCollection<string> codes,
        CancellationToken ct) =>
        _writer.PopulateScriptsAsync(codes, ct);

    protected Task PopulateBlocksCoreAsync(
        IReadOnlyList<(string Code, int RangeStart, int RangeEnd)> blocks,
        CancellationToken ct) =>
        _writer.PopulateBlocksAsync(blocks, ct);

    protected Task PopulateBreakPropertiesCoreAsync(
        IReadOnlyCollection<(string Code, string Category)> properties,
        CancellationToken ct) =>
        _writer.PopulateBreakPropertiesAsync(properties, ct);

    protected Task WriteCodepointPropertiesCoreAsync(
        IReadOnlyList<(
            byte[] EntityHash,
            int CodepointValue,
            int GeneralCategoryId,
            int ScriptId,
            int BlockId,
            int? GcbId,
            int? WbId,
            int? SbId,
            int? LbId,
            bool IsExtendedPictographic,
            short Ccc,
            string? DecompositionType,
            int[]? DecompositionMapping,
            int? SimpleCaseFold,
            int[]? FullCaseFold)> rows,
        CancellationToken ct) =>
        _writer.WriteCodepointPropertiesAsync(rows, ct);

    protected Task UpsertHomogeneousEdgeTypesAsync(
        IReadOnlyCollection<string> codes,
        string category,
        string entityTypeCode,
        CancellationToken ct) =>
        _writer.UpsertHomogeneousEdgeTypesAsync(codes, category, entityTypeCode, ct);

    protected Task PopulateSenseRowsAsync(
        IReadOnlyList<(string Code, string Gloss, int LexnameId, int PosId)> senses,
        CancellationToken ct) =>
        _writer.PopulateSensesAsync(senses, ct);

    // Virtual so subclasses can extend if they ever own disposable state. The base
    // owns nothing now (audit A.3 — single connection pool comes from the pipeline).
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Defense-in-depth: every table/column name passed to a generic helper is compiled
    // from a string literal at the call site today, but we validate anyway so a future
    // caller cannot accidentally flow user input into the interpolated SQL.
    private static void AssertSafeIdentifier(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            throw new ArgumentException("Identifier cannot be empty.", nameof(identifier));
        }
        foreach (char c in identifier)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.'))
            {
                throw new ArgumentException(
                    $"Unsafe SQL identifier: '{identifier}'", nameof(identifier));
            }
        }
    }
}
