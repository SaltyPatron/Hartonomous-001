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
    // Reference / encoding decomposers (Gate 1 #41 registration). Each takes
    // the standard source-directory root; they resolve their actual data file
    // via internal probing (Bcp47 walks ISO639/iana/language-subtag-registry.txt;
    // Iso15924 walks Unicode/iso15924/iso15924.txt; encoding decomposers are
    // synthetic — driven by the embedded UCD blob, no source file).
    public DecomposerSettings Bcp47 { get; set; } = new();
    public DecomposerSettings Iso15924 { get; set; } = new();
    public DecomposerSettings AsciiEncoding { get; set; } = new();
    public DecomposerSettings Iso88591Encoding { get; set; } = new();
}
