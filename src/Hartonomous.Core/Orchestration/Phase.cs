namespace Hartonomous.Core.Orchestration;

public enum Phase
{
    // Foundation phases seed substrate invariants first. Semantic seed phases
    // add truth-grounding evidence; model decomposition can create its own
    // token/lemma/model entities from content and later converge with those
    // semantic seeds by hash identity.
    CoreAlgebra,
    UcdUca,
    Iso639,
    WordNetOmw,
    UniversalDeps,
    Wiktionary,
    Tatoeba,
    TextDecomp,
    // Model decomposition requires the foundation substrate, not the whole
    // semantic seed floor. WordNet/UD/Wiktionary/Tatoeba enrich model-derived
    // entities when present; they do not hard-block safetensors ingestion.
    ModelDecomp,
    SignificanceField,
    InferenceEngine,
    Validation
}
