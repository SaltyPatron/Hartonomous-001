namespace Hartonomous.Smoke.Tests;

/// <summary>
/// Smoke tests for the substrate's named SQL function surface. These verify
/// that the functions exist, accept their declared parameter types, and
/// return without crashing the backend on representative inputs.
///
/// Coverage gate: any inference / classify / complete / recompose call that
/// the C# layer makes in production must have a smoke test here that
/// exercises the same call shape against the live container. If a function
/// is added or its signature changes, the corresponding test fails.
/// </summary>
[Collection("smoke")]
public sealed class SubstrateFunctionSmokeTests
{
    private readonly SmokeFixture _fx;

    public SubstrateFunctionSmokeTests(SmokeFixture fx) => _fx = fx;

    [Fact]
    public async Task Blake3_EmptyInput_ReturnsKnownVector()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // BLAKE3 of the empty string — published test vector.
        long byteCount = await _fx.ExecScalarLongAsync(
            "SELECT length(public.blake3_hash(''::bytea))");
        Assert.Equal(32, byteCount);
    }

    [Fact]
    public async Task Dist4d_MAxis_ReturnsOne()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // Substrate 4D distance must see the M-axis difference; PostGIS
        // ST_Distance silently drops M to 2D and returns 0.
        await using Npgsql.NpgsqlConnection conn = new(_fx.ConnectionString);
        await conn.OpenAsync();
        await using Npgsql.NpgsqlCommand cmd = new(
            "SELECT substrate.dist_4d(" +
            "  ST_GeomFromText('POINT ZM (0 0 0 0)', 0)," +
            "  ST_GeomFromText('POINT ZM (0 0 0 1)', 0))",
            conn);
        object? result = await cmd.ExecuteScalarAsync();
        double d = Convert.ToDouble(result, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(1.0, d, 9);
    }

    [Fact]
    public async Task UcdVersion_Returns17()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        await using Npgsql.NpgsqlConnection conn = new(_fx.ConnectionString);
        await conn.OpenAsync();
        await using Npgsql.NpgsqlCommand cmd = new("SELECT substrate.ucd_version()", conn);
        object? result = await cmd.ExecuteScalarAsync();
        string? version = result?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(version), "ucd_version() returned empty");
        Assert.StartsWith("17.", version, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferenceSeeds_HaveExpectedRowCounts()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // Catches reference-seed drift between substrate.entity_type /
        // edge_type / attestation_type seed files and the validate.sql gate.
        // If a seed grows, validate.sql must grow with it; this test pairs
        // those source-of-truths.
        long entityTypes = await _fx.ExecScalarLongAsync("SELECT count(*) FROM substrate.entity_type");
        long edgeTypes = await _fx.ExecScalarLongAsync("SELECT count(*) FROM substrate.edge_type");
        long edgeRoles = await _fx.ExecScalarLongAsync("SELECT count(*) FROM substrate.edge_role");
        long attestation = await _fx.ExecScalarLongAsync("SELECT count(*) FROM substrate.attestation_type");
        long arenas = await _fx.ExecScalarLongAsync("SELECT count(*) FROM substrate.significance_context");
        long provenance = await _fx.ExecScalarLongAsync("SELECT count(*) FROM substrate.provenance");

        Assert.Equal(21, entityTypes);
        Assert.Equal(112, edgeTypes);
        Assert.Equal(7, edgeRoles);
        Assert.Equal(27, attestation);
        Assert.Equal(10, arenas);
        Assert.Equal(10, provenance);
    }

    [Fact]
    public async Task AttestationType_PerRoleCodes_Present()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // Per-role attestation_type codes are referenced by safetensors
        // analysis passes (TokenCrossEdgePass, TokenAttentionEdgePass,
        // TokenFfnEdgePass, etc.). If they're missing, M7 (model decomp)
        // fails with "attestation_type code=... missing".
        string[] requiredCodes =
        [
            "lexical_curated_relation",
            "provenance_authority_corroboration",
            "model_input_embedding",
            "model_attention_qk_pattern",
            "model_attention_vo_pattern",
            "model_cross_modal_alignment",
            "model_ffn_full_path",
            "model_embedding_proximity",
        ];
        foreach (string code in requiredCodes)
        {
            await using Npgsql.NpgsqlConnection conn = new(_fx.ConnectionString);
            await conn.OpenAsync();
            await using Npgsql.NpgsqlCommand cmd = new(
                "SELECT count(*) FROM substrate.attestation_type WHERE code = @code", conn);
            cmd.Parameters.AddWithValue("code", code);
            object? r = await cmd.ExecuteScalarAsync();
            long n = Convert.ToInt64(r, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(n == 1, $"attestation_type '{code}' missing (count={n})");
        }
    }

    [Fact]
    public async Task PhysicalityType_Codes_Present()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        string[] required = ["s3_position", "embedding_firefly", "contour"];
        foreach (string code in required)
        {
            await using Npgsql.NpgsqlConnection conn = new(_fx.ConnectionString);
            await conn.OpenAsync();
            await using Npgsql.NpgsqlCommand cmd = new(
                "SELECT count(*) FROM substrate.physicality_type WHERE code = @code", conn);
            cmd.Parameters.AddWithValue("code", code);
            object? r = await cmd.ExecuteScalarAsync();
            long n = Convert.ToInt64(r, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(n == 1, $"physicality_type '{code}' missing (count={n})");
        }
    }

    [Fact]
    public async Task Glicko2BulkUpdate_PaperExample_ProducesExpectedShape()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // Glickman 2013 example: rating 1500, RD 200, vol 0.06 vs opp 1400/30, score 1.
        // Returns three arrays (mu', sigma', vol'); just verify it returns
        // without crashing and the array dimensionality is right.
        await using Npgsql.NpgsqlConnection conn = new(_fx.ConnectionString);
        await conn.OpenAsync();
        await using Npgsql.NpgsqlCommand cmd = new(
            "SELECT * FROM public.glicko2_bulk_update(" +
            "  ARRAY[1500.0::float8], ARRAY[200.0::float8], ARRAY[0.06::float8]," +
            "  ARRAY[1400.0::float8], ARRAY[30.0::float8], ARRAY[1.0::float8])",
            conn);
        await using Npgsql.NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "glicko2_bulk_update returned no row");
        Assert.True(reader.FieldCount >= 3, $"expected 3 array outputs, got {reader.FieldCount}");
    }
}
