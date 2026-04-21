using System.Collections.Generic;

namespace Hartonomous.Decomposers.Wiktionary;

internal sealed record WiktSense(
    IReadOnlyList<string> Glosses,
    IReadOnlyList<string> RawGlosses,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Categories,
    IReadOnlyList<WiktExample> Examples,
    IReadOnlyList<string> Senseid,
    IReadOnlyList<string> Wikidata);
