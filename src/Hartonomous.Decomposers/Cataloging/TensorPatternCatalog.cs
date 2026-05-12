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
