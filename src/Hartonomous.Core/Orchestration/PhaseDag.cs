namespace Hartonomous.Core.Orchestration;

public static class PhaseDag
{
    // Lexical floor (UCD/UCA → ISO 639 → WordNet/OMW → UD → Wiktionary →
    // Tatoeba → TextDecomp) ingests BEFORE model decomposition. AI models
    // are not lexical seed — they are content that references the seed
    // (covers_lemma, has_token_string, vocab_coverage). ModelDecomp must
    // therefore depend on the full lexical floor, not just UD.
    //
    // The previous DAG declared Wiktionary depending on ModelDecomp and
    // ModelDecomp on UniversalDeps only, which forced ModelDecomp to run
    // BEFORE the Wiktionary/Tatoeba/TextDecomp seed entities existed —
    // contradicting the explicit ordering comment on the Phase enum and
    // breaking covers_lemma / has_token_string emission against absent
    // word_form/lemma rows.
    private static readonly Dictionary<Phase, Phase[]> Dependencies = new()
    {
        [Phase.CoreAlgebra] = [],
        [Phase.UcdUca] = [Phase.CoreAlgebra],
        [Phase.Iso639] = [Phase.UcdUca],
        [Phase.WordNetOmw] = [Phase.Iso639],
        [Phase.UniversalDeps] = [Phase.WordNetOmw],
        [Phase.Wiktionary] = [Phase.UniversalDeps],
        [Phase.Tatoeba] = [Phase.Wiktionary],
        [Phase.TextDecomp] = [Phase.UcdUca],
        [Phase.ModelDecomp] = [Phase.UniversalDeps, Phase.Wiktionary, Phase.Tatoeba, Phase.TextDecomp],
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
