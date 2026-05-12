using System.Collections.Generic;

namespace Hartonomous.Decomposers.Wiktionary;

internal sealed record WiktionaryJsonlLineChunk(
    long Index,
    IReadOnlyList<string> Lines,
    long BytesReadAfterChunk,
    long TotalBytes);
