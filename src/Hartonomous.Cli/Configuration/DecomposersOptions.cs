namespace Hartonomous.Cli.Configuration;

public sealed class DecomposersOptions
{
    public DecomposerSettings Ucd { get; set; } = new();
    public DecomposerSettings Iso639 { get; set; } = new();
    public DecomposerSettings WordNet { get; set; } = new();
    public DecomposerSettings Omw { get; set; } = new();
    public DecomposerSettings Ud { get; set; } = new();
    public SafetensorsSettings Safetensors { get; set; } = new();
    public DecomposerSettings Wiktionary { get; set; } = new();
    public DecomposerSettings Tatoeba { get; set; } = new();
    public DecomposerSettings Text { get; set; } = new();
}
