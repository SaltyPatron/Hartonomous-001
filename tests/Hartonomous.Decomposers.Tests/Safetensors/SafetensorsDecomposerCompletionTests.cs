using Hartonomous.Decomposers.Safetensors;

namespace Hartonomous.Decomposers.Tests.Safetensors;

public sealed class SafetensorsDecomposerCompletionTests
{
    [Fact]
    public void ThrowIfIncompleteModelDecomposition_NoDiscoveredModels_Throws()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            SafetensorsDecomposer.ThrowIfIncompleteModelDecomposition(0, 0, []));

        Assert.Contains("discovered zero models", ex.Message);
    }

    [Fact]
    public void ThrowIfIncompleteModelDecomposition_AllModelsFailed_Throws()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            SafetensorsDecomposer.ThrowIfIncompleteModelDecomposition(2, 0, ["org/a", "org/b"]));

        Assert.Contains("completed zero of 2", ex.Message);
        Assert.Contains("org/a", ex.Message);
        Assert.Contains("org/b", ex.Message);
    }

    [Fact]
    public void ThrowIfIncompleteModelDecomposition_PartialFailure_Throws()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            SafetensorsDecomposer.ThrowIfIncompleteModelDecomposition(3, 2, ["org/broken"]));

        Assert.Contains("completed 2 of 3", ex.Message);
        Assert.Contains("org/broken", ex.Message);
    }

    [Fact]
    public void ThrowIfIncompleteModelDecomposition_AllModelsCompleted_DoesNotThrow()
    {
        SafetensorsDecomposer.ThrowIfIncompleteModelDecomposition(2, 2, []);
    }
}
