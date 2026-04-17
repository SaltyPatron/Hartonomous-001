namespace Hartonomous.Decomposers.WordNet;

internal readonly record struct SenseIndexEntry(
    string SenseKey,
    int SynsetOffset,
    int SenseNumber,
    int TagCount);
