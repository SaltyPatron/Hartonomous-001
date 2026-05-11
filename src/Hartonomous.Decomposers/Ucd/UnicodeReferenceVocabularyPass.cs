namespace Hartonomous.Decomposers.Ucd;

internal sealed class UnicodeReferenceVocabularyPass : IUnicodeSeedPass
{
    public string PassId => "unicode.reference_vocabularies";

    public IReadOnlyList<string> Dependencies => ["unicode.extension_catalog"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        await UnicodeSql.ExecuteScalarLongAsync(context.Connection, "SELECT substrate.populate_general_categories_from_ext()", ct);
        await UnicodeSql.ExecuteScalarLongAsync(context.Connection, "SELECT substrate.populate_scripts_from_ext()", ct);
        await UnicodeSql.ExecuteScalarLongAsync(context.Connection, "SELECT substrate.populate_blocks_from_ext()", ct);
        await UnicodeSql.ExecuteScalarLongAsync(context.Connection, "SELECT substrate.populate_break_properties_from_ext()", ct);
        await context.ReportAsync(PassId, 0, 0, ct);
    }
}
