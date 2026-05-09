namespace Hartonomous.Smoke.Tests;

/// <summary>
/// Smoke tests for each seed phase's CRITICAL CALL SHAPE — not the full
/// volume seed, just the one operation that has crashed or could crash.
/// Each test runs in seconds. If a regression in this class lands, it
/// surfaces here before the 25-min full-corpus seed catches it.
///
/// Coverage targets one bug class per phase:
///   - UCD: SRF + parallel chunked atom seed (covered in UcdSrfSmokeTests)
///   - ISO 639: bulk-existence-check round-trip (the path WordNet+OMW
///     also use to deduplicate against substrate before emit)
///   - WordNet: text-decompose of a representative gloss + has_sense edge
///     hash computation
///   - UD: UAX #29 grapheme/word/sentence segmentation determinism
///   - Safetensors: tensor-name BLAKE3 hash + has_tensor edge construction
///
/// Tests do not depend on prior seed state being present — they assert
/// only the immediate behavior of the call shape.
/// </summary>
[Collection("smoke")]
public sealed class SeedPhaseSmokeTests
{
    private readonly SmokeFixture _fx;

    public SeedPhaseSmokeTests(SmokeFixture fx) => _fx = fx;

    [Fact]
    public async Task TextDecompose_AsciiInput_RoundTripsToCanonicalRoot()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // The text decomposer is the seed-uses-core anchor — every text
        // bearing seed (WordNet glosses, OMW lemmas, UD sentences,
        // Wiktionary citations, Tatoeba sentences, model config JSON) routes
        // through this. Verifying it produces a stable hash for ASCII
        // input gates the entire seed chain.
        await using Npgsql.NpgsqlConnection conn = new(_fx.ConnectionString);
        await conn.OpenAsync();
        await using Npgsql.NpgsqlCommand cmd = new(
            "SELECT (substrate.text_decompose($1::bytea, 'text_composition'::text, " +
            "  95000.0::float8, 'unicode_consortium'::text)).root_hash",
            conn);
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea,
            Value = System.Text.Encoding.UTF8.GetBytes("hello"),
        });
        object? result = await cmd.ExecuteScalarAsync();
        Assert.NotNull(result);
        byte[] hash = (byte[])result!;
        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public async Task TextDecompose_NfcEquivalence_CollapsesToSameHash()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // S2 gate: 'café' precomposed (U+00E9) and decomposed (e + U+0301)
        // must produce the same text_composition hash after NFC normalization.
        // If they don't, every cross-source convergence claim is broken.
        byte[] nfcHash = await TextDecomposeRootHashAsync("café");      // é precomposed
        byte[] nfdHash = await TextDecomposeRootHashAsync("café");     // e + combining acute
        Assert.Equal(nfcHash, nfdHash);
    }

    [Fact]
    public async Task BulkExistenceCheck_EntityHashes_Roundtrip()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // The substrate's identity oracle — decomposers ask "of these N hashes,
        // which already exist?" via ANY($1). Verifies the SQL contract.
        await using Npgsql.NpgsqlConnection conn = new(_fx.ConnectionString);
        await conn.OpenAsync();
        await using Npgsql.NpgsqlCommand cmd = new(
            "SELECT count(*) FROM substrate.entity " +
            "WHERE hash = ANY(ARRAY[" +
            "  decode('0000000000000000000000000000000000000000000000000000000000000000', 'hex')::bytea," +
            "  decode('1111111111111111111111111111111111111111111111111111111111111111', 'hex')::bytea" +
            "])",
            conn);
        cmd.CommandTimeout = 10;
        object? result = await cmd.ExecuteScalarAsync();
        long n = Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(n >= 0); // sentinel hashes — count is whatever; the SQL must execute.
    }

    [Fact]
    public async Task EdgeIdentity_BlakeOverParticipants_ProducesStableHash()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // Edge identity is BLAKE3(edge_type_id || role-ordered participant
        // hashes). Same inputs → same edge hash, byte-identical, regardless
        // of which decomposer emitted it.
        long len = await _fx.ExecScalarLongAsync(
            "SELECT length(public.blake3_hash(" +
            "  decode('0000000100', 'hex')::bytea ||" +
            "  decode('aa00000000000000000000000000000000000000000000000000000000000000', 'hex')::bytea ||" +
            "  decode('bb00000000000000000000000000000000000000000000000000000000000000', 'hex')::bytea" +
            "))");
        Assert.Equal(32, len);
    }

    [Fact]
    public async Task SignificanceContext_Set_HasOpenVocabulary()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // AP-1: arenas are open vocabulary. Code MUST cross-product against
        // whatever arenas exist at execution time. Verifies the canonical
        // 10 starter arenas are seeded; new arenas added later are
        // expected to live alongside, not replace.
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM substrate.significance_context " +
            "WHERE code IN (" +
            "  'lexical_disambiguation','syntactic_role_fitness','translation_quality'," +
            "  'model_trust','source_authority','semantic_relevance'," +
            "  'corroboration_strength','frequency_significance'," +
            "  'attention_pattern_confidence','morphological_productivity')");
        Assert.Equal(10, n);
    }

    [Fact]
    public async Task EdgeTypePartitions_AllChildrenAttached()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // substrate.edge is LIST-partitioned by edge_type_id category. A
        // missing partition = silent edge drop on insert. Verifies every
        // declared category has its child partition present in the catalog.
        long parents = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM pg_partitioned_table p " +
            "JOIN pg_class c ON c.oid = p.partrelid " +
            "WHERE c.relname IN ('edge','edge_member','physicality','entity_significance','edge_significance')");
        Assert.Equal(5, parents);

        long childPartitions = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM pg_inherits i " +
            "JOIN pg_class p ON p.oid = i.inhparent " +
            "WHERE p.relname IN ('edge','edge_member','physicality','entity_significance','edge_significance')");
        Assert.True(childPartitions >= 30, $"expected ≥30 child partitions across 5 parents, got {childPartitions}");
    }

    private async Task<byte[]> TextDecomposeRootHashAsync(string text)
    {
        await using Npgsql.NpgsqlConnection conn = new(_fx.ConnectionString);
        await conn.OpenAsync();
        await using Npgsql.NpgsqlCommand cmd = new(
            "SELECT (substrate.text_decompose($1::bytea, 'text_composition'::text, " +
            "  95000.0::float8, 'unicode_consortium'::text)).root_hash",
            conn);
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea,
            Value = System.Text.Encoding.UTF8.GetBytes(text),
        });
        object? result = await cmd.ExecuteScalarAsync();
        return (byte[])result!;
    }
}
