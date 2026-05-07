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
    /// Load the WordNet offset → synset_hash bridge map produced by
    /// <c>substrate.load_wordnet_offset_synset_map()</c>. The key is the
    /// BLAKE3 hash of the offset string ("XXXXXXXX-p"); callers compute it
    /// the same way to look up the substrate's content-pure synset hash.
    /// Used by OMW and any cross-lexicon decomposer to resolve synsets by
    /// their authoring offset without recomputing content hashes.
    /// </summary>
    Task<Dictionary<byte[], byte[]>> LoadWordNetOffsetSynsetMapAsync(CancellationToken ct);
}
