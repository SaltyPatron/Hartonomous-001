namespace Hartonomous.Decomposers.Ucd;

internal sealed class UnicodeConfusablePass : IUnicodeSeedPass
{
    public string PassId => "unicode.confusables";

    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        long edgeRows = await UnicodeSql.PopulateUnicodeConfusablesAsync(context.Connection, ct);
        await context.ReportAsync(PassId, edgeRows, 0, ct);
    }
}
