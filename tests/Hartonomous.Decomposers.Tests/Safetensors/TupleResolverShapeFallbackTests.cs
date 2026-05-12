using Hartonomous.Decomposers.Safetensors;
using Hartonomous.Decomposers.Safetensors.Passes;
using Hartonomous.Decomposers.Safetensors.TupleResolution;
using Xunit;

namespace Hartonomous.Decomposers.Tests.Safetensors;

public sealed class TupleResolverShapeFallbackTests
{
    [Fact]
    public void UnknownRank2Tensor_ClassifiesAsLinearPrimitiveOnly()
    {
        TensorHandle tensor = TupleResolverTestHelpers.Tensor("unmapped.proj.weight", [128, 64]);

        TupleResolver resolver = new();
        (var classifications, var tuples) = resolver.Resolve("UnknownArchitecture", [tensor]);

        TensorClassification classification = classifications[tensor];
        Assert.Equal(PrimitiveKind.Linear, classification.Primitive);
        Assert.Equal(ArchetypeTuple.Unknown, classification.Tuple);
        Assert.Equal(TupleSlot.Unknown, classification.Slot);
        Assert.Empty(tuples);
    }

    [Fact]
    public void UnknownPointwiseConvTensor_ClassifiesAsLinearPrimitiveOnly()
    {
        TensorHandle tensor = TupleResolverTestHelpers.Tensor("unmapped.attn.q.weight", [320, 320, 1, 1]);

        TupleResolver resolver = new();
        (var classifications, _) = resolver.Resolve("UnknownArchitecture", [tensor]);

        Assert.Equal(PrimitiveKind.Linear, classifications[tensor].Primitive);
    }

    [Fact]
    public void UnknownSpatialKernelTensor_ClassifiesAsLocalKernelPrimitiveOnly()
    {
        TensorHandle tensor = TupleResolverTestHelpers.Tensor("unmapped.conv.weight", [64, 3, 7, 7]);

        TupleResolver resolver = new();
        (var classifications, _) = resolver.Resolve("UnknownArchitecture", [tensor]);

        Assert.Equal(PrimitiveKind.LocalKernel, classifications[tensor].Primitive);
    }

    [Fact]
    public void UnknownOneDimensionalBias_DoesNotPretendToBeNormalization()
    {
        TensorHandle tensor = TupleResolverTestHelpers.Tensor("unmapped.proj.bias", [128]);

        TupleResolver resolver = new();
        (var classifications, _) = resolver.Resolve("UnknownArchitecture", [tensor]);

        Assert.False(classifications.ContainsKey(tensor));
    }
}
