using System;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Recomposition;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Recomposers;

/// <summary>
/// Client-side bulk-tier content recomposer. Replaces the PG-side
/// recursive-CTE walkers (<c>substrate.recompose_text</c> /
/// <c>recompose_content</c> / <c>get_composition_children</c> /
/// <c>pg_recompose_walk</c>) per modular-wishing-koala Gate 1 reopened
/// item #36.
///
/// <para>
/// Thin wrapper over <see cref="BulkTierContentWalk"/>: owns the
/// <see cref="NpgsqlDataSource"/> and a logger; delegates the actual
/// algorithm to the static walker in Core. The static helper lives in
/// Core so <see cref="Hartonomous.Engine.Data.NpgsqlEntityReader"/> can
/// call it via the same surface without a cross-project dependency on
/// this project.
/// </para>
/// </summary>
public sealed class ContentRecomposer
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ContentRecomposer> _logger;

    public ContentRecomposer(NpgsqlDataSource dataSource, ILogger<ContentRecomposer> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Recompose UTF-8 byte content for the entity at <paramref name="rootHash"/>.
    /// </summary>
    /// <param name="rootHash">32-byte BLAKE3 content hash of the root entity.</param>
    /// <param name="maxDepth">Safety cap on tier descent.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<byte[]> RecomposeAsync(
        Hash32 rootHash, int maxDepth, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        return await BulkTierContentWalk.RecomposeAsync(conn, rootHash, maxDepth, ct);
    }
}
