using System;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// Wraps a Postgres / Npgsql exception with the pipeline context that was
/// active when it was thrown: which batch number, which sub-phase
/// (UpsertEntities, CreateEdges, …), which sub-step (the named substrate
/// function or COPY operation), the row count being processed, and the SQL
/// text. Lets the C# log line carry the same identification PG's
/// "client backend was running: …" line carries — without grep'ing
/// docker logs.
/// </summary>
public sealed class IngestionStepException : Exception
{
    public long BatchId { get; }
    public string Phase { get; }
    public string SubStep { get; }
    public int RowCount { get; }
    public string Sql { get; }

    public IngestionStepException(
        long batchId,
        string phase,
        string subStep,
        int rowCount,
        string sql,
        Exception inner)
        : base(BuildMessage(batchId, phase, subStep, rowCount, sql, inner), inner)
    {
        BatchId = batchId;
        Phase = phase;
        SubStep = subStep;
        RowCount = rowCount;
        Sql = sql;
    }

    private static string BuildMessage(long batchId, string phase, string subStep, int rowCount, string sql, Exception inner)
    {
        return $"Pipeline batch #{batchId} | phase={phase} | step={subStep} | rows={rowCount} | sql=\"{sql}\" | inner={inner.GetType().Name}: {inner.Message}";
    }
}
