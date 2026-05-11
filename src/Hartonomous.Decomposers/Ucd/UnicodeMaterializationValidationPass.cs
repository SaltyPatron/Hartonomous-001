namespace Hartonomous.Decomposers.Ucd;

internal sealed class UnicodeMaterializationValidationPass : IUnicodeSeedPass
{
    public string PassId => "unicode.materialization_validation";

    public IReadOnlyList<string> Dependencies => ["unicode.case_edges"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        long codepointClassifications = await UnicodeSql.ExecuteScalarLongAsync(
            context.Connection,
            """
            SELECT count(*)
            FROM substrate.entity_classification ec
            JOIN substrate.entity_type et ON et.id = ec.entity_type_id
            JOIN substrate.provenance p ON p.id = ec.provenance_id
            WHERE et.code = 'codepoint'
              AND p.code = 'unicode_consortium'
            """,
            ct);
        if (codepointClassifications < UnicodeSql.MaxCodepoints)
        {
            throw new InvalidOperationException(
                $"UCD/UCA materialization incomplete: expected at least {UnicodeSql.MaxCodepoints:N0} unicode_consortium codepoint classifications, found {codepointClassifications:N0}.");
        }

        long codepointProperties = await UnicodeSql.ExecuteScalarLongAsync(
            context.Connection,
            "SELECT count(*) FROM substrate.codepoint_property",
            ct);
        if (codepointProperties < UnicodeSql.MaxCodepoints)
        {
            throw new InvalidOperationException(
                $"UCD/UCA materialization incomplete: expected at least {UnicodeSql.MaxCodepoints:N0} codepoint_property rows, found {codepointProperties:N0}.");
        }

        long simpleCaseEdges = await UnicodeSql.ExecuteScalarLongAsync(
            context.Connection,
            """
            SELECT count(*)
            FROM substrate.edge e
            JOIN substrate.edge_type et ON et.id = e.edge_type_id
            WHERE et.code IN ('maps_to_lowercase', 'maps_to_uppercase', 'maps_to_titlecase', 'case_folds_to')
            """,
            ct);
        if (simpleCaseEdges <= 0)
        {
            throw new InvalidOperationException(
                "UCD/UCA materialization incomplete: expected Unicode case mapping edges, found none.");
        }

        long simpleCaseEdgesWithoutGeometry = await UnicodeSql.ExecuteScalarLongAsync(
            context.Connection,
            """
            SELECT count(*)
            FROM substrate.edge e
            JOIN substrate.edge_type et ON et.id = e.edge_type_id
            WHERE et.code IN ('maps_to_lowercase', 'maps_to_uppercase', 'maps_to_titlecase', 'case_folds_to')
              AND e.geom IS NULL
            """,
            ct);
        if (simpleCaseEdgesWithoutGeometry > 0)
        {
            throw new InvalidOperationException(
                $"UCD/UCA materialization incomplete: {simpleCaseEdgesWithoutGeometry:N0} Unicode case mapping edges are missing trajectory geometry.");
        }

        long significanceContexts = await UnicodeSql.ExecuteScalarLongAsync(
            context.Connection,
            "SELECT count(*) FROM substrate.significance_context",
            ct);
        if (significanceContexts <= 0)
        {
            throw new InvalidOperationException(
                "UCD/UCA materialization incomplete: significance_context has no arenas.");
        }

        long simpleCaseEdgeSignificance = await UnicodeSql.ExecuteScalarLongAsync(
            context.Connection,
            """
            SELECT count(*)
            FROM substrate.edge_significance es
            JOIN substrate.edge_type et ON et.id = es.edge_type_id
            JOIN substrate.attestation_type at ON at.id = es.attestation_type_id
            WHERE et.code IN ('maps_to_lowercase', 'maps_to_uppercase', 'maps_to_titlecase', 'case_folds_to')
              AND at.code = 'provenance_authority_corroboration'
            """,
            ct);
        long expectedSimpleCaseEdgeSignificance = simpleCaseEdges * significanceContexts;
        if (simpleCaseEdgeSignificance < expectedSimpleCaseEdgeSignificance)
        {
            throw new InvalidOperationException(
                $"UCD/UCA materialization incomplete: expected {expectedSimpleCaseEdgeSignificance:N0} Unicode case edge significance rows, found {simpleCaseEdgeSignificance:N0}.");
        }

        await context.ReportAsync(PassId, codepointClassifications, 0, ct);
    }
}
