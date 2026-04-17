using System.Collections.Generic;

namespace Hartonomous.Decomposers.WordNet;

internal readonly record struct VerbSentenceIndex(
    string SenseKey,
    IReadOnlyList<int> SentenceIds);
