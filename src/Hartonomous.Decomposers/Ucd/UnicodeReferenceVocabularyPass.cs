using Hartonomous.Core.Data;

namespace Hartonomous.Decomposers.Ucd;

internal sealed class UnicodeReferenceVocabularyPass : IUnicodeSeedPass
{
    public string PassId => "unicode.reference_vocabularies";

    public IReadOnlyList<string> Dependencies => ["unicode.extension_catalog"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        await UnicodeSql.ExecuteScalarLongAsync(context.Connection, SubstrateFunctionNames.PopulateGeneralCategoriesFromExt, ct);
        await UnicodeSql.ExecuteScalarLongAsync(context.Connection, SubstrateFunctionNames.PopulateScriptsFromExt, ct);
        await UnicodeSql.ExecuteScalarLongAsync(context.Connection, SubstrateFunctionNames.PopulateBlocksFromExt, ct);
        await UnicodeSql.ExecuteScalarLongAsync(context.Connection, SubstrateFunctionNames.PopulateBreakPropertiesFromExt, ct);
        await context.ReportAsync(PassId, 0, 0, ct);
    }
}
