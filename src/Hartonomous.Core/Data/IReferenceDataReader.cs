using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Core.Data;

/// <summary>
/// Reads code→id mappings from substrate reference tables
/// (<c>entity_type</c>, <c>edge_type</c>, <c>pos</c>, <c>language</c>, etc.).
/// Replaces the duplicate load logic in <c>CodeResolver</c> and
/// <c>BaseReferenceTableWriter.LoadCodeMapAsync</c>.
/// </summary>
public interface IReferenceDataReader
{
    /// <summary>
    /// Load all <c>(code → id)</c> pairs from a reference table.
    /// Table name is validated against an allowlist by the implementation.
    /// </summary>
    Task<Dictionary<string, int>> LoadCodeMapAsync(
        string tableName, int initialCapacity, CancellationToken ct);

    /// <summary>
    /// Load all <c>((key, value) → id)</c> triples from a two-column reference table
    /// (e.g. <c>morph_feature</c> with <c>key</c> and <c>value</c> columns).
    /// </summary>
    Task<Dictionary<(string Key, string Value), int>> LoadKeyValueMapAsync(
        string tableName, string keyColumn, string valueColumn,
        int initialCapacity, CancellationToken ct);

    /// <summary>
    /// Load all <c>(code → text)</c> pairs from a reference table.
    /// Table and column names are validated against an allowlist by the implementation.
    /// </summary>
    Task<Dictionary<string, string>> LoadCodeTextMapAsync(
        string tableName, string valueColumn, int initialCapacity, CancellationToken ct);

    /// <summary>
    /// Load all bigint values from a single column in a reference or junction table.
    /// Table and column names are validated against an allowlist by the implementation.
    /// </summary>
    Task<HashSet<long>> LoadInt64SetAsync(
        string tableName, string columnName, CancellationToken ct);

    /// <summary>
    /// Load a single row id by its <c>code</c> value from a reference table.
    /// Table name is validated against an allowlist by the implementation.
    /// </summary>
    Task<int> LoadIdByCodeAsync(
        string tableName, string code, CancellationToken ct);

    /// <summary>
    /// Load all <c>(code → double)</c> pairs from a reference table with a float8 column.
    /// Used to load <c>substrate.provenance.initial_mu</c> for inline edge significance emission.
    /// Table and column names are validated against an allowlist by the implementation.
    /// </summary>
    Task<Dictionary<string, double>> LoadCodeDoubleMapAsync(
        string tableName, string valueColumn, int initialCapacity, CancellationToken ct);

    /// <summary>
    /// Load every ISO 639 form (<c>code</c> = 639-3, <c>part1</c> = 639-1,
    /// <c>part2b</c> = 639-2/B, <c>part2t</c> = 639-2/T) from
    /// <c>substrate.language</c> as a single alias → canonical-id map. Each
    /// non-null form maps to the row's id; conflicts are first-write-wins (the
    /// substrate.language seed is well-formed so conflicts shouldn't occur in
    /// practice).
    ///
    /// Used by language-aware decomposers (Wiktionary translations, OMW
    /// cross-lingual alignments, Tatoeba) to build a BCP47 + ISO-form-aware
    /// filter via <c>Hartonomous.Decomposers.LanguageFilterResolver</c>.
    /// </summary>
    Task<Dictionary<string, int>> LoadLanguageAliasMapAsync(CancellationToken ct);
}
