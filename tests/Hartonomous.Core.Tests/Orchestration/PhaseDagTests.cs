using Hartonomous.Core.Orchestration;

namespace Hartonomous.Core.Tests.Orchestration;

public sealed class PhaseDagTests
{
    [Fact]
    public void GetDependencies_CoreAlgebra_HasNone()
    {
        IReadOnlyList<Phase> deps = PhaseDag.GetDependencies(Phase.CoreAlgebra);
        Assert.Empty(deps);
    }

    [Fact]
    public void GetDependencies_UcdUca_DependsOnCoreAlgebra()
    {
        IReadOnlyList<Phase> deps = PhaseDag.GetDependencies(Phase.UcdUca);
        Assert.Single(deps);
        Assert.Equal(Phase.CoreAlgebra, deps[0]);
    }

    [Fact]
    public void GetDependencies_InferenceEngine_HasFourDependencies()
    {
        IReadOnlyList<Phase> deps = PhaseDag.GetDependencies(Phase.InferenceEngine);
        Assert.Equal(4, deps.Count);
        Assert.Contains(Phase.Tatoeba, deps);
        Assert.Contains(Phase.ModelDecomp, deps);
        Assert.Contains(Phase.TextDecomp, deps);
        Assert.Contains(Phase.SignificanceField, deps);
    }

    [Fact]
    public void GetDependencies_UnknownPhase_ReturnsEmpty()
    {
        IReadOnlyList<Phase> deps = PhaseDag.GetDependencies((Phase)999);
        Assert.Empty(deps);
    }

    [Fact]
    public void TopologicalOrder_ContainsAllPhases()
    {
        IReadOnlyList<Phase> order = PhaseDag.TopologicalOrder();
        Phase[] allPhases = Enum.GetValues<Phase>();
        Assert.Equal(allPhases.Length, order.Count);
        foreach (Phase p in allPhases)
        {
            Assert.Contains(p, order);
        }
    }

    [Fact]
    public void TopologicalOrder_DependenciesAppearBeforeDependents()
    {
        IReadOnlyList<Phase> order = PhaseDag.TopologicalOrder();
        Dictionary<Phase, int> indexOf = new();
        for (int i = 0; i < order.Count; i++)
        {
            indexOf[order[i]] = i;
        }

        foreach (Phase phase in Enum.GetValues<Phase>())
        {
            foreach (Phase dep in PhaseDag.GetDependencies(phase))
            {
                Assert.True(indexOf[dep] < indexOf[phase],
                    $"{dep} (index {indexOf[dep]}) should appear before {phase} (index {indexOf[phase]})");
            }
        }
    }

    [Fact]
    public void TopologicalOrder_CoreAlgebraIsFirst()
    {
        IReadOnlyList<Phase> order = PhaseDag.TopologicalOrder();
        Assert.Equal(Phase.CoreAlgebra, order[0]);
    }

    [Fact]
    public void TopologicalOrder_ValidationIsLast()
    {
        IReadOnlyList<Phase> order = PhaseDag.TopologicalOrder();
        Assert.Equal(Phase.Validation, order[^1]);
    }

    [Fact]
    public void TopologicalOrder_IsDeterministic()
    {
        IReadOnlyList<Phase> first = PhaseDag.TopologicalOrder();
        IReadOnlyList<Phase> second = PhaseDag.TopologicalOrder();
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(Phase.CoreAlgebra)]
    [InlineData(Phase.UcdUca)]
    [InlineData(Phase.Iso639)]
    [InlineData(Phase.WordNetOmw)]
    [InlineData(Phase.UniversalDeps)]
    [InlineData(Phase.ModelDecomp)]
    [InlineData(Phase.Wiktionary)]
    [InlineData(Phase.Tatoeba)]
    [InlineData(Phase.SignificanceField)]
    [InlineData(Phase.InferenceEngine)]
    [InlineData(Phase.Validation)]
    public void GetDependencies_EveryPhaseHasEntry(Phase phase)
    {
        // Should not throw — every phase should be in the DAG.
        IReadOnlyList<Phase> deps = PhaseDag.GetDependencies(phase);
        Assert.NotNull(deps);
    }
}
