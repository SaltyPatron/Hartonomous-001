using System.Text.Json.Serialization;

namespace Hartonomous.Decomposers.Cataloging;

public sealed record DonorPackageFile
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("format")]
    public required string Format { get; init; }

    [JsonPropertyName("tensor_count")]
    public int TensorCount { get; init; }
}
