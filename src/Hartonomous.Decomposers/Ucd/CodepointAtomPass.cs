namespace Hartonomous.Decomposers.Ucd;

internal sealed class CodepointAtomPass : IUnicodeSeedPass
{
    public string PassId => "unicode.codepoint_atoms";

    public IReadOnlyList<string> Dependencies => ["unicode.reference_vocabularies"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        long atomRows = await UnicodeSql.PopulateCodepointAtomsAsync(context.DataSource, ct);
        await context.ReportAsync(PassId, atomRows, 0, ct);
    }
}
