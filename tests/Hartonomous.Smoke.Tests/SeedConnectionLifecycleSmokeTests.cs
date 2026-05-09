using Npgsql;

namespace Hartonomous.Smoke.Tests;

/// <summary>
/// Smoke tests that mirror the EXACT connection-lifecycle pattern of the
/// production seed scripts. The earlier UcdSrfSmokeTests opened a fresh
/// connection per chunk via SmokeFixture.ExecScalarLongAsync, which masked
/// a real bug class: PG backend state accumulating chunk-by-chunk inside a
/// single long-lived connection until something tips it into recovery mode.
///
/// Specifically, scripts/seed/Ucd.ps1 calls
/// substrate.populate_codepoint_property_range_from_ext for every 32k chunk
/// in a single psql session, then calls populate_codepoint_atoms_chunk × 8
/// across 8 parallel sessions. The smoke harness must reproduce both
/// patterns exactly so any same-connection accumulation bug or parallel-
/// SRF race surfaces here in seconds, not in a 25-second seed run.
///
/// 2026-05-08 evidence: bndk711cv background seed crashed UCD into
/// "database system is in recovery mode" mid-run despite UcdSrfSmokeTests
/// passing. The fresh-per-call fixture pattern is structurally blind to
/// this bug class. These tests use one Npgsql connection across the entire
/// chunked sweep to mirror Ucd.ps1's Invoke-Psql helper.
/// </summary>
[Collection("smoke")]
public sealed class SeedConnectionLifecycleSmokeTests
{
    private readonly SmokeFixture _fx;

    public SeedConnectionLifecycleSmokeTests(SmokeFixture fx) => _fx = fx;

    [Fact]
    public async Task PopulateCodepointPropertyRange_AllChunks_OneConnection_NoRecoveryMode()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // Mirror Ucd.ps1's Invoke-ChunkedCodepointSql: one connection,
        // 32k chunks across 0..1114112, sequentially. If any chunk causes
        // a backend crash that throws the cluster into recovery, the next
        // chunk's call here fails with "the database system is in
        // recovery mode" and this test surfaces it in seconds.
        await using NpgsqlConnection conn = new(_fx.ConnectionString);
        await conn.OpenAsync();

        const int chunkSize = 32768;
        const int max = 1114112;
        for (int lo = 0; lo < max; lo += chunkSize)
        {
            int hi = Math.Min(lo + chunkSize, max);
            await using NpgsqlCommand cmd = new(
                $"SELECT substrate.populate_codepoint_property_range_from_ext({lo}, {hi - lo})",
                conn);
            cmd.CommandTimeout = 60;
            try
            {
                object? r = await cmd.ExecuteScalarAsync();
                Assert.NotNull(r);
            }
            catch (NpgsqlException ex)
            {
                Assert.Fail(
                    $"chunk [{lo},{hi}) crashed the backend on a single-connection sweep: " +
                    $"{ex.Message}");
            }
        }
    }

    [Fact]
    public async Task PopulateCodepointAtomsChunk_EightParallel_SeparateConnections_NoRecoveryMode()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // Mirror Ucd.ps1's ForEach-Object -Parallel pattern: 8 disjoint
        // chunks running concurrently, each on its own connection. If any
        // backend SIGSEGVs the postmaster aborts every other backend; the
        // surviving tasks then see "the database system is in recovery
        // mode" on their next query.
        const int max = 1114112;
        const int degree = 8;
        int chunkSize = (int)Math.Ceiling((double)max / degree);

        Task[] tasks = new Task[degree];
        for (int i = 0; i < degree; i++)
        {
            int lo = i * chunkSize;
            int hi = Math.Min(lo + chunkSize, max);
            tasks[i] = Task.Run(async () =>
            {
                await using NpgsqlConnection conn = new(_fx.ConnectionString);
                await conn.OpenAsync();
                await using NpgsqlCommand cmd = new(
                    $"SELECT substrate.populate_codepoint_atoms_chunk(" +
                    $"'unicode_consortium'::text, NULL::float8, {lo}::int, {hi}::int)",
                    conn);
                cmd.CommandTimeout = 120;
                await cmd.ExecuteScalarAsync();
            });
        }
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task FullUcdSeedSequence_OneConnection_PropertyRangeThenAtoms()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // The full Ucd.ps1 sequence on one connection: every property
        // range chunk first, then the atom chunks. Catches accumulation
        // bugs that span phase boundaries inside a single backend.
        await using NpgsqlConnection conn = new(_fx.ConnectionString);
        await conn.OpenAsync();

        const int chunkSize = 32768;
        const int max = 1114112;

        for (int lo = 0; lo < max; lo += chunkSize)
        {
            int hi = Math.Min(lo + chunkSize, max);
            await using NpgsqlCommand cmd = new(
                $"SELECT substrate.populate_codepoint_property_range_from_ext({lo}, {hi - lo})",
                conn);
            cmd.CommandTimeout = 60;
            await cmd.ExecuteScalarAsync();
        }

        // After all property-range chunks land, run a small atom chunk
        // on the SAME connection. If state from the property-range
        // sweep corrupted backend memory, the atom call surfaces it.
        await using NpgsqlCommand atomCmd = new(
            "SELECT substrate.populate_codepoint_atoms_chunk(" +
            "'unicode_consortium'::text, NULL::float8, 0::int, 32768::int)",
            conn);
        atomCmd.CommandTimeout = 120;
        await atomCmd.ExecuteScalarAsync();
    }
}
