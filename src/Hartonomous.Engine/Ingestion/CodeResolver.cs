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
    private Dictionary<string, double>? _provenanceMus;
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

    /// <summary>
    /// Returns all significance context codes (arena codes) currently in
    /// substrate.significance_context. Cached after first load. AP-1: no
    /// cherry-picking — returns every arena.
    /// </summary>
    public async Task<IReadOnlyList<string>> AllSignificanceContextCodesAsync(CancellationToken ct)
    {
        _significanceContexts ??= await _reader.LoadCodeMapAsync("substrate.significance_context", 16, ct);
        return [.. _significanceContexts.Keys];
    }

    /// <summary>
    /// Returns the <c>initial_mu</c> trust prior for a provenance code. Used
    /// for inline edge significance emission — each edge gets per-arena
    /// significance rows seeded at the provenance's trust prior rather than
    /// default Glicko-2 1500.
    /// </summary>
    public async Task<double> ProvenanceMuAsync(string provenanceCode, CancellationToken ct)
    {
        _provenanceMus ??= await _reader.LoadCodeDoubleMapAsync("substrate.provenance", "initial_mu", 16, ct);
        if (_provenanceMus.TryGetValue(provenanceCode, out double mu))
        {
            return mu;
        }
        // Hierarchical fallback matches the same logic as ProvenanceIdAsync.
        string current = provenanceCode;
        while (true)
        {
            int lastSlash = current.LastIndexOf('/');
            if (lastSlash <= 0)
            {
                break;
            }
            current = current[..lastSlash];
            if (_provenanceMus.TryGetValue(current, out mu))
            {
                return mu;
            }
        }
        return 1500.0; // Glicko-2 default if provenance code not found
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
