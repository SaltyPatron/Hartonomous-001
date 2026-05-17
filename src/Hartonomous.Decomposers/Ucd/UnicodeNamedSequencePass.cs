using System.IO;
using System.Text;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Producer pass for Unicode Named Sequences:
///   has_named_sequence  (text_composition → text_composition)
///
/// Parses NamedSequences.txt — format: name ; cp_seq. Emits a text_composition
/// entity for the named cp sequence + a text_composition for the name + the
/// has_named_sequence edge from name → sequence (source role = name content,
/// target role = sequence content).
/// </summary>
internal sealed partial class UnicodeNamedSequencePass : IUnicodeSeedPass
{
    private const int BatchFlushSize = 25_000;

    public string PassId => "unicode.named_sequences";
    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        string? path = UnicodeConfusablePass.ResolveSource(context.SourceDirectory, "ucd", "NamedSequences.txt");
        if (path is null)
        {
            Log.SourceMissing(context.Logger, "NamedSequences.txt");
            return;
        }

        long edges = 0;
        long comps = 0;
        IIngestionBatch batch = context.Pipeline.CreateBatch(context.ProvenanceCode);

        foreach (string raw in File.ReadLines(path))
        {
            ct.ThrowIfCancellationRequested();
            string line = UnicodeConfusablePass.StripComment(raw);
            if (string.IsNullOrWhiteSpace(line)) { continue; }
            int semi = line.IndexOf(';');
            if (semi <= 0) { continue; }
            string name = line.Substring(0, semi).Trim();
            int[] cps = UnicodeConfusablePass.ParseCpSeq(line.Substring(semi + 1));
            if (string.IsNullOrEmpty(name) || cps.Length == 0) { continue; }

            EntityHandle seqComp = UnicodeConfusablePass.EmitCompositionFromCps(batch, cps);
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            Hash32 nameHash = BaseDecomposer.ComputeHash(nameBytes);
            EntityHandle nameComp = batch.AddEntity(nameHash, "text_composition");
            comps += 2;

            EdgeMemberSpec[] members =
            [
                new EdgeMemberSpec(nameComp, "source", 0),
                new EdgeMemberSpec(seqComp, "target", 1),
            ];
            batch.AddEdge("has_named_sequence", context.ProvenanceCode, members);
            edges++;

            if (batch.EntityCount + batch.EdgeCount >= BatchFlushSize)
            {
                await context.Pipeline.SubmitBatchAsync(batch, ct);
                batch = context.Pipeline.CreateBatch(context.ProvenanceCode);
            }
        }

        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await context.Pipeline.SubmitBatchAsync(batch, ct);
        }

        Log.Materialized(context.Logger, edges, comps);
        await context.ReportAsync(PassId, comps, edges, ct);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode named sequence edges: {Edges}; composition entities: {Comps}")]
        public static partial void Materialized(ILogger logger, long edges, long comps);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Source file not found, skipping: {File}")]
        public static partial void SourceMissing(ILogger logger, string file);
    }
}
