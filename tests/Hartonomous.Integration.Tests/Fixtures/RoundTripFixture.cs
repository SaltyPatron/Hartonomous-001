using System;
using System.IO;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Hartonomous.Integration.Tests.Fixtures;

/// <summary>
/// Provides a connection-managed substrate snapshot for V1 round-trip tests.
/// The fixture expects an already-bootstrapped, already-seeded substrate (the
/// user's pipeline runs scripts/db/Bootstrap + scripts/seed/All before tests
/// fire). Per the V1 plan's Phase 0 baseline.
///
/// Real ingested-model snapshots are loaded by the test from /vault/Data/... if
/// present; otherwise the test that depends on them is skipped via
/// <see cref="HasIngestedModel"/>.
/// </summary>
public sealed class RoundTripFixture : IAsyncLifetime
{
    public NpgsqlDataSource? DataSource { get; private set; }

    public string ConnectionString { get; }

    public string ModelsRoot { get; }

    public RoundTripFixture()
    {
        ConnectionString = Environment.GetEnvironmentVariable("HARTONOMOUS_DB")
            ?? "Host=localhost;Port=5433;Username=hartonomous;Password=hartonomous;Database=hartonomous";
        ModelsRoot = Environment.GetEnvironmentVariable("HARTONOMOUS_MODELS_ROOT")
            ?? "/vault/Data";
    }

    public Task InitializeAsync()
    {
        DataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }
    }

    /// <summary>
    /// Returns true when an ingested model is present in the substrate (any
    /// row in substrate.entity_model_source). Tests that require a real
    /// ingested model gate on this so they skip cleanly when the substrate
    /// has only seed corpora.
    /// </summary>
    public async Task<bool> HasIngestedModelAsync()
    {
        if (DataSource is null) { return false; }
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(
            "SELECT EXISTS (SELECT 1 FROM substrate.entity_model_source LIMIT 1)", conn);
        object? result = await cmd.ExecuteScalarAsync();
        return result is true;
    }

    /// <summary>
    /// Returns the model_architecture entity hash of the most-recently-
    /// ingested model. Returns null when no model is ingested.
    /// </summary>
    public async Task<byte[]?> GetSomeIngestedModelHashAsync()
    {
        if (DataSource is null) { return null; }
        await using NpgsqlConnection conn = await DataSource.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new(@"
            SELECT em_src.entity_hash
              FROM substrate.edge_member em_src
              JOIN substrate.edge_type et ON et.id = em_src.edge_type_id AND et.code = 'has_tensor'
              JOIN substrate.edge_role er ON er.id = em_src.edge_role_id  AND er.code = 'source'
             LIMIT 1", conn);
        object? result = await cmd.ExecuteScalarAsync();
        return result as byte[];
    }
}

[CollectionDefinition("RoundTrip")]
public sealed class RoundTripFixtureBinding : ICollectionFixture<RoundTripFixture>
{
}
