namespace Hartonomous.Decomposers.Ucd;

internal sealed class UnicodeRadicalStrokePass : IUnicodeSeedPass
{
    public string PassId => "unicode.radical_stroke";

    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        long edgeRows = await UnicodeSql.PopulateUnicodeRadicalStrokeAsync(context.Connection, ct);
        await context.ReportAsync(PassId, edgeRows, 0, ct);
    }
}
