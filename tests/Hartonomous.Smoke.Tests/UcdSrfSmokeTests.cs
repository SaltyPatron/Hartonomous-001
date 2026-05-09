using Npgsql;

namespace Hartonomous.Smoke.Tests;

/// <summary>
/// Smoke tests for the embedded UCD SRF — substrate.ucd_codepoints — and the
/// two seed callers that previously crashed the backend with SIGSEGV under
/// specific codepoint ranges.
///
/// Each test runs against a tiny range so a regression surfaces in
/// sub-second wall-clock time, not in the 25-second full UCD seed.
///
/// Crash history these tests guard against:
///   - 2026-05-08 PID 1917 SIGSEGV in ucd_codepoints during parallel chunked
///     seed (8-way concurrent populate_codepoint_atoms_chunk).
///   - 2026-05-08 PID 3248 SIGSEGV at chunk [819200, 851968) in
///     populate_codepoint_property_range_from_ext (single-backend).
///
/// The tests assert no backend death and expected row deltas. If the SRF
/// SIGSEGVs again, Npgsql sees the connection drop and the test fails with
/// "server closed the connection unexpectedly" — the exact signature.
/// </summary>
[Collection("smoke")]
public sealed class UcdSrfSmokeTests
{
    private readonly SmokeFixture _fx;

    public UcdSrfSmokeTests(SmokeFixture fx) => _fx = fx;

    [Fact]
    public async Task UcdCodepoints_Srf_RangeStart_DoesNotCrash()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM substrate.ucd_codepoints(0, 1024)");
        Assert.Equal(1024, n);
    }

    [Fact]
    public async Task UcdCodepoints_Srf_HighRange_DoesNotCrash()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // The range that caught the second crash on
        // populate_codepoint_property_range_from_ext: [819200, 851968).
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM substrate.ucd_codepoints(819200, 32768)");
        Assert.True(n >= 0, "ucd_codepoints SRF returned successfully on the previously-crashing range");
    }

    [Fact]
    public async Task UcdCodepoints_Srf_FullRange_SweepsWithoutCrash()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // Sweep every 32k chunk of the 0..0x10FFFF codespace. Counts the rows
        // returned per chunk; the body of the SRF must execute for every
        // codepoint without tripping the in-extension SIGSEGV handler.
        // If any chunk crashes the backend, Npgsql throws and this test fails.
        const int chunk = 32768;
        const int max = 1114112;
        for (int lo = 0; lo < max; lo += chunk)
        {
            int hi = Math.Min(lo + chunk, max);
            long n = await _fx.ExecScalarLongAsync(
                $"SELECT count(*) FROM substrate.ucd_codepoints({lo}, {hi - lo})");
            Assert.True(n >= 0, $"chunk [{lo},{hi}) returned");
        }
    }

    [Fact]
    public async Task PopulateCodepointAtomsChunk_SmallRange_Succeeds()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        long inserted = await _fx.ExecScalarLongAsync(
            "SELECT substrate.populate_codepoint_atoms_chunk('unicode_consortium'::text, NULL::float8, 0::int, 1024::int)");
        Assert.Equal(1024, inserted);
    }

    [Fact]
    public async Task PopulateCodepointAtomsChunk_HighRange_Succeeds()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // Chunk that previously crashed under parallelism. Single-call here
        // verifies the SRF body itself — parallel invariants tested separately.
        long inserted = await _fx.ExecScalarLongAsync(
            "SELECT substrate.populate_codepoint_atoms_chunk('unicode_consortium'::text, NULL::float8, 819200::int, 851968::int)");
        Assert.True(inserted > 0);
    }

    [Fact]
    public async Task PopulateCodepointAtomsChunk_EightWayParallel_DoesNotCrash()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // The exact 8-way concurrency pattern the production seed uses.
        // Each task opens its own connection; if any backend SIGSEGVs the
        // others see "server closed the connection unexpectedly" because
        // the postmaster aborts every backend on a crash of one.
        const int max = 1114112;
        const int degree = 8;
        int chunkSize = (int)Math.Ceiling((double)max / degree);
        Task<long>[] tasks = new Task<long>[degree];
        for (int i = 0; i < degree; i++)
        {
            int lo = i * chunkSize;
            int hi = Math.Min(lo + chunkSize, max);
            tasks[i] = _fx.ExecScalarLongAsync(
                $"SELECT substrate.populate_codepoint_atoms_chunk('unicode_consortium'::text, NULL::float8, {lo}::int, {hi}::int)");
        }
        long[] results = await Task.WhenAll(tasks);
        long total = results.Sum();
        Assert.True(total > 0, $"parallel 8-way insert produced {total} rows");
    }

    [Fact]
    public async Task PopulateCodepointPropertyRange_FullSweep_DoesNotCrash()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // The function that crashed at chunk [819200, 851968) on 2026-05-08.
        // Sweep every 32k chunk to verify no codepoint range trips the
        // in-extension SIGSEGV handler.
        const int chunk = 32768;
        const int max = 1114112;
        for (int lo = 0; lo < max; lo += chunk)
        {
            int hi = Math.Min(lo + chunk, max);
            long inserted = await _fx.ExecScalarLongAsync(
                $"SELECT substrate.populate_codepoint_property_range_from_ext({lo}, {hi - lo})");
            Assert.True(inserted >= 0, $"chunk [{lo},{hi}) returned");
        }
    }
}

/// <summary>
/// Inline minimal "skip when DB unreachable" — when the substrate Docker
/// container isn't running, all DB-dependent tests in this project bail out
/// with this exception instead of failing. xUnit treats unhandled exceptions
/// as failures by default; this exception's message is what the test runner
/// surfaces. We could pull Xunit.SkippableFact for proper Skipped status, but
/// that's another dependency for marginal value.
/// </summary>
#pragma warning disable CA1032, CA1064 // Test-only sentinel exception; standard ctors / public visibility intentional.
public sealed class SkipException(string message) : Exception(message);
#pragma warning restore CA1032, CA1064

internal static class Skip
{
    public static void IfNot(bool condition, string reason)
    {
        if (!condition)
        {
            throw new SkipException(reason);
        }
    }
}
