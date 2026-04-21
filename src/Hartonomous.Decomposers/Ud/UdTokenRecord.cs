using System.Collections.Generic;

namespace Hartonomous.Decomposers.Ud;

/// <summary>
/// One parsed CoNLL-U token row. Fields use '_' sentinel from the source → null here.
/// </summary>
internal sealed record UdTokenRecord(
    string Id,
    string Form,
    string? Lemma,
    string? Upos,
    string? Xpos,
    IReadOnlyList<UdMorphFeature> Feats,
    string? Head,
    string? Deprel,
    string? Deps,
    string? Misc);
