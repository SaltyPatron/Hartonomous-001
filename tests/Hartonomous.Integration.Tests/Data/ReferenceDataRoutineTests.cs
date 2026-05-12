using Hartonomous.Engine.Data;
using Npgsql;

namespace Hartonomous.Integration.Tests.Data;

public sealed class ReferenceDataRoutineTests : IAsyncLifetime
{
    private static string ConnectionString() =>
        Environment.GetEnvironmentVariable("HARTONOMOUS_DB")
        ?? "Host=localhost;Port=5433;Username=hartonomous;Password=hartonomous;Database=hartonomous";

    private NpgsqlDataSource _dataSource = null!;
    private NpgsqlReferenceDataReader _reader = null!;
    private NpgsqlReferenceDataWriter _writer = null!;

    public Task InitializeAsync()
    {
        _dataSource = NpgsqlDataSource.Create(ConnectionString());
        _reader = new NpgsqlReferenceDataReader(_dataSource);
        _writer = new NpgsqlReferenceDataWriter(_dataSource);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task LoadCodeMapAsync_EntityTypes_ReturnsKnownRows()
    {
        Dictionary<string, int> map = await _reader.LoadCodeMapAsync(
            "substrate.entity_type",
            initialCapacity: 32,
            CancellationToken.None);

        Assert.True(map.Count >= 21, $"Expected at least 21 entity types, got {map.Count}");
        Assert.Equal(1, map["codepoint"]);
        Assert.Contains("lemma", map.Keys);
        Assert.Contains("word_form", map.Keys);
    }

    [Fact]
    public async Task PopulateMorphFeaturesAsync_ThenLoadKeyValueMapAsync_RoundTripsRows()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string key1 = $"tk_{suffix}";
        string value1 = $"tv_{suffix}";
        string key2 = $"tk2_{suffix}";
        string value2 = $"tv2_{suffix}";

        try
        {
            await _writer.PopulateMorphFeaturesAsync(
                [(key1, value1), (key2, value2)],
                CancellationToken.None);

            Dictionary<(string Key, string Value), int> map = await _reader.LoadKeyValueMapAsync(
                "substrate.morph_feature",
                "key",
                "value",
                initialCapacity: 8,
                CancellationToken.None);

            Assert.Contains((key1, value1), map.Keys);
            Assert.Contains((key2, value2), map.Keys);
            Assert.True(map[(key1, value1)] > 0);
            Assert.True(map[(key2, value2)] > 0);
        }
        finally
        {
            await DeleteMorphFeaturesAsync([key1, key2], CancellationToken.None);
        }
    }

    [Fact]
    public async Task PopulateDeprelsAsync_SubtypeChild_ResolvesParentId()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string parentCode = $"zp_{suffix}";
        string childCode = $"{parentCode}:child";

        try
        {
            await _writer.PopulateDeprelsAsync([parentCode, childCode], CancellationToken.None);

            await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(CancellationToken.None);
            await using NpgsqlCommand cmd = new(
                "SELECT child.parent_id, parent.id " +
                "FROM substrate.deprel child " +
                "JOIN substrate.deprel parent ON parent.code = $1 " +
                "WHERE child.code = $2", conn);
            cmd.Parameters.AddWithValue(parentCode);
            cmd.Parameters.AddWithValue(childCode);

            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(reader.GetInt32(1), reader.GetInt32(0));
        }
        finally
        {
            await DeleteDeprelsAsync([childCode, parentCode], CancellationToken.None);
        }
    }

    [Fact]
    public async Task UpsertEdgeTypeAsync_CreatesRow_LoadIdByCodeAsync_FindsIt()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string edgeCode = $"te_{suffix}";

        try
        {
            await _writer.UpsertEdgeTypeAsync(
                edgeCode,
                "structural",
                "lemma",
                "lemma",
                CancellationToken.None);

            int edgeTypeId = await _reader.LoadIdByCodeAsync(
                "substrate.edge_type",
                edgeCode,
                CancellationToken.None);

            await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(CancellationToken.None);
            await using NpgsqlCommand cmd = new(
                "SELECT et.id, src.code, tgt.code " +
                "FROM substrate.edge_type et " +
                "JOIN substrate.entity_type src ON src.id = et.source_type_id " +
                "JOIN substrate.entity_type tgt ON tgt.id = et.target_type_id " +
                "WHERE et.code = $1", conn);
            cmd.Parameters.AddWithValue(edgeCode);

            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(edgeTypeId, reader.GetInt32(0));
            Assert.Equal("lemma", reader.GetString(1).Trim());
            Assert.Equal("lemma", reader.GetString(2).Trim());
        }
        finally
        {
            await DeleteEdgeTypeAsync(edgeCode, CancellationToken.None);
        }
    }

    private async Task DeleteMorphFeaturesAsync(string[] keys, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "DELETE FROM substrate.morph_feature WHERE \"key\" = ANY($1)", conn);
        cmd.Parameters.AddWithValue(keys);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task DeleteDeprelsAsync(string[] codes, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "DELETE FROM substrate.deprel WHERE code = ANY($1)", conn);
        cmd.Parameters.AddWithValue(codes);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task DeleteEdgeTypeAsync(string edgeCode, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "DELETE FROM substrate.edge_type WHERE code = $1", conn);
        cmd.Parameters.AddWithValue(edgeCode);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
