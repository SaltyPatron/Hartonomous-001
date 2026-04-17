namespace Hartonomous.Decomposers.WordNet;

internal readonly record struct PointerRecord(
    string Symbol,
    int TargetOffset,
    char TargetPos,
    int SourceWordNum,
    int TargetWordNum);
