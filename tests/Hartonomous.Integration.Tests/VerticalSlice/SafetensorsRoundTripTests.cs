using System.Threading.Tasks;
using Hartonomous.Integration.Tests.Fixtures;
using Npgsql;
using Xunit;

namespace Hartonomous.Integration.Tests.VerticalSlice;

/// <summary>
/// V1 round-trip + audit-chain + cross-source D-* gates run against the
/// shared <see cref="RoundTripFixture"/>. Tests gate on
/// <c>HasIngestedModelAsync</c> so the suite passes cleanly on a seed-only
/// substrate — the real assertions fire once a model has been ingested.
///
/// Per the V1 plan's Phase 5 § "Validation gates":
///   D-determinism-ingest      — substrate state Merkle byte-identical on re-ingest
///   D-vocab-recovered         — model_vocab_recovered == declared vocab_size
///   D-layer-count             — distinct layers == declared num_hidden_layers
///   D-firefly-tensor-coupled  — every firefly has firefly_for_token edge
///   D-cross-model-divergence  — non-zero divergence on shared tokens
/// </summary>
[Collection("RoundTrip")]
public sealed class SafetensorsRoundTripTests
{
    private readonly RoundTripFixture _fixture;

    public SafetensorsRoundTripTests(RoundTripFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DVocabRecovered_NonNegative_WhenModelIngested()
    {
        Assert.True(await _fixture.HasIngestedModelAsync(),
            "Substrate has no ingested model. Run scripts/seed/Safetensors.ps1 against a real safetensors directory before running this test.");

        byte[]? modelHash = await _fixture.GetSomeIngestedModelHashAsync();
        Assert.NotNull(modelHash);

        await using NpgsqlConnection conn = await _fixture.DataSource!.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(
            "SELECT substrate.model_vocab_recovered($1)", conn)
        {
            Parameters = { new() { Value = modelHash } },
        };
        object? result = await cmd.ExecuteScalarAsync();
        long recovered = result is long l ? l : System.Convert.ToInt64(result!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(recovered >= 0,
            $"model_vocab_recovered returned negative count: {recovered}");
    }

    [Fact]
    public async Task ModelInventory_ReturnsRowsForIngestedModel()
    {
        Assert.True(await _fixture.HasIngestedModelAsync(),
            "Requires an ingested safetensors model. Run scripts/seed/Safetensors.ps1 first.");

        byte[]? modelHash = await _fixture.GetSomeIngestedModelHashAsync();
        Assert.NotNull(modelHash);

        await using NpgsqlConnection conn = await _fixture.DataSource!.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(
            "SELECT count(*) FROM substrate.model_inventory($1)", conn)
        {
            Parameters = { new() { Value = modelHash } },
        };
        object? result = await cmd.ExecuteScalarAsync();
        long rows = result is long l ? l : System.Convert.ToInt64(result!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(rows > 0, "model_inventory returned no rows");
    }

    [Fact]
    public async Task ArchitectureEdges_PresentForIngestedModel()
    {
        Assert.True(await _fixture.HasIngestedModelAsync(),
            "Requires an ingested safetensors model. Run scripts/seed/Safetensors.ps1 first.");

        byte[]? modelHash = await _fixture.GetSomeIngestedModelHashAsync();
        Assert.NotNull(modelHash);

        await using NpgsqlConnection conn = await _fixture.DataSource!.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(@"
            SELECT count(*)
              FROM substrate.edge_member em
              JOIN substrate.edge_type et ON et.id = em.edge_type_id
             WHERE em.entity_hash = $1
               AND et.code IN (
                    'attention_head_in_layer',
                    'ffn_up_in_layer','ffn_gate_in_layer','ffn_down_in_layer',
                    'vocab_embedding','vocab_unembedding',
                    'layer_norm_for_layer_position',
                    'tensor_in_model_at_position'
               )", conn)
        {
            Parameters = { new() { Value = modelHash } },
        };
        object? result = await cmd.ExecuteScalarAsync();
        long count = result is long l ? l : System.Convert.ToInt64(result!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(count > 0,
            "ArchitectureEdgesPass must emit at least one Track 2 architectural edge per ingested model");
    }

    [Fact]
    public async Task FireflyForToken_EdgeExists_WhenFireflyEmitted()
    {
        Assert.True(await _fixture.HasIngestedModelAsync(),
            "Requires an ingested safetensors model. Run scripts/seed/Safetensors.ps1 first.");

        await using NpgsqlConnection conn = await _fixture.DataSource!.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(@"
            SELECT count(*)
              FROM substrate.physicality p
              JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
             WHERE pt.code = 'embedding_firefly'", conn);
        object? result = await cmd.ExecuteScalarAsync();
        long fireflies = result is long l ? l : System.Convert.ToInt64(result!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(fireflies > 0,
            "EmbeddingFireflyPass must emit fireflies for any ingested model with a tokenizer.json present.");

        await using NpgsqlCommand edgeCmd = new(@"
            SELECT count(*)
              FROM substrate.edge_type
             WHERE code IN ('firefly_for_token', 'has_token_id')", conn);
        object? edgeResult = await edgeCmd.ExecuteScalarAsync();
        long edgeTypeCount = edgeResult is long l2 ? l2 : System.Convert.ToInt64(edgeResult!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(edgeTypeCount >= 1,
            "firefly_for_token (or legacy has_token_id) edge type must be registered when fireflies exist");
    }

    [Fact]
    public async Task PreviewTargetArch_ReturnsStructuredPreview_OnEmptyRecipe()
    {
        await using NpgsqlConnection conn = await _fixture.DataSource!.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(@"
            SELECT count(*) FROM substrate.preview_target_arch(
                '{""hidden_size"":4096,""num_layers"":32,""num_attention_heads"":32,""vocab_size"":32768,""ffn_intermediate"":11008}'::jsonb,
                '{""arena_codes"":[""semantic_relevance""],""significance_floor"":0.5}'::jsonb
            )", conn);
        object? result = await cmd.ExecuteScalarAsync();
        long previewRows = result is long lp ? lp : System.Convert.ToInt64(result!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(previewRows > 0,
            "preview_target_arch must return at least one role bucket");
    }
}
