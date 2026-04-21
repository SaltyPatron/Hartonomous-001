using System.Collections.Generic;

namespace Hartonomous.Decomposers.Wiktionary;

internal sealed record WiktTranslation(
    string? LangName,
    string? LangCode,
    string Word,
    string? Sense,
    string? Roman,
    IReadOnlyList<string> Tags);
