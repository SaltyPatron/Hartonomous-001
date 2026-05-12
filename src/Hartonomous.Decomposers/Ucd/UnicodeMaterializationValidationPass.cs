namespace Hartonomous.Decomposers.Ucd;

internal sealed class UnicodeMaterializationValidationPass : IUnicodeSeedPass
{
    public string PassId => "unicode.materialization_validation";

    public IReadOnlyList<string> Dependencies => ["unicode.case_edges"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        UcdMaterializationCounts counts = await UnicodeSql.LoadMaterializationCountsAsync(context.Connection, ct);
        if (counts.CodepointClassifications < UnicodeSql.MaxCodepoints)
        {
            throw new InvalidOperationException(
                $"UCD/UCA materialization incomplete: expected at least {UnicodeSql.MaxCodepoints:N0} unicode_consortium codepoint classifications, found {counts.CodepointClassifications:N0}.");
        }

        if (counts.CodepointProperties < UnicodeSql.MaxCodepoints)
        {
            throw new InvalidOperationException(
                $"UCD/UCA materialization incomplete: expected at least {UnicodeSql.MaxCodepoints:N0} codepoint_property rows, found {counts.CodepointProperties:N0}.");
        }

        if (counts.SimpleCaseEdges <= 0)
        {
            throw new InvalidOperationException(
                "UCD/UCA materialization incomplete: expected Unicode case mapping edges, found none.");
        }

        if (counts.SimpleCaseEdgesWithoutGeometry > 0)
        {
            throw new InvalidOperationException(
                $"UCD/UCA materialization incomplete: {counts.SimpleCaseEdgesWithoutGeometry:N0} Unicode case mapping edges are missing trajectory geometry.");
        }

        if (counts.SignificanceContexts <= 0)
        {
            throw new InvalidOperationException(
                "UCD/UCA materialization incomplete: significance_context has no arenas.");
        }

        long expectedSimpleCaseEdgeSignificance = counts.SimpleCaseEdges * counts.SignificanceContexts;
        if (counts.SimpleCaseEdgeSignificance < expectedSimpleCaseEdgeSignificance)
        {
            throw new InvalidOperationException(
                $"UCD/UCA materialization incomplete: expected {expectedSimpleCaseEdgeSignificance:N0} Unicode case edge significance rows, found {counts.SimpleCaseEdgeSignificance:N0}.");
        }

        await context.ReportAsync(PassId, counts.CodepointClassifications, 0, ct);
    }
}
