namespace Hartonomous.Smoke.Tests;

/// <summary>
/// Smoke tests for the safetensors / model-decomposition phase (M7). These
/// run independent of any actual model on disk: they validate the substrate
/// contracts the 36-pass orchestrator depends on. If any of these break,
/// every safetensors decomposition fails identically — surfacing the bug
/// here in seconds instead of after a 5.8GB Qwen-3B parse.
/// </summary>
[Collection("smoke")]
public sealed class SafetensorsSmokeTests
{
    private readonly SmokeFixture _fx;

    public SafetensorsSmokeTests(SmokeFixture fx) => _fx = fx;

    [Fact]
    public async Task ModelArchitectureEntityType_Seeded()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM substrate.entity_type WHERE code = 'model_architecture'");
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task TensorEntityType_Seeded()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM substrate.entity_type WHERE code = 'tensor'");
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task HasTensorEdgeType_Seeded()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM substrate.edge_type WHERE code = 'has_tensor'");
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task TensorRoleReferenceTable_Exists()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // tensor_role is populated lazily at first safetensors run via
        // SafetensorsReferenceTableWriter.EnsureTensorRoleAsync. Pre-seed
        // population is not required; what IS required is that the table
        // exists with (id, code) at minimum so EnsureTensorRoleAsync's
        // INSERT succeeds.
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_schema = 'substrate' AND table_name = 'tensor_role' " +
            "AND column_name IN ('id','code')");
        Assert.Equal(2, n);
    }

    [Fact]
    public async Task ArchitectureClassReferenceTable_Exists()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // architecture_class populates lazily via EnsureArchitectureClassAsync.
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_schema = 'substrate' AND table_name = 'architecture_class' " +
            "AND column_name IN ('id','code')");
        Assert.Equal(2, n);
    }

    [Fact]
    public async Task ModelSourceTable_Exists()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // The orchestrator pins per-model identity into model_source via
        // EnsureModelSourceAsync. If the table is missing or its CHECK
        // constraint drifts (revision must be 20 or 32 bytes), every
        // safetensors run fails at bootstrap.
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM information_schema.tables " +
            "WHERE table_schema = 'substrate' AND table_name = 'model_source'");
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task ModelPassCheckpoint_TableExists()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // Checkpoint/resume across the 36 passes lives here. Missing table
        // means every pass restarts from scratch on every run — no resume.
        long n = await _fx.ExecScalarLongAsync(
            "SELECT count(*) FROM information_schema.tables " +
            "WHERE table_schema = 'substrate' AND table_name = 'model_pass_checkpoint'");
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task PerRoleAttestationTypes_AllSeeded()
    {
        Skip.IfNot(_fx.DbReachable, "Hartonomous DB not reachable");
        // The 18 per-role attestation_type codes are referenced by the model
        // analysis passes. If any are missing, the corresponding pass crashes
        // with 'attestation_type code=... missing — bootstrap not applied?'.
        string[] required =
        [
            "model_attention_query_projection", "model_attention_key_projection",
            "model_attention_value_projection", "model_attention_output_projection",
            "model_attention_qk_pattern", "model_attention_vo_pattern",
            "model_ffn_up_projection", "model_ffn_gate_projection",
            "model_ffn_down_projection", "model_ffn_full_path",
            "model_lm_head_projection", "model_input_embedding",
            "model_layer_norm_evidence", "model_moe_router",
            "model_moe_expert_response", "model_lora_adapter_evidence",
            "model_position_embedding", "model_quantization_variant_evidence",
        ];
        foreach (string code in required)
        {
            await using Npgsql.NpgsqlConnection conn = new(_fx.ConnectionString);
            await conn.OpenAsync();
            await using Npgsql.NpgsqlCommand cmd = new(
                "SELECT count(*) FROM substrate.attestation_type WHERE code = @code", conn);
            cmd.Parameters.AddWithValue("code", code);
            object? r = await cmd.ExecuteScalarAsync();
            long n = Convert.ToInt64(r, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(n == 1, $"per-role attestation_type '{code}' missing");
        }
    }
}
