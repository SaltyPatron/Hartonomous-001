using System.Collections.Generic;

namespace Hartonomous.Decomposers.Wiktionary;

internal sealed record WiktForm(string Form, IReadOnlyList<string> Tags);
