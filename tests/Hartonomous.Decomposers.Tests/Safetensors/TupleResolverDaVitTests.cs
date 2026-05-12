using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Hartonomous.Decomposers.Safetensors;
using Hartonomous.Decomposers.Safetensors.Passes;
using Hartonomous.Decomposers.Safetensors.TupleResolution;
using Xunit;

namespace Hartonomous.Decomposers.Tests.Safetensors;

public sealed class TupleResolverDaVitTests
{
    [Fact]
    public void FusedQkv_EmitsLogicalAttentionMembersWithSlices()
    {
        TensorHandle qkv = TupleResolverTestHelpers.Tensor(
            "vision_tower.1.2.channel_block.channel_attn.fn.qkv.weight",
            [12, 4]);
        TensorHandle o = TupleResolverTestHelpers.Tensor(
            "vision_tower.1.2.channel_block.channel_attn.fn.proj.weight",
            [4, 4]);

        TupleResolver resolver = new();
        (var classifications, var tuples) = resolver.Resolve("Florence2ForConditionalGeneration", [qkv, o]);

        TensorClassification classification = classifications[qkv];
        Assert.Equal(PrimitiveKind.Linear, classification.Primitive);
        Assert.Equal(ArchetypeTuple.AttentionBlock, classification.Tuple);
        Assert.Equal(TupleSlot.Unknown, classification.Slot);
        Assert.Equal(ModalityHint.ImagePatch, classification.Modality);
        Assert.Collection(classification.FusedMembers!,
            q => Assert.Equal((TupleSlot.Q, 0, 0, 4), (q.Slot, q.Slice.Axis, q.Slice.Offset, q.Slice.Length)),
            k => Assert.Equal((TupleSlot.K, 0, 4, 4), (k.Slot, k.Slice.Axis, k.Slice.Offset, k.Slice.Length)),
            v => Assert.Equal((TupleSlot.V, 0, 8, 4), (v.Slot, v.Slice.Axis, v.Slice.Offset, v.Slice.Length)));

        ResolvedTuple tuple = tuples.Single(t => t.Tuple == ArchetypeTuple.AttentionBlock && t.LayerIndex == 1);
        Assert.Contains(tuple.Members, m => m.Slot == TupleSlot.Q && m.FusedSplit?.Offset == 0);
        Assert.Contains(tuple.Members, m => m.Slot == TupleSlot.K && m.FusedSplit?.Offset == 4);
        Assert.Contains(tuple.Members, m => m.Slot == TupleSlot.V && m.FusedSplit?.Offset == 8);
        Assert.Contains(tuple.Members, m => m.Slot == TupleSlot.O && m.FusedSplit is null);
    }

    [Fact]
    public void Materializer_ReadsOnlyRequestedFusedSlice()
    {
        string path = Path.Combine(Path.GetTempPath(), $"hartonomous-qkv-{Guid.NewGuid():N}.bin");
        byte[] bytes = new byte[12 * sizeof(float)];
        for (int i = 0; i < 12; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), i);
        }

        try
        {
            File.WriteAllBytes(path, bytes);
            TensorHandle qkv = TupleResolverTestHelpers.Tensor(
                "vision_tower.0.0.channel_block.channel_attn.fn.qkv.weight",
                [6, 2]);
            SafetensorsTensorInfo info = qkv.Info with
            {
                BeginByte = 0,
                EndByte = bytes.Length,
                FilePath = path
            };
            TensorHandle onDisk = qkv with { Info = info };
            TupleMember k = new(TupleSlot.K, onDisk, new FusedTensorSlice(2, 2, 0));

            Assert.Equal(new long[] { 2, 2 }, TensorMemberMaterializer.EffectiveShape(k));
            Assert.Equal(new double[] { 4.0, 5.0, 6.0, 7.0 }, TensorMemberMaterializer.ReadAsDouble(k));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
