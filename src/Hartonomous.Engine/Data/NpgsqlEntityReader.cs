using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Data;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Engine;
using Hartonomous.Core.Ingestion;
using Npgsql;

namespace Hartonomous.Engine.Data;

/// <summary>
/// Hash-as-PK implementation of <see cref="IEntityReader"/> and
/// <see cref="ITextRecompositionReader"/>. Every method addresses entities
/// and edges by composite (type_code, hash) handles. All SQL goes through
/// named substrate functions (AP-2): substrate.resolve_entity_handles,
/// get_entity_info_by_handles, get_edge_info_by_handles,
/// get_outbound_edge_targets, get_composition_children, recompose_text.
/// </summary>
public sealed class NpgsqlEntityReader : IEntityReader, ITextRecompositionReader
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlEntityReader(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<EntityHandle>> ResolveEntityHandlesAsync(
        IReadOnlyList<byte[]> hashes,
        IReadOnlyList<string> entityTypeCodes,
        CancellationToken ct)
    {
        if (hashes.Count == 0 || entityTypeCodes.Count == 0)
        {
            return [];
        }

        byte[][] hashArray = new byte[hashes.Count][];
        for (int i = 0; i < hashes.Count; i++)
        {
            hashArray[i] = hashes[i];
        }
        string[] typeArray = new string[entityTypeCodes.Count];
        for (int i = 0; i < entityTypeCodes.Count; i++)
        {
            typeArray[i] = entityTypeCodes[i];
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = new(
            "SELECT entity_type_code, entity_hash FROM substrate.resolve_entity_handles($1, $2)", conn);
        cmd.Parameters.AddWithValue(hashArray);
        cmd.Parameters.AddWithValue(typeArray);

        List<EntityHandle> result = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string typeCode = reader.GetString(0).Trim();
            byte[] hash = (byte[])reader.GetValue(1);
            result.Add(new EntityHandle(hash, typeCode));
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<EntityHandle, EntityInfo>> GetEntityInfoAsync(
        IReadOnlyList<EntityHandle> entityHandles, CancellationToken ct)
    {
        if (entityHandles.Count == 0)
        {
            return new Dictionary<EntityHandle, EntityInfo>();
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        Dictionary<string, int> typeMap = await LoadTypeMapAsync(conn, ct);

        int[] typeIds = new int[entityHandles.Count];
        byte[][] hashes = new byte[entityHandles.Count][];
        for (int i = 0; i < entityHandles.Count; i++)
        {
            if (!typeMap.TryGetValue(entityHandles[i].EntityTypeCode, out int typeId))
            {
                throw new InvalidOperationException(
                    $"Unknown entity type code: '{entityHandles[i].EntityTypeCode}'");
            }
            typeIds[i] = typeId;
            hashes[i] = entityHandles[i].Hash;
        }

        Dictionary<EntityHandle, EntityInfo> result = new(entityHandles.Count);
        await using NpgsqlCommand cmd = new(
            "SELECT entity_type_code, entity_hash FROM substrate.get_entity_info_by_handles($1, $2)", conn);
        cmd.Parameters.AddWithValue(typeIds);
        cmd.Parameters.AddWithValue(hashes);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string typeCode = reader.GetString(0).Trim();
            byte[] hash = (byte[])reader.GetValue(1);
            EntityHandle handle = new(hash, typeCode);
            result[handle] = new EntityInfo
            {
                Handle = handle,
                ContentLabel = null,
            };
        }
        return result;
    }

    public async Task<IReadOnlyList<(EntityHandle Child, int Position)>> GetCompositionChildrenAsync(
        EntityHandle parent, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        Dictionary<string, int> typeMap = await LoadTypeMapAsync(conn, ct);
        if (!typeMap.TryGetValue(parent.EntityTypeCode, out int parentTypeId))
        {
            return [];
        }

        // composition_range expands RLE-compressed sequence rows back into
        // one ordinal per row, and walks the partitioned PK on
        // (parent_type_id, parent_hash, ordinal) — microsecond range scan
        // even on Moby-Dick-sized parents. INT.MaxValue as the upper bound
        // says "every child, however many there are."
        await using NpgsqlCommand cmd = new(
            "SELECT child_type_code, child_hash, ordinal " +
            "FROM substrate.composition_range($1, $2, 1, $3) ORDER BY ordinal", conn);
        cmd.Parameters.AddWithValue(parentTypeId);
        cmd.Parameters.AddWithValue(parent.Hash);
        cmd.Parameters.AddWithValue(int.MaxValue);

        List<(EntityHandle Child, int Position)> children = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string typeCode = reader.GetString(0).Trim();
            byte[] hash = (byte[])reader.GetValue(1);
            int position = reader.GetInt32(2);
            children.Add((new EntityHandle(hash, typeCode), position));
        }
        return children;
    }

    public async Task<IReadOnlyDictionary<EdgeHandle, EdgeInfo>> GetEdgeInfoAsync(
        IReadOnlyList<EdgeHandle> edgeHandles, CancellationToken ct)
    {
        if (edgeHandles.Count == 0)
        {
            return new Dictionary<EdgeHandle, EdgeInfo>();
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        Dictionary<string, int> edgeTypeMap = await LoadEdgeTypeMapAsync(conn, ct);

        int[] typeIds = new int[edgeHandles.Count];
        byte[][] hashes = new byte[edgeHandles.Count][];
        for (int i = 0; i < edgeHandles.Count; i++)
        {
            if (!edgeTypeMap.TryGetValue(edgeHandles[i].EdgeTypeCode, out int typeId))
            {
                throw new InvalidOperationException(
                    $"Unknown edge type code: '{edgeHandles[i].EdgeTypeCode}'");
            }
            typeIds[i] = typeId;
            hashes[i] = edgeHandles[i].Hash;
        }

        Dictionary<EdgeHandle, EdgeInfo> result = new(edgeHandles.Count);
        await using NpgsqlCommand cmd = new(
            "SELECT edge_type_code, edge_hash, " +
            "       source_type_code, source_hash, target_type_code, target_hash " +
            "FROM substrate.get_edge_info_by_handles($1, $2)", conn);
        cmd.Parameters.AddWithValue(typeIds);
        cmd.Parameters.AddWithValue(hashes);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string edgeTypeCode = reader.GetString(0).Trim();
            byte[] edgeHash = (byte[])reader.GetValue(1);
            EdgeHandle handle = new(edgeHash, edgeTypeCode);

            EntityHandle? source = null;
            if (!reader.IsDBNull(2))
            {
                source = new EntityHandle(
                    (byte[])reader.GetValue(3), reader.GetString(2).Trim());
            }
            EntityHandle? target = null;
            if (!reader.IsDBNull(4))
            {
                target = new EntityHandle(
                    (byte[])reader.GetValue(5), reader.GetString(4).Trim());
            }

            result[handle] = new EdgeInfo
            {
                Handle = handle,
                Source = source,
                Target = target,
            };
        }
        return result;
    }

    public async Task<IReadOnlyList<EntityHandle>> FindEntitiesByContentAsync(
        string content, IReadOnlyList<string> entityTypeCodes, CancellationToken ct)
    {
        IReadOnlyList<byte[]> candidateHashes = EntityContentHashResolver.GetCandidateHashes(
            content, entityTypeCodes);
        if (candidateHashes.Count == 0)
        {
            return [];
        }
        return await ResolveEntityHandlesAsync(candidateHashes, entityTypeCodes, ct);
    }

    public async Task<IReadOnlyList<EntityHandle>> GetOutboundEdgeTargetsAsync(
        EntityHandle source, string edgeTypeCode, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        Dictionary<string, int> typeMap = await LoadTypeMapAsync(conn, ct);
        if (!typeMap.TryGetValue(source.EntityTypeCode, out int sourceTypeId))
        {
            return [];
        }

        await using NpgsqlCommand cmd = new(
            "SELECT target_type_code, target_hash " +
            "FROM substrate.get_outbound_edge_targets($1, $2, $3)", conn);
        cmd.Parameters.AddWithValue(sourceTypeId);
        cmd.Parameters.AddWithValue(source.Hash);
        cmd.Parameters.AddWithValue(edgeTypeCode);

        List<EntityHandle> targets = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string typeCode = reader.GetString(0).Trim();
            byte[] hash = (byte[])reader.GetValue(1);
            targets.Add(new EntityHandle(hash, typeCode));
        }
        return targets;
    }

    public async Task<string?> RecomposeTextAsync(EntityHandle root, int maxDepth, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        Dictionary<string, int> typeMap = await LoadTypeMapAsync(conn, ct);
        if (!typeMap.TryGetValue(root.EntityTypeCode, out int typeId))
        {
            return null;
        }

        await using NpgsqlCommand cmd = new(
            "SELECT substrate.recompose_text($1, $2, $3)", conn);
        cmd.Parameters.AddWithValue(typeId);
        cmd.Parameters.AddWithValue(root.Hash);
        cmd.Parameters.AddWithValue(maxDepth);

        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull or null ? null : (string)result;
    }

    private static Dictionary<string, int> _entityTypeMapCache = new(StringComparer.Ordinal);
    private static Dictionary<string, int> _edgeTypeMapCache = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim _typeMapLock = new(1, 1);

    private static async Task<Dictionary<string, int>> LoadTypeMapAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (_entityTypeMapCache.Count > 0)
        {
            return _entityTypeMapCache;
        }
        await _typeMapLock.WaitAsync(ct);
        try
        {
            if (_entityTypeMapCache.Count > 0)
            {
                return _entityTypeMapCache;
            }
            Dictionary<string, int> map = new(StringComparer.Ordinal);
            await using NpgsqlCommand cmd = new(
                "SELECT id, code FROM substrate.entity_type", conn);
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                map[reader.GetString(1).Trim()] = reader.GetInt32(0);
            }
            _entityTypeMapCache = map;
            return map;
        }
        finally
        {
            _typeMapLock.Release();
        }
    }

    private static async Task<Dictionary<string, int>> LoadEdgeTypeMapAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (_edgeTypeMapCache.Count > 0)
        {
            return _edgeTypeMapCache;
        }
        await _typeMapLock.WaitAsync(ct);
        try
        {
            if (_edgeTypeMapCache.Count > 0)
            {
                return _edgeTypeMapCache;
            }
            Dictionary<string, int> map = new(StringComparer.Ordinal);
            await using NpgsqlCommand cmd = new(
                "SELECT id, code FROM substrate.edge_type", conn);
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                map[reader.GetString(1).Trim()] = reader.GetInt32(0);
            }
            _edgeTypeMapCache = map;
            return map;
        }
        finally
        {
            _typeMapLock.Release();
        }
    }

    /// <summary>Byte array equality comparer for hash dictionaries.</summary>
    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }
            if (x is null || y is null || x.Length != y.Length)
            {
                return false;
            }
            return x.AsSpan().SequenceEqual(y);
        }

        public int GetHashCode(byte[] obj)
        {
            if (obj.Length >= 4)
            {
                return BitConverter.ToInt32(obj, 0);
            }
            return obj.Length;
        }
    }
}
