namespace Hartonomous.Decomposers.Safetensors.Packages;

public sealed record TensorMetadata
{
    public required string Name { get; init; }
    public required string Dtype { get; init; }
    public required int[] Shape { get; init; }
    public required long ByteOffset { get; init; }
    public required long ByteLength { get; init; }
    public string? Component { get; init; }
}
