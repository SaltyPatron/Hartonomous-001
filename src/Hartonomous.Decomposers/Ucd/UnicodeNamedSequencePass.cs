namespace Hartonomous.Decomposers.Ucd;

internal sealed class UnicodeNamedSequencePass : IUnicodeSeedPass
{
    public string PassId => "unicode.named_sequences";

    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        long edgeRows = await UnicodeSql.PopulateUnicodeNamedSequencesAsync(context.Connection, ct);
        await context.ReportAsync(PassId, edgeRows, 0, ct);
    }
}
