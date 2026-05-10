using Hartonomous.Core.Ingestion;
using Hartonomous.Decomposers.Safetensors;
using Hartonomous.Decomposers.Safetensors.Passes;

namespace Hartonomous.Decomposers.Tests.Safetensors;

/// <summary>
/// Shared helpers for TupleResolver and TuplePass tests. Synthesizes
/// TensorHandle objects without touching disk — the resolver only inspects
/// names and shapes, so synthetic inputs are sufficient for dispatch tests.
/// </summary>
internal static class TupleResolverTestHelpers
{
    /// <summary>Synthesize a TensorHandle from name + shape (used for resolver dispatch tests).</summary>
    public static TensorHandle Tensor(string name, long[] shape, SafetensorsDtype dtype = SafetensorsDtype.F32)
    {
        SafetensorsTensorInfo info = new(
            Name: name,
            Dtype: dtype,
            Shape: shape,
            BeginByte: 0,
            EndByte: 1,
            FilePath: "test://synthetic");
        byte[] hash = HashFromName(name);
        return new TensorHandle(
            info,
            new TensorClassification(
                PrimitiveKind.Unknown, ArchetypeTuple.Unknown, TupleSlot.Unknown,
                LayerIndex: null, HeadIndex: null, ExpertIndex: null,
                Modality: ModalityHint.Unknown, AdaptationOf: null),
            hash,
            new EntityHandle(hash, "tensor"));
    }

    /// <summary>Stable per-name hash so equal names produce equal entity hashes.</summary>
    private static byte[] HashFromName(string name)
    {
        byte[] hash = new byte[32];
        int seed = name.GetHashCode(System.StringComparison.Ordinal);
        for (int i = 0; i < 32; i++)
        {
            hash[i] = (byte)((seed >> (i % 4 * 8)) ^ i);
        }
        return hash;
    }
}
