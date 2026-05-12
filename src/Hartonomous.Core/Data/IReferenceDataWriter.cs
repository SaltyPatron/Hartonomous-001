using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Core.Data;

/// <summary>
/// Writes reference/classification data into substrate tables. Covers
/// <c>edge_type</c> upserts and <c>morph_feature</c> population that were
/// previously inline SQL in <c>BaseReferenceTableWriter</c>.
/// </summary>
public interface IReferenceDataWriter
{
    /// <summary>
    /// Upsert a row into <c>substrate.edge_type</c>.
    /// </summary>
    Task UpsertEdgeTypeAsync(
        string code, string category,
        string sourceEntityType, string targetEntityType,
        CancellationToken ct);

    /// <summary>
    /// Bulk-insert <c>(key, value)</c> rows into <c>substrate.morph_feature</c>,
    /// ignoring duplicates.
    /// </summary>
    Task PopulateMorphFeaturesAsync(
        IReadOnlyCollection<(string Key, string Value)> features,
        CancellationToken ct);

    /// <summary>
    /// Bulk-insert UD dependency relation codes into <c>substrate.deprel</c>,
    /// then resolve parent ids for subtyped relations such as <c>acl:relcl</c>.
    /// </summary>
    Task PopulateDeprelsAsync(
        IReadOnlyCollection<string> deprels,
        CancellationToken ct);

    /// <summary>
    /// Bulk-insert ISO 639 language rows into <c>substrate.language</c>, updating
    /// alternate code columns on conflict.
    /// </summary>
    Task PopulateLanguagesAsync(
        IReadOnlyList<(
            string Code,
            string Name,
            string Scope,
            string Type,
            string? Part1,
            string? Part2B,
            string? Part2T)> records,
        CancellationToken ct);

    /// <summary>
    /// Upsert and return an architecture class id via the substrate helper function.
    /// </summary>
    Task<int> EnsureArchitectureClassAsync(
        string code,
        CancellationToken ct);

    /// <summary>
    /// Upsert and return a model registry id via the substrate helper function.
    /// </summary>
    Task<int> EnsureModelRegistryAsync(
        string code,
        string displayName,
        CancellationToken ct);

    /// <summary>
    /// Upsert and return a model publisher id via the substrate helper function.
    /// </summary>
    Task<int> EnsureModelPublisherAsync(
        int registryId,
        string slug,
        string? displayName,
        CancellationToken ct);

    /// <summary>
    /// Upsert and return a model source id via the substrate helper function.
    /// </summary>
    Task<long> EnsureModelSourceAsync(
        int registryId,
        int publisherId,
        string modelSlug,
        byte[] revision,
        CancellationToken ct);

    /// <summary>
    /// Bulk-insert Unicode general categories into <c>substrate.general_category</c>.
    /// </summary>
    Task PopulateGeneralCategoriesAsync(
        IReadOnlyCollection<(string Code, string GroupCode, string Description)> categories,
        CancellationToken ct);

    /// <summary>
    /// Bulk-insert Unicode scripts into <c>substrate.script</c>.
    /// </summary>
    Task PopulateScriptsAsync(
        IReadOnlyCollection<string> codes,
        CancellationToken ct);

    /// <summary>
    /// Bulk-insert Unicode blocks into <c>substrate.block</c>.
    /// </summary>
    Task PopulateBlocksAsync(
        IReadOnlyList<(string Code, int RangeStart, int RangeEnd)> blocks,
        CancellationToken ct);

    /// <summary>
    /// Bulk-insert Unicode break properties into <c>substrate.break_property</c>.
    /// </summary>
    Task PopulateBreakPropertiesAsync(
        IReadOnlyCollection<(string Code, string Category)> properties,
        CancellationToken ct);

    /// <summary>
    /// Binary-copy rows into <c>substrate.codepoint_property</c> using hash FKs
    /// to <c>substrate.entity(hash)</c>.
    /// </summary>
    Task WriteCodepointPropertiesAsync(
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
        CancellationToken ct);

    /// <summary>
    /// Bulk-upsert edge types whose source and target types are the same entity type.
    /// </summary>
    Task UpsertHomogeneousEdgeTypesAsync(
        IReadOnlyCollection<string> codes,
        string category,
        string entityTypeCode,
        CancellationToken ct);

}
