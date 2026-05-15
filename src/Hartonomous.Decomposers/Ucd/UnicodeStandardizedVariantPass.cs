namespace Hartonomous.Decomposers.Ucd;

internal sealed class UnicodeStandardizedVariantPass : IUnicodeSeedPass
{
    public string PassId => "unicode.standardized_variants";

    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        long edgeRows = await UnicodeSql.PopulateUnicodeStandardizedVariantsAsync(context.Connection, ct);
        await context.ReportAsync(PassId, edgeRows, 0, ct);
    }
}
