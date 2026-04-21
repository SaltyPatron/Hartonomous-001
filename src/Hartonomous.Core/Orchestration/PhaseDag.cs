namespace Hartonomous.Core.Orchestration;

public static class PhaseDag
{
    private static readonly Dictionary<Phase, Phase[]> Dependencies = new()
    {
        [Phase.CoreAlgebra] = [],
        [Phase.UcdUca] = [Phase.CoreAlgebra],
        [Phase.Iso639] = [Phase.UcdUca],
        [Phase.WordNetOmw] = [Phase.Iso639],
        [Phase.UniversalDeps] = [Phase.WordNetOmw],
        [Phase.ModelDecomp] = [Phase.UniversalDeps],
        [Phase.Wiktionary] = [Phase.ModelDecomp],
        [Phase.Tatoeba] = [Phase.Wiktionary],
        [Phase.TextDecomp] = [Phase.UcdUca],
        [Phase.SignificanceField] = [Phase.CoreAlgebra],
        [Phase.InferenceEngine] = [Phase.Tatoeba, Phase.ModelDecomp, Phase.TextDecomp, Phase.SignificanceField],
        [Phase.Validation] = [Phase.InferenceEngine],
    };

    public static IReadOnlyList<Phase> GetDependencies(Phase phase)
    {
        return Dependencies.TryGetValue(phase, out Phase[]? deps) ? deps : [];
    }

    public static IReadOnlyList<Phase> TopologicalOrder()
    {
        HashSet<Phase> visited = [];
        List<Phase> result = [];
        foreach (Phase p in Enum.GetValues<Phase>())
        {
            Visit(p, visited, result);
        }
        return result;
    }

    private static void Visit(Phase phase, HashSet<Phase> visited, List<Phase> result)
    {
        if (!visited.Add(phase))
        {
            return;
        }
        foreach (Phase dep in GetDependencies(phase))
        {
            Visit(dep, visited, result);
        }
        result.Add(phase);
    }
}
