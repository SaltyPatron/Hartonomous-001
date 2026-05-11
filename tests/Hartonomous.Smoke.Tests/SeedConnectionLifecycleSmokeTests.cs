using Npgsql;

namespace Hartonomous.Smoke.Tests;

/// <summary>
/// Smoke tests for seed implementation pieces and connection-lifecycle edge
/// cases. They intentionally do not run a full seed; full seeded-state checks
/// are read-only validation tests marked Category=SeedValidation.
///
/// The cases below use small and previously-problematic ranges only. That
/// verifies the populator contracts, ordering, FK behavior, and backend
/// connection lifecycle without turning the test suite into another seeder.
/// </summary>
[Collection("smoke")]
public sealed class SeedConnectionLifecycleSmokeTests
{
    private readonly SmokeFixture _fx;

    public SeedConnectionLifecycleSmokeTests(SmokeFixture fx) => _fx = fx;

    [Fact]
    [Trait("Category", "SeedMutation")]
    public async Task PopulateCodepointPropertyRange_TargetedChunks_OneConnection_NoRecoveryMode()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        await using NpgsqlConnection conn = new(_fx.ConnectionString);
        await conn.OpenAsync();

        string[] setup =
        [
            "SELECT substrate.populate_general_categories_from_ext()",
            "SELECT substrate.populate_scripts_from_ext()",
            "SELECT substrate.populate_blocks_from_ext()",
            "SELECT substrate.populate_break_properties_from_ext()",
        ];
        foreach (string sql in setup)
        {
            await using NpgsqlCommand cmd = new(sql, conn);
            cmd.CommandTimeout = 60;
            await cmd.ExecuteScalarAsync();
        }

        int[] starts = [0, 819200];
        foreach (int lo in starts)
        {
            await ExecuteScalarAsync(conn,
                $"SELECT substrate.populate_codepoint_atoms_chunk('unicode_consortium'::text, NULL::float8, {lo}::int, {lo + 1024}::int)");
            object? r = await ExecuteScalarAsync(conn,
                $"SELECT substrate.populate_codepoint_property_range_from_ext({lo}, {lo + 1024})");
            Assert.NotNull(r);
        }
    }

    [Fact]
    [Trait("Category", "SeedMutation")]
    public async Task PopulateCodepointAtomsChunk_TargetedParallelRanges_SeparateConnections_NoRecoveryMode()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        int[] starts = [0, 32768, 819200, 851968];
        Task[] tasks = starts.Select(async lo =>
        {
            await using NpgsqlConnection conn = new(_fx.ConnectionString);
            await conn.OpenAsync();
            await ExecuteScalarAsync(conn,
                $"SELECT substrate.populate_codepoint_atoms_chunk('unicode_consortium'::text, NULL::float8, {lo}::int, {lo + 1024}::int)");
        }).ToArray();

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task UcdPieceSequence_AtomsBeforeProperties_SameConnection()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        await using NpgsqlConnection conn = new(_fx.ConnectionString);
        await conn.OpenAsync();

        await ExecuteScalarAsync(conn, "SELECT substrate.populate_general_categories_from_ext()");
        await ExecuteScalarAsync(conn, "SELECT substrate.populate_scripts_from_ext()");
        await ExecuteScalarAsync(conn, "SELECT substrate.populate_blocks_from_ext()");
        await ExecuteScalarAsync(conn, "SELECT substrate.populate_break_properties_from_ext()");
        await ExecuteScalarAsync(conn,
            "SELECT substrate.populate_codepoint_atoms_chunk('unicode_consortium'::text, NULL::float8, 0::int, 1024::int)");
        await ExecuteScalarAsync(conn, "SELECT substrate.populate_codepoint_property_range_from_ext(0, 1024)");

        long count = await CountAsync(conn, "SELECT count(*) FROM substrate.codepoint_property WHERE codepoint_value < 1024");
        Assert.True(count >= 1024);
    }

    private static async Task<object?> ExecuteScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using NpgsqlCommand cmd = new(sql, conn);
        cmd.CommandTimeout = 60;
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<long> CountAsync(NpgsqlConnection conn, string sql)
    {
        object? result = await ExecuteScalarAsync(conn, sql);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}
