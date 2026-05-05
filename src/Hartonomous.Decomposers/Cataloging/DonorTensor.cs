using System.Text.Json.Serialization;

namespace Hartonomous.Decomposers.Cataloging;

public sealed record DonorTensor
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("dtype")]
    public required string Dtype { get; init; }

    [JsonPropertyName("shape")]
    public required IReadOnlyList<int> Shape { get; init; }

    [JsonPropertyName("byte_length")]
    public long ByteLength { get; init; }

    [JsonPropertyName("component")]
    public string? Component { get; init; }

    [JsonPropertyName("lobe")]
    public required string Lobe { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }
}
