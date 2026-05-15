namespace Hartonomous.Decomposers.Ucd;

internal sealed class UnicodeEmojiSequencePass : IUnicodeSeedPass
{
    public string PassId => "unicode.emoji_sequences";

    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        long basic = await UnicodeSql.PopulateUnicodeEmojiSequencesAsync(context.Connection, useZwj: false, ct);
        long zwj = await UnicodeSql.PopulateUnicodeEmojiSequencesAsync(context.Connection, useZwj: true, ct);
        await context.ReportAsync(PassId, basic + zwj, 0, ct);
    }
}
