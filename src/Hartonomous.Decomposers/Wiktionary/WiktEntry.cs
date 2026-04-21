using System.Collections.Generic;

namespace Hartonomous.Decomposers.Wiktionary;

internal sealed record WiktEntry(
    string Word,
    string Lang,
    string LangCode,
    string Pos,
    int? EtymologyNumber,
    string? EtymologyText,
    IReadOnlyList<WiktEtymologyTemplate> EtymologyTemplates,
    IReadOnlyList<WiktSense> Senses,
    IReadOnlyList<WiktForm> Forms,
    IReadOnlyList<WiktSound> Sounds,
    IReadOnlyList<WiktHyphenation> Hyphenations,
    IReadOnlyList<WiktTranslation> Translations,
    IReadOnlyList<WiktRelation> Synonyms,
    IReadOnlyList<WiktRelation> Antonyms,
    IReadOnlyList<WiktRelation> Hypernyms,
    IReadOnlyList<WiktRelation> Hyponyms,
    IReadOnlyList<WiktRelation> Meronyms,
    IReadOnlyList<WiktRelation> CoordinateTerms,
    IReadOnlyList<WiktRelation> Derived,
    IReadOnlyList<WiktRelation> Related);
