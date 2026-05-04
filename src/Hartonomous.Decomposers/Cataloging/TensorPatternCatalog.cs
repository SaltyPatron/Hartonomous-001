using System.Text.Json.Serialization;

namespace Hartonomous.Decomposers.Cataloging;

public sealed record TensorPatternCatalog
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("generated_at_utc")]
    public required DateTime GeneratedAtUtc { get; init; }

    [JsonPropertyName("entries")]
    public required IReadOnlyList<TensorPatternEntry> Entries { get; init; }
}

public sealed record TensorPatternEntry
{
    [JsonPropertyName("tensor_name")]
    public required string TensorName { get; init; }

    [JsonPropertyName("lobe")]
    public required string Lobe { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("architecture_class")]
    public required string ArchitectureClass { get; init; }

    [JsonPropertyName("observed_in_models")]
    public required IReadOnlyList<string> ObservedInModels { get; init; }

    [JsonPropertyName("example_shape")]
    public IReadOnlyList<int>? ExampleShape { get; init; }

    [JsonPropertyName("example_dtype")]
    public string? ExampleDtype { get; init; }
}
