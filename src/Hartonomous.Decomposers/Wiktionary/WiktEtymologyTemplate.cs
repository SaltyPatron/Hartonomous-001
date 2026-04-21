using System.Collections.Generic;

namespace Hartonomous.Decomposers.Wiktionary;

/// <summary>
/// One wiktextract etymology template. <c>Name</c> is the template kind
/// (inh = inherited, der = derived, bor = borrowed, cog = cognate, cal = calque,
/// m = mention, etymon, l = link, etc.). <c>Args</c> is the numbered+named
/// argument dict; key "1" is usually the target language code, "2" the source
/// word, "3" the gloss. <c>Expansion</c> is the fully-rendered text.
/// </summary>
internal sealed record WiktEtymologyTemplate(
    string Name,
    IReadOnlyDictionary<string, string> Args,
    string? Expansion);
