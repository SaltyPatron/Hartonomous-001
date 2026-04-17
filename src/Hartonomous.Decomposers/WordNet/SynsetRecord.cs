using System.Collections.Generic;

namespace Hartonomous.Decomposers.WordNet;

internal sealed record SynsetRecord(
    int Offset,
    int LexFileNum,
    char SsType,
    IReadOnlyList<SynsetWord> Words,
    IReadOnlyList<PointerRecord> Pointers,
    IReadOnlyList<FrameRef> Frames,
    string Gloss);
