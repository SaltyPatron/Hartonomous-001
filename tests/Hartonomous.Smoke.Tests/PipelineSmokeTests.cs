namespace Hartonomous.Smoke.Tests;

/// <summary>
/// Smoke tests for the streaming ingestion pipeline's drain SQL — every
/// embedded resource SQL file must parse and execute against the live
/// substrate. If any of these SQL strings drift out of sync with the
/// substrate schema (e.g. attestation_type_id added, partition layout
/// changed), the pipeline fails at first emission with no clear signal
/// from the embedded resources.
/// </summary>
[Collection("smoke")]
public sealed class PipelineSmokeTests
{
    private readonly SmokeFixture _fx;

    public PipelineSmokeTests(SmokeFixture fx) => _fx = fx;

    [Fact]
    public async Task DrainTempTableCreate_AllKinds_ExecutesCleanly()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // Mimics what each drain task does at connection start: CREATE TEMP
        // TABLE pg_temp.X_inflight (...). If the embedded SQL diverges from
        // substrate column shape, this fails with a clear column-mismatch
        // error instead of a mid-flight COPY failure.
        string[] kinds =
        [
            "entity", "entity_classification", "edge", "edge_member",
            "junction", "physicality", "sequence",
            "entity_significance", "edge_significance", "entity_model_source",
        ];

        foreach (string kind in kinds)
        {
            string resourcePath = $"Hartonomous.Engine.Ingestion.Sql.{kind}.temp.sql";
            string sql = ReadEmbeddedFromEngine(resourcePath);
            Assert.False(string.IsNullOrWhiteSpace(sql), $"missing temp.sql resource for kind '{kind}'");
            // The temp.sql is a CREATE TEMP TABLE statement. Running it once
            // per connection is the production pattern; running it here
            // verifies the column list compiles against current schema.
            await using Npgsql.NpgsqlConnection conn = new(_fx.ConnectionString);
            await conn.OpenAsync();
            await using Npgsql.NpgsqlCommand cmd = new(sql, conn);
            cmd.CommandTimeout = 10;
            await cmd.ExecuteNonQueryAsync();
            // Drop on connection close — implicit, since pg_temp is session-local.
        }
    }

    [Fact]
    public async Task DrainSql_AllKinds_ParseWithoutError()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // For each kind, prepare the .drain.sql against the live DB. EXPLAIN
        // forces the planner to walk the statement, catching schema drift
        // (missing column on substrate.X, mistyped function call, missing
        // attestation_type_id propagation, etc.) without actually inserting.
        string[] kinds =
        [
            "entity", "entity_classification", "edge", "edge_member",
            "junction", "physicality", "sequence",
            "entity_significance", "edge_significance", "entity_model_source",
        ];

        foreach (string kind in kinds)
        {
            string resourcePath = $"Hartonomous.Engine.Ingestion.Sql.{kind}.drain.sql";
            string sql = ReadEmbeddedFromEngine(resourcePath);
            Assert.False(string.IsNullOrWhiteSpace(sql), $"missing drain.sql resource for kind '{kind}'");

            // The drain reads from pg_temp.X_inflight which doesn't exist on
            // a fresh connection; create it first by running temp.sql, then
            // EXPLAIN the drain. EXPLAIN doesn't execute — it just validates.
            string tempSql = ReadEmbeddedFromEngine($"Hartonomous.Engine.Ingestion.Sql.{kind}.temp.sql");

            await using Npgsql.NpgsqlConnection conn = new(_fx.ConnectionString);
            await conn.OpenAsync();
            await using (Npgsql.NpgsqlCommand t = new(tempSql, conn))
            {
                await t.ExecuteNonQueryAsync();
            }
            await using Npgsql.NpgsqlCommand explain = new($"EXPLAIN {sql}", conn);
            explain.CommandTimeout = 10;
            try
            {
                await explain.ExecuteScalarAsync();
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
            {
                // partition table missing — hard schema drift.
                Assert.Fail($"drain.sql for '{kind}' references missing relation: {ex.MessageText}");
            }
        }
    }

    private static string ReadEmbeddedFromEngine(string resourceName)
    {
        // Pull from the Engine assembly the same way IngestionSql.Read does
        // at runtime, so we exercise the actual production code path.
        System.Reflection.Assembly engine = typeof(Hartonomous.Engine.Ingestion.StreamingIngestionPipeline).Assembly;
        string? matched = Array.Find(engine.GetManifestResourceNames(),
            name => name.EndsWith($".{resourceName.Split('.')[^2]}.{resourceName.Split('.')[^1]}", StringComparison.Ordinal));
        if (matched is null)
        {
            // Fallback: exact match.
            matched = Array.Find(engine.GetManifestResourceNames(),
                name => name.Equals(resourceName, StringComparison.Ordinal));
        }
        Assert.NotNull(matched);
        using System.IO.Stream stream = engine.GetManifestResourceStream(matched)!;
        using System.IO.StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
