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

}
