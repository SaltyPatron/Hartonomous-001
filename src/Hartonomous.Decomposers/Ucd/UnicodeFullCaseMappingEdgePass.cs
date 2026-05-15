namespace Hartonomous.Decomposers.Ucd;

internal sealed class UnicodeFullCaseMappingEdgePass : IUnicodeSeedPass
{
    public string PassId => "unicode.full_case_mapping_edges";

    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        long edgeRows = await UnicodeSql.PopulateUnicodeFullCaseMappingEdgesAsync(context.Connection, ct);
        await context.ReportAsync(PassId, edgeRows, 0, ct);
    }
}
