namespace Hartonomous.Decomposers.Ucd;

internal sealed class CodepointPropertyPass : IUnicodeSeedPass
{
    public string PassId => "unicode.codepoint_properties";

    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        long propertyRows = await UnicodeSql.PopulateCodepointPropertiesAsync(context.Connection, ct);
        await context.ReportAsync(PassId, propertyRows, 0, ct);
    }
}
