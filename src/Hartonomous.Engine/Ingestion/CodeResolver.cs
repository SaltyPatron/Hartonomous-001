using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Hartonomous.Engine.Ingestion;

internal sealed class CodeResolver
{
    private readonly NpgsqlDataSource _dataSource;
    private Dictionary<string, int>? _entityTypes;
    private Dictionary<string, int>? _edgeTypes;
    private Dictionary<string, int>? _physicalityTypes;
    private Dictionary<string, int>? _significanceContexts;
    private Dictionary<string, int>? _provenances;
    private Dictionary<string, int>? _edgeRoles;

    public CodeResolver(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<int> EntityTypeIdAsync(string code, CancellationToken ct)
    {
        _entityTypes ??= await LoadAsync("substrate.entity_type", ct);
        return Resolve(_entityTypes, code, "entity_type");
    }

    public async Task<int> EdgeTypeIdAsync(string code, CancellationToken ct)
    {
        _edgeTypes ??= await LoadAsync("substrate.edge_type", ct);
        return Resolve(_edgeTypes, code, "edge_type");
    }

    public async Task<int> PhysicalityTypeIdAsync(string code, CancellationToken ct)
    {
        _physicalityTypes ??= await LoadAsync("substrate.physicality_type", ct);
        return Resolve(_physicalityTypes, code, "physicality_type");
    }

    public async Task<int> SignificanceContextIdAsync(string code, CancellationToken ct)
    {
        _significanceContexts ??= await LoadAsync("substrate.significance_context", ct);
        return Resolve(_significanceContexts, code, "significance_context");
    }

    public async Task<int> ProvenanceIdAsync(string code, CancellationToken ct)
    {
        _provenances ??= await LoadAsync("substrate.provenance", ct);
        return Resolve(_provenances, code, "provenance");
    }

    public async Task<int> EdgeRoleIdAsync(string code, CancellationToken ct)
    {
        _edgeRoles ??= await LoadAsync("substrate.edge_role", ct);
        return Resolve(_edgeRoles, code, "edge_role");
    }

    private async Task<Dictionary<string, int>> LoadAsync(string table, CancellationToken ct)
    {
        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new($"SELECT id, code FROM {table}", conn);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            map[reader.GetString(1)] = reader.GetInt32(0);
        }
        return map;
    }

    private static int Resolve(Dictionary<string, int> map, string code, string typeName)
    {
        if (map.TryGetValue(code, out int id))
        {
            return id;
        }
        throw new InvalidOperationException($"Unknown {typeName} code: '{code}'");
    }
}
