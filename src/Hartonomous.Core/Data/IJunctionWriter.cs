using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Core.Data;

/// <summary>
/// Bulk-writes rows into substrate junction tables. Junction tables reference
/// entities by hash only (Phase C unification — content identity is the hash;
/// classification is metadata on substrate.entity_classification).
/// </summary>
public interface IJunctionWriter
{
    /// <summary>
    /// Bulk insert (entity_hash, ref_id, mu, sigma) rows into a Glicko-tracked
    /// junction table. Mu and sigma are applied uniformly to all entries.
    /// </summary>
    Task WriteGlickoJunctionAsync(
        string tableName, string refColumn,
        IReadOnlyList<(byte[] EntityHash, int RefId)> entries,
        double mu, double sigma, CancellationToken ct);

    /// <summary>
    /// Bulk insert with per-entry mu. Sigma defaults to the implementation's
    /// authoritative value.
    /// </summary>
    Task WriteGlickoJunctionAsync(
        string tableName, string refColumn,
        IReadOnlyList<(byte[] EntityHash, int RefId, double Mu)> entries,
        CancellationToken ct);

    /// <summary>
    /// Bulk insert plain (entity_hash, ref_id) junction rows without Glicko tracking.
    /// </summary>
    Task WritePlainJunctionAsync(
        string tableName, string refColumn,
        IReadOnlyList<(byte[] EntityHash, int RefId)> entries,
        CancellationToken ct);
}
