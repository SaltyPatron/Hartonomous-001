using System.Collections.Generic;

namespace Hartonomous.Decomposers.WordNet;

internal readonly record struct MorphException(
    string InflectedForm,
    IReadOnlyList<string> BaseForms,
    char Pos);
