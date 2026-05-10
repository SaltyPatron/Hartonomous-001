using System;
using System.Buffers.Binary;
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

    [Fact]
    public async Task RecomposeAsync_ScattersSequenceRowContoursIntoTensorBytes()
    {
        EntityHandle architecture = Handle(1, "model_architecture");
        EntityHandle tensor = Handle(2, "tensor");
        EntityHandle nameDoc = Handle(3, "text_composition");
        EntityHandle dtypeDoc = Handle(4, "text_composition");
        EntityHandle shapeDoc = Handle(5, "text_composition");
        EntityHandle rowOne = Handle(6, "ffn_neuron");
        EntityHandle rowTwo = Handle(7, "ffn_neuron");

        FakeEntityReader entityReader = new();
        entityReader.AddOutbound(architecture, "has_tensor", tensor);
        entityReader.AddOutbound(tensor, "has_tensor_name", nameDoc);
        entityReader.AddOutbound(tensor, "has_dtype", dtypeDoc);
        entityReader.AddOutbound(tensor, "has_shape", shapeDoc);
        entityReader.AddSequence(tensor, rowOne, 1);
        entityReader.AddSequence(tensor, rowTwo, 2);

        FakeTextReader textReader = new();
        textReader.Add(nameDoc, "layers.0.mlp.up_proj.weight");
        textReader.Add(dtypeDoc, "F32");
        textReader.Add(shapeDoc, "[2,4]");

        FakePhysicalityReader physicalityReader = new();
        physicalityReader.AddContour(rowOne, [1.0, 2.0, 3.0, 4.0]);
        physicalityReader.AddContour(rowTwo, [5.0, 6.0, 7.0, 8.0]);

        SafetensorsRecomposer recomposer = new(entityReader, textReader, physicalityReader);

        SafetensorsFile file = await recomposer.RecomposeAsync(
            architecture,
            new RecompositionOptions { MaxDepth = 20 },
            CancellationToken.None);

        TensorData data = Assert.Single(file.Tensors).Value;
        Assert.Equal("F32", data.Dtype);
        Assert.Equal([2, 4], data.Shape);
        Assert.Equal(8 * 4, data.Data.Length);
        Assert.Equal([1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f], ReadSingles(data.Data));
    }

    private static EntityHandle Handle(byte seed, string entityTypeCode)
    {
        byte[] hash = new byte[32];
        hash[0] = seed;
        return new EntityHandle(hash, entityTypeCode);
    }

    private static float[] ReadSingles(byte[] bytes)
    {
        float[] values = new float[bytes.Length / 4];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4, 4));
        }
        return values;
    }

    private sealed class FakeEntityReader : IEntityReader
    {
        private readonly Dictionary<(EntityHandle Source, string EdgeType), List<EntityHandle>> _outbound = [];
        private readonly Dictionary<EntityHandle, List<(EntityHandle Child, int Position)>> _sequence = [];

        public void AddOutbound(EntityHandle source, string edgeTypeCode, EntityHandle target)
        {
            (EntityHandle Source, string EdgeType) key = (source, edgeTypeCode);
            if (!_outbound.TryGetValue(key, out List<EntityHandle>? targets))
            {
                targets = [];
                _outbound[key] = targets;
            }

            targets.Add(target);
        }

        public void AddSequence(EntityHandle parent, EntityHandle child, int position)
        {
            if (!_sequence.TryGetValue(parent, out List<(EntityHandle Child, int Position)>? children))
            {
                children = [];
                _sequence[parent] = children;
            }

            children.Add((child, position));
        }

        public Task<IReadOnlyList<EntityHandle>> ResolveEntityHandlesAsync(
            IReadOnlyList<byte[]> hashes, IReadOnlyList<string> entityTypeCodes, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<EntityHandle>>([]);

        public Task<IReadOnlyDictionary<EntityHandle, EntityInfo>> GetEntityInfoAsync(
            IReadOnlyList<EntityHandle> entityHandles, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<EntityHandle, EntityInfo>>(
                new Dictionary<EntityHandle, EntityInfo>());

        public Task<IReadOnlyList<(EntityHandle Child, int Position)>> GetCompositionChildrenAsync(
            EntityHandle parent, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<(EntityHandle, int)>>(
                _sequence.TryGetValue(parent, out List<(EntityHandle Child, int Position)>? children)
                    ? children
                    : []);

        public Task<IReadOnlyDictionary<EdgeHandle, EdgeInfo>> GetEdgeInfoAsync(
            IReadOnlyList<EdgeHandle> edgeHandles, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<EdgeHandle, EdgeInfo>>(
                new Dictionary<EdgeHandle, EdgeInfo>());

        public Task<IReadOnlyList<EntityHandle>> FindEntitiesByContentAsync(
            string content, IReadOnlyList<string> entityTypeCodes, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<EntityHandle>>([]);

        public Task<IReadOnlyList<EntityHandle>> GetOutboundEdgeTargetsAsync(
            EntityHandle source, string edgeTypeCode, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<EntityHandle>>(
                _outbound.TryGetValue((source, edgeTypeCode), out List<EntityHandle>? targets)
                    ? targets
                    : []);
    }

    private sealed class FakeTextReader : ITextRecompositionReader
    {
        private readonly Dictionary<EntityHandle, string> _texts = [];

        public void Add(EntityHandle handle, string text) => _texts[handle] = text;

        public Task<string?> RecomposeTextAsync(EntityHandle root, int maxDepth, CancellationToken ct)
            => Task.FromResult(_texts.TryGetValue(root, out string? text) ? text : null);
    }

    private sealed class FakePhysicalityReader : IPhysicalityReader
    {
        private readonly Dictionary<EntityHandle, double[]> _contours = [];

        public void AddContour(EntityHandle handle, double[] contour) => _contours[handle] = contour;

        public Task<double[]?> GetLineString4dAsync(
            EntityHandle entity, string physicalityTypeCode, CancellationToken ct)
            => Task.FromResult(
                string.Equals(physicalityTypeCode, "contour", StringComparison.Ordinal)
                    && _contours.TryGetValue(entity, out double[]? contour)
                    ? contour
                    : null);

        public Task<(double X1, double X2, double X3, double X4)?> GetPoint4dAsync(
            EntityHandle entity, string physicalityTypeCode, CancellationToken ct)
            => Task.FromResult<(double, double, double, double)?>(null);
    }
}
