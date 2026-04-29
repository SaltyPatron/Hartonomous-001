namespace Hartonomous.Cli.Configuration;

/// <summary>
/// Root configuration object loaded from appsettings.json + environment
/// variables (HARTONOMOUS__*). Mirrors the PowerShell scripts/config.psd1
/// Seed section so the C# CLI and the PS scripts read the SAME per-decomposer
/// path layout — no more hardcoded Path.Combine literals scattered across
/// RunPhasesAsync, no more --source flag stamping the same root onto every
/// decomposer regardless of whether their data lives there.
///
/// Per-decomposer paths can be absolute (used as-is) or relative
/// (resolved against DataRoot). LanguageFilter and ModelFilter let
/// individual phases narrow scope without touching others.
/// </summary>
public sealed class HartonomousOptions
{
    public string DataRoot { get; set; } = "D:\\Models";
    public string? ConnectionString { get; set; }
    public DecomposersOptions Decomposers { get; set; } = new();
}

public sealed class DecomposersOptions
{
    public DecomposerSettings Ucd        { get; set; } = new();
    public DecomposerSettings Iso639     { get; set; } = new();
    public DecomposerSettings WordNet    { get; set; } = new();
    public DecomposerSettings Omw        { get; set; } = new();
    public DecomposerSettings Ud         { get; set; } = new();
    public SafetensorsSettings Safetensors { get; set; } = new();
    public DecomposerSettings Wiktionary { get; set; } = new();
    public DecomposerSettings Tatoeba    { get; set; } = new();
    public DecomposerSettings Text       { get; set; } = new();
}

public sealed class DecomposerSettings
{
    /// <summary>
    /// Path to the data the decomposer reads. Absolute = used as-is.
    /// Relative = resolved against <see cref="HartonomousOptions.DataRoot"/>.
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Optional language-code allowlist. Each decomposer compares against
    /// its source's native code form (UD = ISO 639-1, OMW/Tatoeba/WordNet
    /// = ISO 639-3, Wiktionary = ISO 639-1). Include both 2- and 3-letter
    /// variants for safety.
    /// </summary>
    public string[]? LanguageFilter { get; set; }
}

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
