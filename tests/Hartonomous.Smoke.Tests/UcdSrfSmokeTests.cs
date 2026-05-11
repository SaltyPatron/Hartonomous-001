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
    [Trait("Category", "SeedValidation")]
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
    [Trait("Category", "SeedMutation")]
    public async Task PopulateCodepointAtomsChunk_HighRange_Succeeds()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // Chunk that previously crashed under parallelism. Single-call here
        // verifies the SRF body itself — parallel invariants tested separately.
        long inserted = await _fx.ExecScalarLongAsync(
            "SELECT substrate.populate_codepoint_atoms_chunk('unicode_consortium'::text, NULL::float8, 819200::int, 820224::int)");
        Assert.True(inserted > 0);
    }

    [Fact]
    [Trait("Category", "SeedValidation")]
    public async Task SeededCodepointClassifications_FullRange_HasNoGaps()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        long classified = await _fx.ExecScalarLongAsync(
            "SELECT count(*) " +
            "FROM substrate.entity_classification ec " +
            "JOIN substrate.entity_type et ON et.id = ec.entity_type_id " +
            "JOIN substrate.provenance p ON p.id = ec.provenance_id " +
            "WHERE et.code = 'codepoint' AND p.code = 'unicode_consortium'");
        Assert.Equal(1114112, classified);
    }

    [Fact]
    [Trait("Category", "SeedValidation")]
    public async Task SeededCodepointProperties_FullRange_HasNoDanglingEntityReferences()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        long propertyRows = await _fx.ExecScalarLongAsync("SELECT count(*) FROM substrate.codepoint_property");
        Assert.Equal(1114112, propertyRows);

        long dangling = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM substrate.codepoint_property cp " +
            "WHERE NOT EXISTS (SELECT 1 FROM substrate.entity e WHERE e.hash = cp.entity_hash)");
        Assert.Equal(0, dangling);
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
