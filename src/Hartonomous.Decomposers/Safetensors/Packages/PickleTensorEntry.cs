namespace Hartonomous.Decomposers.Safetensors.Packages;

public sealed record PickleTensorEntry(
    string Name,
    string DtypeCanonical,
    int[] Shape,
    string StorageKey,
    long StorageElementOffset,
    long ByteLength);
