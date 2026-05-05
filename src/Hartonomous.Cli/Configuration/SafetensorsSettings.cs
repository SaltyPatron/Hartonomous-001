namespace Hartonomous.Cli.Configuration;

public sealed class SafetensorsSettings
{
    /// <summary>
    /// Path to the HuggingFace hub root containing models--{publisher}--{name}/
    /// directories. Absolute or relative (resolved against DataRoot).
    /// </summary>
    public string HubPath { get; set; } = "hub";

    /// <summary>
    /// Optional model-id allowlist in "publisher_slug/model_slug" form
    /// (e.g. ["sentence-transformers/all-MiniLM-L6-v2"]). Empty/null = all
    /// models discovered under HubPath are processed. Lets ModelDecomp run
    /// against a specific subset without copying files or skipping
    /// dependency phases.
    /// </summary>
    public string[]? ModelFilter { get; set; }
}
