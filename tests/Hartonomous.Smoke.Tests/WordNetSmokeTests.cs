namespace Hartonomous.Smoke.Tests;

/// <summary>
/// Smoke tests for WordNet's substrate-side contracts. WordNet is the
/// largest seed by emission volume (~117k synsets × multi-edge fan-out);
/// drift in any of these contracts breaks the M4 lexical-backbone gate.
/// </summary>
[Collection("smoke")]
public sealed class WordNetSmokeTests
{
    private readonly SmokeFixture _fx;

    public WordNetSmokeTests(SmokeFixture fx) => _fx = fx;

    [Fact]
    public async Task SynsetEntityType_Seeded()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM substrate.entity_type WHERE code = 'synset'");
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task LemmaAndWordFormTypes_Seeded()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM substrate.entity_type WHERE code IN ('lemma','word_form')");
        Assert.Equal(2, n);
    }

    [Fact]
    public async Task HasSenseEdgeType_Seeded()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM substrate.edge_type WHERE code = 'has_sense'");
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task HypernymHyponymHolonymMeronymEdgeTypes_AllSeeded()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // WordNet's 24 pointer-relation types map onto edge_type entries.
        // Missing one = silent edge drop on the corresponding pointer record.
        string[] required =
        [
            "hypernym", "hyponym",
            "member_holonym", "substance_holonym", "part_holonym",
            "member_meronym", "substance_meronym", "part_meronym",
            "antonym", "also_see", "similar_to",
        ];
        foreach (string code in required)
        {
            await using Npgsql.NpgsqlConnection conn = new(_fx.ConnectionString);
            await conn.OpenAsync();
            await using Npgsql.NpgsqlCommand cmd = new(
                "SELECT count(*) FROM substrate.edge_type WHERE code = @code", conn);
            cmd.Parameters.AddWithValue("code", code);
            object? r = await cmd.ExecuteScalarAsync();
            long n = Convert.ToInt64(r, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(n == 1, $"edge_type '{code}' missing");
        }
    }

    [Fact]
    public async Task LexnameTable_Has45Entries()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // WordNet 3.0 has exactly 45 lex-files (lexnames). Drift here
        // means the synset → lexname junction breaks for some synsets.
        long n = await _fx.ExecScalarLongAsync("SELECT count(*) FROM substrate.lexname");
        Assert.Equal(45, n);
    }

    [Fact]
    public async Task PosTable_HasFullUdInventory()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // WordNet maps SsType (n/v/a/r/s) to UD POS via PosCharToUdPos.
        // The UD POS inventory must include NOUN/VERB/ADJ/ADV at minimum.
        string[] required = ["NOUN", "VERB", "ADJ", "ADV"];
        foreach (string code in required)
        {
            await using Npgsql.NpgsqlConnection conn = new(_fx.ConnectionString);
            await conn.OpenAsync();
            await using Npgsql.NpgsqlCommand cmd = new(
                "SELECT count(*) FROM substrate.pos WHERE code = @code", conn);
            cmd.Parameters.AddWithValue("code", code);
            object? r = await cmd.ExecuteScalarAsync();
            long n = Convert.ToInt64(r, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(n == 1, $"pos '{code}' missing");
        }
    }
}
