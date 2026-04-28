using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Hartonomous.Core.Engine;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Recomposition;
using Hartonomous.Recomposers;

namespace Hartonomous.Integration.Tests.VerticalSlice;

/// <summary>
/// Verifies the SafetensorsRecomposer produces a structurally valid
/// safetensors binary stream end-to-end without requiring a live database.
/// Hash-as-PK throughout — exercises the EntityHandle-based IEntityReader.
/// </summary>
public sealed class SafetensorsRecomposerRoundTripTests
{
    [Fact]
    public async Task EmptySubstrate_StillProducesValidSafetensorsContainer()
    {
        FakeEntityReader reader = new();
        SafetensorsRecomposer recomposer = new(reader, textReader: null);

        byte[] hash = new byte[32];
        BitConverter.GetBytes(1L).CopyTo(hash, 0);
        EntityHandle archHandle = new(hash, "model_architecture");

        SafetensorsFile file = await recomposer.RecomposeAsync(
            entity: archHandle,
            options: new RecompositionOptions(),
            ct: CancellationToken.None);

        Assert.NotNull(file);
        Assert.NotNull(file.Tensors);
        Assert.Empty(file.Tensors);
        Assert.StartsWith("model_", file.ModelName);
    }

    [Fact]
    public async Task SafetensorsWriter_WritesValidBinaryFormat()
    {
        Dictionary<string, TensorData> tensors = new(StringComparer.Ordinal)
        {
            ["a.weight"] = new TensorData("F32", [2, 2], new byte[2 * 2 * 4]),
            ["b.weight"] = new TensorData("BF16", [4], new byte[4 * 2]),
        };
        SafetensorsFile file = new(tensors, "test_model");

        using MemoryStream ms = new();
        await SafetensorsWriter.WriteAsync(file, ms, CancellationToken.None);
        byte[] bytes = ms.ToArray();

        Assert.True(bytes.Length >= 8);
        ulong headerLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0, 8));
        Assert.True(headerLen > 0);
        Assert.Equal(0UL, headerLen % 8UL);

        string header = System.Text.Encoding.UTF8.GetString(bytes, 8, (int)headerLen).TrimEnd();
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(header);
        Assert.True(doc.RootElement.TryGetProperty("__metadata__", out _));
        Assert.True(doc.RootElement.TryGetProperty("a.weight", out _));
        Assert.True(doc.RootElement.TryGetProperty("b.weight", out _));

        long expected = 8 + (long)headerLen + (2 * 2 * 4) + (4 * 2);
        Assert.Equal(expected, bytes.Length);
    }

    private sealed class FakeEntityReader : IEntityReader
    {
        public Task<IReadOnlyList<EntityHandle>> ResolveEntityHandlesAsync(
            IReadOnlyList<byte[]> hashes, IReadOnlyList<string> entityTypeCodes, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<EntityHandle>>([]);

        public Task<IReadOnlyDictionary<EntityHandle, EntityInfo>> GetEntityInfoAsync(
            IReadOnlyList<EntityHandle> entityHandles, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<EntityHandle, EntityInfo>>(
                new Dictionary<EntityHandle, EntityInfo>());

        public Task<IReadOnlyList<(EntityHandle Child, int Position)>> GetCompositionChildrenAsync(
            EntityHandle parent, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<(EntityHandle, int)>>([]);

        public Task<IReadOnlyDictionary<EdgeHandle, EdgeInfo>> GetEdgeInfoAsync(
            IReadOnlyList<EdgeHandle> edgeHandles, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<EdgeHandle, EdgeInfo>>(
                new Dictionary<EdgeHandle, EdgeInfo>());

        public Task<IReadOnlyList<EntityHandle>> FindEntitiesByContentAsync(
            string content, IReadOnlyList<string> entityTypeCodes, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<EntityHandle>>([]);

        public Task<IReadOnlyList<EntityHandle>> GetOutboundEdgeTargetsAsync(
            EntityHandle source, string edgeTypeCode, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<EntityHandle>>([]);
    }
}
