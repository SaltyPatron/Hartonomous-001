using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;

namespace Hartonomous.Engine.Ingestion;

internal sealed class CodeResolver
{
    private readonly IReferenceDataReader _reader;
    private Dictionary<string, int>? _entityTypes;
    private Dictionary<string, int>? _edgeTypes;
    private Dictionary<string, int>? _physicalityTypes;
    private Dictionary<string, int>? _significanceContexts;
    private Dictionary<string, int>? _provenances;
    private Dictionary<string, int>? _edgeRoles;

    public CodeResolver(IReferenceDataReader reader)
    {
        _reader = reader;
    }

    public async Task<int> EntityTypeIdAsync(string code, CancellationToken ct)
    {
        _entityTypes ??= await _reader.LoadCodeMapAsync("substrate.entity_type", 32, ct);
        if (_entityTypes.TryGetValue(code, out int id))
        {
            return id;
        }
        _entityTypes = await _reader.LoadCodeMapAsync("substrate.entity_type", 32, ct);
        return Resolve(_entityTypes, code, "entity_type");
    }

    public async Task<int> EdgeTypeIdAsync(string code, CancellationToken ct)
    {
        _edgeTypes ??= await _reader.LoadCodeMapAsync("substrate.edge_type", 64, ct);
        if (_edgeTypes.TryGetValue(code, out int id))
        {
            return id;
        }
        _edgeTypes = await _reader.LoadCodeMapAsync("substrate.edge_type", 64, ct);
        return Resolve(_edgeTypes, code, "edge_type");
    }

    public async Task<int> PhysicalityTypeIdAsync(string code, CancellationToken ct)
    {
        _physicalityTypes ??= await _reader.LoadCodeMapAsync("substrate.physicality_type", 16, ct);
        if (_physicalityTypes.TryGetValue(code, out int id))
        {
            return id;
        }
        _physicalityTypes = await _reader.LoadCodeMapAsync("substrate.physicality_type", 16, ct);
        return Resolve(_physicalityTypes, code, "physicality_type");
    }

    public async Task<int> SignificanceContextIdAsync(string code, CancellationToken ct)
    {
        _significanceContexts ??= await _reader.LoadCodeMapAsync("substrate.significance_context", 16, ct);
        if (_significanceContexts.TryGetValue(code, out int id))
        {
            return id;
        }
        _significanceContexts = await _reader.LoadCodeMapAsync("substrate.significance_context", 16, ct);
        return Resolve(_significanceContexts, code, "significance_context");
    }

    public async Task<int> ProvenanceIdAsync(string code, CancellationToken ct)
    {
        _provenances ??= await _reader.LoadCodeMapAsync("substrate.provenance", 64, ct);
        if (TryResolveHierarchical(_provenances, code, out int id))
        {
            return id;
        }
        _provenances = await _reader.LoadCodeMapAsync("substrate.provenance", 64, ct);
        return ResolveHierarchical(_provenances, code, "provenance");
    }

    public async Task<int> EdgeRoleIdAsync(string code, CancellationToken ct)
    {
        _edgeRoles ??= await _reader.LoadCodeMapAsync("substrate.edge_role", 16, ct);
        if (_edgeRoles.TryGetValue(code, out int id))
        {
            return id;
        }
        _edgeRoles = await _reader.LoadCodeMapAsync("substrate.edge_role", 16, ct);
        return Resolve(_edgeRoles, code, "edge_role");
    }

    private static int Resolve(Dictionary<string, int> map, string code, string typeName)
    {
        if (map.TryGetValue(code, out int id))
        {
            return id;
        }
        throw new InvalidOperationException($"Unknown {typeName} code: '{code}'");
    }

    /// <summary>
    /// Resolve a hierarchical code by walking up slash-delimited segments.
    /// E.g. "universaldependencies/v2.17/UD_Abaza-ATB" → "universaldependencies/v2.17" → "universaldependencies".
    /// </summary>
    private static int ResolveHierarchical(Dictionary<string, int> map, string code, string typeName)
    {
        if (TryResolveHierarchical(map, code, out int id))
        {
            return id;
        }
        throw new InvalidOperationException($"Unknown {typeName} code: '{code}'");
    }

    private static bool TryResolveHierarchical(Dictionary<string, int> map, string code, out int id)
    {
        string current = code;
        while (true)
        {
            if (map.TryGetValue(current, out id))
            {
                return true;
            }
            int lastSlash = current.LastIndexOf('/');
            if (lastSlash <= 0)
            {
                id = 0;
                return false;
            }
            current = current[..lastSlash];
        }
    }
}
