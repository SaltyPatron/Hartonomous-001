using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;

namespace Hartonomous.Engine.Ingestion;

internal sealed class CodeResolver : IDisposable
{
    private readonly IReferenceDataReader _reader;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private Dictionary<string, int>? _entityTypes;
    private Dictionary<string, int>? _edgeTypes;
    private Dictionary<string, int>? _physicalityTypes;
    private Dictionary<string, int>? _significanceContexts;
    private Dictionary<string, int>? _attestationTypes;
    private Dictionary<string, int>? _provenances;
    private Dictionary<string, double>? _provenanceMus;
    private Dictionary<string, int>? _edgeRoles;

    public CodeResolver(IReferenceDataReader reader)
    {
        _reader = reader;
    }

    public void Dispose()
    {
        _cacheGate.Dispose();
    }

    public async Task<int> EntityTypeIdAsync(string code, CancellationToken ct)
    {
        Dictionary<string, int> entityTypes = await EntityTypesAsync(ct).ConfigureAwait(false);
        if (entityTypes.TryGetValue(code, out int id))
        {
            return id;
        }
        entityTypes = await ReloadEntityTypesAsync(ct).ConfigureAwait(false);
        return Resolve(entityTypes, code, "entity_type");
    }

    public async Task<int> EdgeTypeIdAsync(string code, CancellationToken ct)
    {
        Dictionary<string, int> edgeTypes = await EdgeTypesAsync(ct).ConfigureAwait(false);
        if (edgeTypes.TryGetValue(code, out int id))
        {
            return id;
        }
        edgeTypes = await ReloadEdgeTypesAsync(ct).ConfigureAwait(false);
        return Resolve(edgeTypes, code, "edge_type");
    }

    public async Task<int> PhysicalityTypeIdAsync(string code, CancellationToken ct)
    {
        Dictionary<string, int> physicalityTypes = await PhysicalityTypesAsync(ct).ConfigureAwait(false);
        if (physicalityTypes.TryGetValue(code, out int id))
        {
            return id;
        }
        physicalityTypes = await ReloadPhysicalityTypesAsync(ct).ConfigureAwait(false);
        return Resolve(physicalityTypes, code, "physicality_type");
    }

    public async Task<int> SignificanceContextIdAsync(string code, CancellationToken ct)
    {
        Dictionary<string, int> significanceContexts = await SignificanceContextsAsync(ct).ConfigureAwait(false);
        if (significanceContexts.TryGetValue(code, out int id))
        {
            return id;
        }
        significanceContexts = await ReloadSignificanceContextsAsync(ct).ConfigureAwait(false);
        return Resolve(significanceContexts, code, "significance_context");
    }

    public async Task<int> AttestationTypeIdAsync(string code, CancellationToken ct)
    {
        Dictionary<string, int> attestationTypes = await AttestationTypesAsync(ct).ConfigureAwait(false);
        if (attestationTypes.TryGetValue(code, out int id))
        {
            return id;
        }
        attestationTypes = await ReloadAttestationTypesAsync(ct).ConfigureAwait(false);
        return Resolve(attestationTypes, code, "attestation_type");
    }

    public async Task<int> ProvenanceIdAsync(string code, CancellationToken ct)
    {
        Dictionary<string, int> provenances = await ProvenancesAsync(ct).ConfigureAwait(false);
        if (TryResolveHierarchical(provenances, code, out int id))
        {
            return id;
        }
        provenances = await ReloadProvenancesAsync(ct).ConfigureAwait(false);
        return ResolveHierarchical(provenances, code, "provenance");
    }

    public async Task<int> EdgeRoleIdAsync(string code, CancellationToken ct)
    {
        Dictionary<string, int> edgeRoles = await EdgeRolesAsync(ct).ConfigureAwait(false);
        if (edgeRoles.TryGetValue(code, out int id))
        {
            return id;
        }
        edgeRoles = await ReloadEdgeRolesAsync(ct).ConfigureAwait(false);
        return Resolve(edgeRoles, code, "edge_role");
    }

    /// <summary>
    /// Returns all significance context codes (arena codes) currently in
    /// substrate.significance_context. Cached after first load. AP-1: no
    /// cherry-picking — returns every arena.
    /// </summary>
    public async Task<IReadOnlyList<string>> AllSignificanceContextCodesAsync(CancellationToken ct)
    {
        Dictionary<string, int> significanceContexts = await SignificanceContextsAsync(ct).ConfigureAwait(false);
        return [.. significanceContexts.Keys];
    }

    /// <summary>
    /// Returns the <c>initial_mu</c> trust prior for a provenance code. Used
    /// for inline edge significance emission — each edge gets per-arena
    /// significance rows seeded at the provenance's trust prior rather than
    /// default Glicko-2 1500.
    /// </summary>
    public async Task<double> ProvenanceMuAsync(string provenanceCode, CancellationToken ct)
    {
        Dictionary<string, double> provenanceMus = await ProvenanceMusAsync(ct).ConfigureAwait(false);
        if (provenanceMus.TryGetValue(provenanceCode, out double mu))
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
            if (provenanceMus.TryGetValue(current, out mu))
            {
                return mu;
            }
        }
        return 1500.0; // Glicko-2 default if provenance code not found
    }

    private async Task<Dictionary<string, int>> EntityTypesAsync(CancellationToken ct)
    {
        Dictionary<string, int>? map = Volatile.Read(ref _entityTypes);
        if (map is not null) { return map; }
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _entityTypes ??= await _reader.LoadCodeMapAsync("substrate.entity_type", 32, ct).ConfigureAwait(false);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<Dictionary<string, int>> EdgeTypesAsync(CancellationToken ct)
    {
        Dictionary<string, int>? map = Volatile.Read(ref _edgeTypes);
        if (map is not null) { return map; }
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _edgeTypes ??= await _reader.LoadCodeMapAsync("substrate.edge_type", 64, ct).ConfigureAwait(false);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<Dictionary<string, int>> PhysicalityTypesAsync(CancellationToken ct)
    {
        Dictionary<string, int>? map = Volatile.Read(ref _physicalityTypes);
        if (map is not null) { return map; }
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _physicalityTypes ??= await _reader.LoadCodeMapAsync("substrate.physicality_type", 16, ct).ConfigureAwait(false);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<Dictionary<string, int>> SignificanceContextsAsync(CancellationToken ct)
    {
        Dictionary<string, int>? map = Volatile.Read(ref _significanceContexts);
        if (map is not null) { return map; }
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _significanceContexts ??= await _reader.LoadCodeMapAsync("substrate.significance_context", 16, ct).ConfigureAwait(false);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<Dictionary<string, int>> AttestationTypesAsync(CancellationToken ct)
    {
        Dictionary<string, int>? map = Volatile.Read(ref _attestationTypes);
        if (map is not null) { return map; }
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _attestationTypes ??= await _reader.LoadCodeMapAsync("substrate.attestation_type", 16, ct).ConfigureAwait(false);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<Dictionary<string, int>> ProvenancesAsync(CancellationToken ct)
    {
        Dictionary<string, int>? map = Volatile.Read(ref _provenances);
        if (map is not null) { return map; }
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _provenances ??= await _reader.LoadCodeMapAsync("substrate.provenance", 64, ct).ConfigureAwait(false);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<Dictionary<string, int>> EdgeRolesAsync(CancellationToken ct)
    {
        Dictionary<string, int>? map = Volatile.Read(ref _edgeRoles);
        if (map is not null) { return map; }
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _edgeRoles ??= await _reader.LoadCodeMapAsync("substrate.edge_role", 16, ct).ConfigureAwait(false);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<Dictionary<string, double>> ProvenanceMusAsync(CancellationToken ct)
    {
        Dictionary<string, double>? map = Volatile.Read(ref _provenanceMus);
        if (map is not null) { return map; }
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _provenanceMus ??= await _reader.LoadCodeDoubleMapAsync("substrate.provenance", "initial_mu", 16, ct).ConfigureAwait(false);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<Dictionary<string, int>> ReloadEntityTypesAsync(CancellationToken ct)
    {
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _entityTypes = await _reader.LoadCodeMapAsync("substrate.entity_type", 32, ct).ConfigureAwait(false);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<Dictionary<string, int>> ReloadEdgeTypesAsync(CancellationToken ct)
    {
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _edgeTypes = await _reader.LoadCodeMapAsync("substrate.edge_type", 64, ct).ConfigureAwait(false);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<Dictionary<string, int>> ReloadPhysicalityTypesAsync(CancellationToken ct)
    {
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _physicalityTypes = await _reader.LoadCodeMapAsync("substrate.physicality_type", 16, ct).ConfigureAwait(false);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<Dictionary<string, int>> ReloadSignificanceContextsAsync(CancellationToken ct)
    {
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _significanceContexts = await _reader.LoadCodeMapAsync("substrate.significance_context", 16, ct).ConfigureAwait(false);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<Dictionary<string, int>> ReloadAttestationTypesAsync(CancellationToken ct)
    {
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _attestationTypes = await _reader.LoadCodeMapAsync("substrate.attestation_type", 16, ct).ConfigureAwait(false);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<Dictionary<string, int>> ReloadProvenancesAsync(CancellationToken ct)
    {
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _provenances = await _reader.LoadCodeMapAsync("substrate.provenance", 64, ct).ConfigureAwait(false);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task<Dictionary<string, int>> ReloadEdgeRolesAsync(CancellationToken ct)
    {
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _edgeRoles = await _reader.LoadCodeMapAsync("substrate.edge_role", 16, ct).ConfigureAwait(false);
        }
        finally
        {
            _cacheGate.Release();
        }
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
