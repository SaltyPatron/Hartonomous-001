namespace Hartonomous.Cli.Configuration;

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
