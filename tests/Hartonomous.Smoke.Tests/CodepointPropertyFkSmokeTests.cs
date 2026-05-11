using Npgsql;

namespace Hartonomous.Smoke.Tests;

/// <summary>
/// Smoke tests for the codepoint_property FK resolution path.
///
/// 2026-05-08 the live UCD seed crashed PG into recovery mode with a syscache
/// SIGSEGV in RI_FKey_check / get_op_opfamily_properties (core wsl-crash-
/// 1778290623-2791). Root cause: populate_codepoint_property_range_from_ext
/// used hardcoded offsets — gcb_id = a.gcb + 1, wb_id = a.wb + 15,
/// sb_id = a.sb + 35, lb_id = a.lb + 50 — that depended on a specific
/// contiguous layout in substrate.break_property. When the SRF emitted enum
/// counts that didn't match the offsets, INSERTs referenced non-existent
/// FK IDs; PG 18.3 handled the resulting catalog state with SIGSEGV instead
/// of a clean ereport FK violation.
///
/// Fix: substrate.break_property now carries enum_id; the codepoint_property
/// INSERT JOINs on (category, enum_id) instead of using offsets. These tests
/// verify the fix:
///
///   1. break_property has the enum_id column with the UNIQUE(category,
///      enum_id) constraint that lets the JOIN match exactly one parent row.
///   2. After populate_break_properties_from_ext(), every (category, enum_id)
///      pair the codepoints SRF can emit has a matching break_property row.
///   3. The full populator sequence
///      (populate_general_categories_from_ext + populate_scripts_from_ext
///       + populate_blocks_from_ext + populate_break_properties_from_ext
///       + populate_codepoint_atoms_chunk + populate_codepoint_property_range_from_ext
///       on a small chunk)
///      runs end-to-end on a single connection without crashing PG into
///      recovery mode. This is the exact path Ucd.ps1 follows; if any of
///      the populators or the FK JOINs are broken, this test surfaces it
///      in seconds, not 25 minutes.
/// </summary>
[Collection("smoke")]
public sealed class CodepointPropertyFkSmokeTests
{
    private readonly SmokeFixture _fx;

    public CodepointPropertyFkSmokeTests(SmokeFixture fx) => _fx = fx;

    [Fact]
    public async Task BreakProperty_HasEnumIdColumn_WithUniqueConstraint()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // Column present.
        long col = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_schema = 'substrate' AND table_name = 'break_property' " +
            "AND column_name = 'enum_id'");
        Assert.Equal(1, col);

        // UNIQUE(category, enum_id) constraint present — without this, the
        // JOIN in populate_codepoint_property_range_from_ext could match
        // multiple rows and silently fan out the row count.
        long uniq = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM pg_constraint c " +
            "JOIN pg_class t ON t.oid = c.conrelid " +
            "JOIN pg_namespace n ON n.oid = t.relnamespace " +
            "WHERE n.nspname = 'substrate' AND t.relname = 'break_property' " +
            "AND c.contype = 'u' AND array_length(c.conkey, 1) = 2");
        Assert.True(uniq >= 1, "substrate.break_property is missing the UNIQUE(category, enum_id) constraint");
    }

    [Fact]
    public async Task CodepointProperty_EntityHashReferencesEntityHash()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");

        long fk = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM pg_constraint c " +
            "JOIN pg_class t ON t.oid = c.conrelid " +
            "JOIN pg_namespace n ON n.oid = t.relnamespace " +
            "JOIN pg_class rt ON rt.oid = c.confrelid " +
            "JOIN pg_namespace rn ON rn.oid = rt.relnamespace " +
            "WHERE n.nspname = 'substrate' AND t.relname = 'codepoint_property' " +
            "AND rn.nspname = 'substrate' AND rt.relname = 'entity' " +
            "AND c.contype = 'f'");

        Assert.Equal(1, fk);
    }

    [Fact]
    public async Task PopulateBreakProperties_PopulatesEnumIdForEveryRow()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // Run populate then assert no row left enum_id null AND every
        // (category, enum_id) is unique. If the SRF doesn't actually emit
        // enum_id, the populator's INSERT fails the NOT NULL constraint and
        // this test surfaces it.
        await _fx.ExecAsync("SELECT substrate.populate_break_properties_from_ext()");
        long total = await _fx.ExecScalarLongAsync("SELECT count(*) FROM substrate.break_property");
        long withEnum = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM substrate.break_property WHERE enum_id IS NOT NULL");
        Assert.True(total > 0, "populate_break_properties_from_ext produced zero rows");
        Assert.Equal(total, withEnum);

        long distinct = await _fx.ExecScalarLongAsync(
            "SELECT count(DISTINCT (category, enum_id)) FROM substrate.break_property");
        Assert.Equal(total, distinct);
    }

    [Fact]
    public async Task UcdReferencePopulators_FullSequence_OneConnection_NoRecoveryMode()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // The exact populator sequence Ucd.ps1 runs in steps 1..5, on one
        // connection, against a small codepoint range. If any populator
        // fails or if the codepoint_property JOINs don't resolve, the
        // backend either ereports cleanly (caught here) or SIGSEGVs into
        // recovery (caught here on the next call's connection failure).
        await using NpgsqlConnection conn = new(_fx.ConnectionString);
        await conn.OpenAsync();

        string[] steps =
        [
            "SELECT substrate.populate_general_categories_from_ext()",
            "SELECT substrate.populate_scripts_from_ext()",
            "SELECT substrate.populate_blocks_from_ext()",
            "SELECT substrate.populate_break_properties_from_ext()",
            "SELECT substrate.populate_codepoint_atoms_chunk('unicode_consortium'::text, NULL::float8, 0::int, 1024::int)",
            // Small range; if FK lookups crash, this surfaces it. Full
            // seeded-state coverage is a read-only SeedValidation gate.
            "SELECT substrate.populate_codepoint_property_range_from_ext(0, 1024)",
        ];

        foreach (string sql in steps)
        {
            await using NpgsqlCommand cmd = new(sql, conn);
            cmd.CommandTimeout = 60;
            try
            {
                await cmd.ExecuteScalarAsync();
            }
            catch (PostgresException ex)
            {
                Assert.Fail($"{sql} failed: {ex.SqlState} {ex.MessageText}");
            }
            catch (NpgsqlException ex)
            {
                Assert.Fail(
                    $"{sql} crashed the backend (recovery mode = SIGSEGV upstream): {ex.Message}");
            }
        }
    }

    [Fact]
    public async Task CodepointProperty_JoinResolvesGcbWbSbLb_ForAsciiRange()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // After population, every codepoint_property row's FK columns must
        // point at a real break_property.id. Validates the JOIN actually
        // resolved every codepoint's gcb/wb/sb/lb against (category,
        // enum_id) — the prior offset arithmetic could silently produce
        // dangling FKs that PG would later SIGSEGV on.
        await _fx.ExecAsync("SELECT substrate.populate_general_categories_from_ext()");
        await _fx.ExecAsync("SELECT substrate.populate_scripts_from_ext()");
        await _fx.ExecAsync("SELECT substrate.populate_blocks_from_ext()");
        await _fx.ExecAsync("SELECT substrate.populate_break_properties_from_ext()");
        await _fx.ExecAsync("SELECT substrate.populate_codepoint_atoms_chunk('unicode_consortium'::text, NULL::float8, 0::int, 1024::int)");
        await _fx.ExecAsync("SELECT substrate.populate_codepoint_property_range_from_ext(0, 1024)");

        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM substrate.codepoint_property cp " +
            "WHERE NOT EXISTS (SELECT 1 FROM substrate.break_property bp WHERE bp.id = cp.gcb_id) " +
            "   OR NOT EXISTS (SELECT 1 FROM substrate.break_property bp WHERE bp.id = cp.wb_id) " +
            "   OR NOT EXISTS (SELECT 1 FROM substrate.break_property bp WHERE bp.id = cp.sb_id) " +
            "   OR NOT EXISTS (SELECT 1 FROM substrate.break_property bp WHERE bp.id = cp.lb_id)");
        Assert.Equal(0, n);

        long total = await _fx.ExecScalarLongAsync("SELECT count(*) FROM substrate.codepoint_property");
        Assert.True(total >= 1024, $"expected >= 1024 codepoint_property rows for ASCII chunk, got {total}");
    }
}
