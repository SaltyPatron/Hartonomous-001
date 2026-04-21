using System.Collections.Generic;

namespace Hartonomous.Decomposers.Wiktionary;

internal sealed record WiktHyphenation(IReadOnlyList<string> Parts);
