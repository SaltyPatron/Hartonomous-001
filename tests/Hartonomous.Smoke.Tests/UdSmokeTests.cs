namespace Hartonomous.Smoke.Tests;

/// <summary>
/// Smoke tests for Universal Dependencies — verifies the substrate's
/// contracts for syntactic seeding without requiring an actual treebank
/// to be parsed. The UD decomposer's hot path uses these contracts on
/// every CoNLL-U row; a missing one would crash the seed mid-treebank.
/// </summary>
[Collection("smoke")]
public sealed class UdSmokeTests
{
    private readonly SmokeFixture _fx;

    public UdSmokeTests(SmokeFixture fx) => _fx = fx;

    [Fact]
    public async Task DeprelTable_Exists()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM information_schema.tables " +
            "WHERE table_schema = 'substrate' AND table_name = 'deprel'");
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task MorphFeatureTable_Exists()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM information_schema.tables " +
            "WHERE table_schema = 'substrate' AND table_name = 'morph_feature'");
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task EntityPos_HasGlickoColumns()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // entity_pos is one of two Glicko-bearing junctions per the rules.
        // Verifies the mu / attestation_type_id columns the UD decomposer
        // populates on every token (and WordNet on every lemma).
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_schema = 'substrate' AND table_name = 'entity_pos' " +
            "AND column_name IN ('mu','attestation_type_id')");
        Assert.Equal(2, n);
    }

    [Fact]
    public async Task PatternDeprel_HasGlickoColumns()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // pattern_deprel is the second Glicko-bearing junction.
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_schema = 'substrate' AND table_name = 'pattern_deprel' " +
            "AND column_name IN ('mu','attestation_type_id')");
        Assert.Equal(2, n);
    }

    [Fact]
    public async Task SyntacticEdgeCategory_PartitionExists()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // UD emits dependency edges into the 'syntactic' edge category
        // partition. If the partition is missing, every UD row INSERT
        // routes to the default partition and silently loses category
        // pruning at query time.
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM pg_class WHERE relname = 'edge_structural'");
        Assert.True(n >= 1, "edge_structural partition missing");
    }
}
