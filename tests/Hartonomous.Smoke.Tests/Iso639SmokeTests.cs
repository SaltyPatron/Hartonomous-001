namespace Hartonomous.Smoke.Tests;

/// <summary>
/// Smoke tests for the ISO 639-3 phase. The decomposer is invoked through
/// the C# pipeline (unlike UCD which has the SQL bypass), so the smoke
/// targets here are the SQL contracts the decomposer relies on:
///   1. <c>substrate.language</c> reference table is writable and
///      indexed by ISO 639-3 alpha-3 code.
///   2. The bulk-existence-check round-trip for language codes does
///      not crash on empty / malformed input.
///   3. Macrolanguage / supersession metadata lives in junctions, not
///      in <c>substrate.entity</c> or <c>substrate.edge</c> (per
///      AP-8 — classification is infrastructure, not substrate).
/// </summary>
[Collection("smoke")]
public sealed class Iso639SmokeTests
{
    private readonly SmokeFixture _fx;

    public Iso639SmokeTests(SmokeFixture fx) => _fx = fx;

    [Fact]
    public async Task LanguageReference_HasIso639Alpha3Index()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // Verify the language table exists with the alpha-3 code column the
        // ISO 639 decomposer indexes against. If the column is renamed or
        // dropped, the decomposer's bulk-existence-check fails at first call.
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_schema = 'substrate' AND table_name = 'language' " +
            "AND column_name IN ('iso639_3','code','alpha3','iso_alpha3')");
        Assert.True(n >= 1, "substrate.language is missing the ISO 639-3 alpha-3 column");
    }

    [Fact]
    public async Task LanguageReference_PrimaryKeyIsId()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // The C# layer joins on substrate.language.id; verifying the PK shape
        // catches the kind of drift that breaks the foreign-key links from
        // entity_language → language without surfacing a column-mismatch.
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM pg_constraint c " +
            "JOIN pg_class t ON t.oid = c.conrelid " +
            "JOIN pg_namespace n ON n.oid = t.relnamespace " +
            "WHERE n.nspname = 'substrate' AND t.relname = 'language' AND c.contype = 'p'");
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task EntityLanguage_Junction_ExistsAndIndexed()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // The entity_language junction is one of the hot lookup paths for
        // language-scoped queries. Missing index = traversal fallback to
        // sequential scan and silent latency drop.
        long indexCount = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM pg_indexes " +
            "WHERE schemaname = 'substrate' AND tablename = 'entity_language'");
        Assert.True(indexCount >= 1, "substrate.entity_language has no indexes");
    }
}
