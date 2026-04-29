namespace Hartonomous.Core.Orchestration;

public enum Phase
{
    // Lexical floor ingests first. Models reference lemmas/word_forms/synsets
    // produced by these phases — they must exist before model decomposition
    // emits edges into them.
    CoreAlgebra,
    UcdUca,
    Iso639,
    WordNetOmw,
    UniversalDeps,
    Wiktionary,
    Tatoeba,
    TextDecomp,
    // Model decomposition runs AFTER the lexical floor is in place. AI models
    // are not lexical seed — they are content that references the seed.
    ModelDecomp,
    SignificanceField,
    InferenceEngine,
    Validation
}
