using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Hartonomous.Decomposers.Safetensors;
using Hartonomous.Decomposers.Safetensors.Passes;
using Hartonomous.Decomposers.Safetensors.TupleResolution;
using Xunit;

namespace Hartonomous.Decomposers.Tests.Safetensors;

public sealed class TupleResolverFluxVaeTests
{
    [Fact]
    public void Attn1PointwiseConv_ClassifiesAsLinearVaeAttention()
    {
        TensorHandle q = TupleResolverTestHelpers.Tensor("encoder.mid.attn_1.q.weight", [512, 512, 1, 1]);
        TensorHandle k = TupleResolverTestHelpers.Tensor("encoder.mid.attn_1.k.weight", [512, 512, 1, 1]);
        TensorHandle v = TupleResolverTestHelpers.Tensor("encoder.mid.attn_1.v.weight", [512, 512, 1, 1]);
        TensorHandle o = TupleResolverTestHelpers.Tensor("encoder.mid.attn_1.proj_out.weight", [512, 512, 1, 1]);

        TupleResolver resolver = new();
        (var classifications, var tuples) = resolver.Resolve("AutoencoderKL", [q, k, v, o]);

        Assert.All(new[] { q, k, v, o }, t =>
        {
            Assert.Equal(PrimitiveKind.Linear, classifications[t].Primitive);
            Assert.Equal(ArchetypeTuple.VaeAttnBlock, classifications[t].Tuple);
            Assert.Equal(ModalityHint.ImagePatch, classifications[t].Modality);
        });
        Assert.Equal(TupleSlot.Q, classifications[q].Slot);
        Assert.Equal(TupleSlot.K, classifications[k].Slot);
        Assert.Equal(TupleSlot.V, classifications[v].Slot);
        Assert.Equal(TupleSlot.O, classifications[o].Slot);

        ResolvedTuple tuple = tuples.Single(t => t.Tuple == ArchetypeTuple.VaeAttnBlock);
        Assert.Equal(4, tuple.Members.Count);
    }

    [Fact]
    public void Materializer_CollapsesPointwiseConvShapeWithoutReorderingData()
    {
        string path = Path.Combine(Path.GetTempPath(), $"hartonomous-1x1-{System.Guid.NewGuid():N}.bin");
        byte[] bytes = new byte[6 * sizeof(float)];
        for (int i = 0; i < 6; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), i + 1);
        }

        try
        {
            File.WriteAllBytes(path, bytes);
            TensorHandle q = TupleResolverTestHelpers.Tensor("encoder.mid.attn_1.q.weight", [2, 3, 1, 1]);
            TensorHandle onDisk = q with
            {
                Info = q.Info with
                {
                    BeginByte = 0,
                    EndByte = bytes.Length,
                    FilePath = path
                }
            };
            TupleMember member = new(TupleSlot.Q, onDisk, FusedSplit: null);

            Assert.Equal(new long[] { 2, 3 }, TensorMemberMaterializer.EffectiveShape(member));
            Assert.Equal(new double[] { 1, 2, 3, 4, 5, 6 }, TensorMemberMaterializer.ReadAsDouble(member));
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
