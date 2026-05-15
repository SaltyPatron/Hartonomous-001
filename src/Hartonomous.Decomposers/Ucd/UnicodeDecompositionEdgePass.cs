namespace Hartonomous.Decomposers.Ucd;

internal sealed class UnicodeDecompositionEdgePass : IUnicodeSeedPass
{
    public string PassId => "unicode.decomposition_edges";

    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        long edgeRows = await UnicodeSql.PopulateUnicodeDecompositionEdgesAsync(context.Connection, ct);
        await context.ReportAsync(PassId, edgeRows, 0, ct);
    }
}
