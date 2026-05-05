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
