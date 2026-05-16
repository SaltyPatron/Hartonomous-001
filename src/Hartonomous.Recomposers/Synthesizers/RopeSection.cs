using System.Text.Json.Serialization;

namespace Hartonomous.Recomposers.Synthesizers;

public sealed class RopeSection
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("theta")]
    public double Theta { get; set; } = 10000.0;
}
