using System.Collections.Generic;

namespace Hartonomous.Decomposers.Wiktionary;

/// <summary>
/// One relation (synonym / antonym / hypernym / hyponym / meronym / coordinate_term /
/// derived / related) pointing from the current entry (or a specific sense) to a target
/// headword. <see cref="SenseIndex"/> is non-null when the relation is per-sense (lifted
/// from <c>senses[i].{synonyms,...}</c>) and is the ordinal index into the entry's sense
/// list. Entry-level relations (lifted from the top-level lists) leave it null.
/// </summary>
internal sealed record WiktRelation(
    string Word,
    string? Source,
    IReadOnlyList<string> Tags,
    int? SenseIndex);
