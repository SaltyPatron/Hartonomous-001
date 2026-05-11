namespace Hartonomous.Decomposers.Ucd;

internal sealed class UnicodeCaseEdgePass : IUnicodeSeedPass
{
    public string PassId => "unicode.case_edges";

    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_properties"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        long memberRows = await UnicodeSql.PopulateUnicodeCaseEdgesAsync(context.Connection, ct);
        await context.ReportAsync(PassId, memberRows, 0, ct);
    }
}
