using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Hartonomous.Core.Monitoring;

namespace Hartonomous.Engine.Monitoring;

public sealed partial class DatabaseProgressReporter : IProgressReporter
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<DatabaseProgressReporter> _logger;

    public DatabaseProgressReporter(NpgsqlDataSource dataSource, ILogger<DatabaseProgressReporter> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task ReportAsync(ProgressSnapshot snapshot, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "CALL monitor.report_progress($1, $2, $3, $4, $5, $6, $7, $8, $9)", conn);

        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, snapshot.DecomposerCode);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, snapshot.CurrentPhase);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, snapshot.CurrentBatch ?? 0);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bigint, snapshot.EntitiesCreated);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bigint, snapshot.EdgesCreated);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bigint, 0L);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, "completed");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, System.DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, System.DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);

        Log.ProgressReported(_logger, snapshot.DecomposerCode, snapshot.CurrentBatch ?? 0, snapshot.EntitiesCreated);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Progress reported: {DecomposerCode} batch {Batch} ({Entities} entities)")]
        public static partial void ProgressReported(ILogger logger, string decomposerCode, int batch, long entities);
    }
}
