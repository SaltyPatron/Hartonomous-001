using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Hartonomous.Core.Engine;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

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

    public async Task RecordEntityComparisonAsync(
        EntityHandle winner, EntityHandle loser, string contextCode, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.RecordEntityComparison,
            [
                TextParameter(contextCode),
                ByteaParameter(winner.Hash),
                ByteaParameter(loser.Hash)
            ]);

        await cmd.ExecuteNonQueryAsync(ct);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            string winnerText = winner.ToString();
            string loserText = loser.ToString();
            Log.ComparisonRecorded(_logger, "entity", winnerText, loserText, contextCode);
        }
    }

    public async Task RecordEdgeComparisonAsync(
        EdgeHandle winner, EdgeHandle loser, string contextCode, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.RecordEdgeComparison,
            [
                TextParameter(contextCode),
                TextParameter(winner.EdgeTypeCode),
                ByteaParameter(winner.Hash),
                TextParameter(loser.EdgeTypeCode),
                ByteaParameter(loser.Hash)
            ]);

        await cmd.ExecuteNonQueryAsync(ct);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            string winnerText = winner.ToString();
            string loserText = loser.ToString();
            Log.ComparisonRecorded(_logger, "edge", winnerText, loserText, contextCode);
        }
    }

    public async Task InitializeEntityAsync(
        EntityHandle target, string contextCode, double initialMu, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.InitializeEntitySignificance,
            [
                TextParameter(contextCode),
                ByteaParameter(target.Hash),
                DoubleParameter(initialMu)
            ]);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InitializeEdgeAsync(
        EdgeHandle target, string contextCode, double initialMu, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.InitializeEdgeSignificance,
            [
                TextParameter(contextCode),
                TextParameter(target.EdgeTypeCode),
                ByteaParameter(target.Hash),
                DoubleParameter(initialMu)
            ]);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> PruneBelowThresholdAsync(
        string contextCode, double muThreshold, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.PruneSignificanceForContext,
            [
                TextParameter(contextCode),
                DoubleParameter(muThreshold)
            ]);

        object? result = await cmd.ExecuteScalarAsync(ct);
        int deleted = result switch
        {
            int intCount => intCount,
            long longCount => checked((int)longCount),
            _ => 0,
        };

        Log.PruneCompleted(_logger, contextCode, muThreshold, deleted);
        return deleted;
    }

    private static NpgsqlParameter TextParameter(string value)
        => new() { NpgsqlDbType = NpgsqlDbType.Text, Value = value };

    private static NpgsqlParameter ByteaParameter(byte[] value)
        => new() { NpgsqlDbType = NpgsqlDbType.Bytea, Value = value };

    private static NpgsqlParameter DoubleParameter(double value)
        => new() { NpgsqlDbType = NpgsqlDbType.Double, Value = value };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "{Surface} comparison recorded: winner={Winner}, loser={Loser}, context={ContextCode}")]
        public static partial void ComparisonRecorded(ILogger logger, string surface, string winner, string loser, string contextCode);

        [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Deleted} significance records below {Threshold} in context {ContextCode}")]
        public static partial void PruneCompleted(ILogger logger, string contextCode, double threshold, int deleted);
    }
}
