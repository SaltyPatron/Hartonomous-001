using System.IO;
using System.Text;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Producer pass for Emoji + Emoji ZWJ sequences:
///   has_emoji_sequence       (text_composition → text_composition)
///   has_emoji_zwj_sequence   (text_composition → text_composition)
///
/// Parses emoji-sequences.txt + emoji-zwj-sequences.txt. Each line: cp_seq ; property ; name.
/// For each entry: emit a text_composition for the cp sequence + a text_composition for the name,
/// plus the typed edge from name → sequence.
/// </summary>
internal sealed partial class UnicodeEmojiSequencePass : IUnicodeSeedPass
{
    private const int BatchFlushSize = 25_000;

    public string PassId => "unicode.emoji_sequences";
    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        long edges = 0;
        long comps = 0;
        IIngestionBatch batch = context.Pipeline.CreateBatch(context.ProvenanceCode);

        (string?, string)[] sources =
        [
            (UnicodeConfusablePass.ResolveSource(context.SourceDirectory, "emoji", "emoji-sequences.txt")
                ?? UnicodeConfusablePass.ResolveSource(context.SourceDirectory, "ucd", "emoji", "emoji-sequences.txt"), "has_emoji_sequence"),
            (UnicodeConfusablePass.ResolveSource(context.SourceDirectory, "emoji", "emoji-zwj-sequences.txt")
                ?? UnicodeConfusablePass.ResolveSource(context.SourceDirectory, "ucd", "emoji", "emoji-zwj-sequences.txt"), "has_emoji_zwj_sequence"),
        ];

        foreach ((string? path, string edgeCode) in sources)
        {
            if (path is null) { continue; }
            foreach (string raw in File.ReadLines(path))
            {
                ct.ThrowIfCancellationRequested();
                string line = UnicodeConfusablePass.StripComment(raw);
                if (string.IsNullOrWhiteSpace(line)) { continue; }
                string[] parts = line.Split(';');
                if (parts.Length < 2) { continue; }
                string cpsField = parts[0].Trim();
                string name = parts.Length >= 3 ? parts[2].Trim() : "";

                // emoji-sequences.txt may have a range field (XXXX..YYYY)
                if (cpsField.Contains(".."))
                {
                    int dotdot = cpsField.IndexOf("..", System.StringComparison.Ordinal);
                    int lo = int.Parse(cpsField.AsSpan(0, dotdot), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                    int hi = int.Parse(cpsField.AsSpan(dotdot + 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                    for (int cp = lo; cp <= hi; cp++)
                    {
                        emit(batch, new[] { cp }, name, edgeCode, context.ProvenanceCode, ref edges, ref comps);
                    }
                }
                else
                {
                    int[] cps = UnicodeConfusablePass.ParseCpSeq(cpsField);
                    if (cps.Length == 0) { continue; }
                    emit(batch, cps, name, edgeCode, context.ProvenanceCode, ref edges, ref comps);
                }

                if (batch.EntityCount + batch.EdgeCount >= BatchFlushSize)
                {
                    await context.Pipeline.SubmitBatchAsync(batch, ct);
                    batch = context.Pipeline.CreateBatch(context.ProvenanceCode);
                }
            }
        }

        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await context.Pipeline.SubmitBatchAsync(batch, ct);
        }

        Log.Materialized(context.Logger, edges, comps);
        await context.ReportAsync(PassId, comps, edges, ct);

        static void emit(IIngestionBatch batch, int[] cps, string name, string edgeCode, string provenance, ref long edges, ref long comps)
        {
            EntityHandle seqComp = UnicodeConfusablePass.EmitCompositionFromCps(batch, cps);
            comps++;
            EntityHandle nameComp;
            if (!string.IsNullOrEmpty(name))
            {
                Hash32 nameHash = BaseDecomposer.ComputeHash(Encoding.UTF8.GetBytes(name));
                nameComp = batch.AddEntity(nameHash, "text_composition");
                comps++;
            }
            else
            {
                // Anonymous emoji sequence: source role = the sequence itself (self-edge skipped at PG level via PK)
                nameComp = seqComp;
            }
            EdgeMemberSpec[] members =
            [
                new EdgeMemberSpec(nameComp, "source", 0),
                new EdgeMemberSpec(seqComp, "target", 1),
            ];
            batch.AddEdge(edgeCode, provenance, members);
            edges++;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode emoji sequence edges: {Edges}; composition entities: {Comps}")]
        public static partial void Materialized(ILogger logger, long edges, long comps);
    }
}
