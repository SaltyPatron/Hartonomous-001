namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Collects unique reference table values encountered during XML parsing.
/// After parsing, these values are used to populate reference tables before
/// creating junction table entries.
/// </summary>
internal sealed class ReferenceTableCollector
{
    // Value → accumulated during parsing. ID → resolved after reference table population.
    public Dictionary<string, int> GeneralCategories { get; } = new();
    public Dictionary<string, int> Scripts { get; } = new();
    public Dictionary<string, (int RangeStart, int RangeEnd)> Blocks { get; } = new();
    public Dictionary<(string Code, string Category), int> BreakProperties { get; } = new();

    // Resolved IDs (populated after reference table inserts)
    public Dictionary<string, int> GeneralCategoryIds { get; } = new();
    public Dictionary<string, int> ScriptIds { get; } = new();
    public Dictionary<string, int> BlockIds { get; } = new();
    public Dictionary<(string Code, string Category), int> BreakPropertyIds { get; } = new();

    public void AddGeneralCategory(string code)
    {
        GeneralCategories.TryAdd(code, 0);
    }

    public void AddScript(string code)
    {
        Scripts.TryAdd(code, 0);
    }

    public void AddBlock(string code, int rangeStart, int rangeEnd)
    {
        Blocks.TryAdd(code, (rangeStart, rangeEnd));
    }

    public void AddBreakProperty(string code, string category)
    {
        BreakProperties.TryAdd((code, category), 0);
    }
}
