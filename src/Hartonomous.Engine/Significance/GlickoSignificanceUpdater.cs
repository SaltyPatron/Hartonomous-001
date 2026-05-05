using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Engine;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Engine.Significance;

public sealed partial class GlickoSignificanceUpdater : ISignificanceUpdater
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<GlickoSignificanceUpdater> _logger;

    public GlickoSignificanceUpdater(NpgsqlDataSource dataSource, ILogger<GlickoSignificanceUpdater> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task RecordComparisonAsync(
        long winnerId, long loserId, string contextCode, bool isEntity, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        int contextId = await ResolveContextIdAsync(conn, contextCode, ct);

        await using NpgsqlCommand cmd = new(
            isEntity
                ? "CALL substrate.record_comparison($1, NULL, $2, NULL, $3)"
                : "CALL substrate.record_comparison(NULL, $1, NULL, $2, $3)",
            conn);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bigint, winnerId);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bigint, loserId);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Integer, contextId);

        await cmd.ExecuteNonQueryAsync(ct);

        Log.ComparisonRecorded(_logger, winnerId, loserId, contextCode);
    }

    public async Task InitializeAsync(
        long targetId, string contextCode, double initialMu, bool isEntity, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        int contextId = await ResolveContextIdAsync(conn, contextCode, ct);

        await using NpgsqlCommand cmd = new(
            isEntity
                ? "CALL substrate.initialize_significance($1, NULL, $2, $3)"
                : "CALL substrate.initialize_significance(NULL, $1, $2, $3)",
            conn);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bigint, targetId);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Integer, contextId);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Double, initialMu);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> PruneBelowThresholdAsync(
        string contextCode, double muThreshold, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        int contextId = await ResolveContextIdAsync(conn, contextCode, ct);

        await using NpgsqlCommand cmd = new(
            "SELECT substrate.prune_significance($1, $2)", conn);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Integer, contextId);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Double, muThreshold);

        object? result = await cmd.ExecuteScalarAsync(ct);
        int deleted = result is int d ? d : 0;

        Log.PruneCompleted(_logger, contextCode, muThreshold, deleted);
        return deleted;
    }

    private static async Task<int> ResolveContextIdAsync(NpgsqlConnection conn, string code, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = new(
            "SELECT substrate.resolve_context_id($1)", conn);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Varchar, code);

        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is int id
            ? id
            : throw new System.InvalidOperationException($"Unknown significance context: '{code}'");
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Comparison recorded: winner={WinnerId}, loser={LoserId}, context={ContextCode}")]
        public static partial void ComparisonRecorded(ILogger logger, long winnerId, long loserId, string contextCode);

        [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Deleted} significance records below {Threshold} in context {ContextCode}")]
        public static partial void PruneCompleted(ILogger logger, string contextCode, double threshold, int deleted);
    }
}
