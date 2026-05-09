using System.Globalization;
using Npgsql;

namespace Hartonomous.Smoke.Tests;

/// <summary>
/// xUnit collection fixture that owns one bootstrapped substrate database for
/// the entire smoke run. Every test reuses the same DB — smoke tests must be
/// idempotent (use ON CONFLICT DO NOTHING semantics) and assert against
/// per-test sentinel codepoint ranges that don't collide.
///
/// Connection string is <c>HARTONOMOUS_DB</c> env var, falling back to the
/// docker-compose default. Tests are skipped if no DB is reachable so a
/// developer running `dotnet test` without docker doesn't see false failures.
/// </summary>
public sealed class SmokeFixture : IAsyncLifetime
{
    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("HARTONOMOUS_DB")
            ?? "Host=localhost;Port=5433;Username=hartonomous;Password=hartonomous;Database=hartonomous";

    public bool DbReachable { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            await using NpgsqlConnection conn = new(ConnectionString);
            await conn.OpenAsync();
            await using NpgsqlCommand cmd = new(
                "SELECT 1 FROM pg_extension WHERE extname='hartonomous'", conn);
            object? result = await cmd.ExecuteScalarAsync();
            DbReachable = result is not null;
        }
        catch
        {
            DbReachable = false;
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public async Task<long> ExecScalarLongAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection conn = new(ConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        foreach ((string name, object value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        cmd.CommandTimeout = 60;
        object? result = await cmd.ExecuteScalarAsync();
        return result switch
        {
            long l => l,
            int i => i,
            null => 0L,
            _ => Convert.ToInt64(result, CultureInfo.InvariantCulture),
        };
    }

    public async Task ExecAsync(string sql)
    {
        await using NpgsqlConnection conn = new(ConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.CommandTimeout = 60;
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("smoke")]
#pragma warning disable CA1711 // xUnit convention: collection-definition class names end in "Collection".
public sealed class SmokeFixtureCollectionDefinition : ICollectionFixture<SmokeFixture>
#pragma warning restore CA1711
{
}
