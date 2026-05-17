using System.IO;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Producer pass for Standardized Variants:
///   has_standardized_variant  (codepoint → text_composition)
///
/// Parses StandardizedVariants.txt + emoji-variation-sequences.txt. Format:
/// base_cp vs_cp ; description ; scope (StandardizedVariants); or just
/// base_cp vs_cp ; description (emoji-variation-sequences). Emits text_composition
/// for the (base, vs) 2-cp sequence + has_standardized_variant edge from base cp.
/// </summary>
internal sealed partial class UnicodeStandardizedVariantPass : IUnicodeSeedPass
{
    private const int BatchFlushSize = 25_000;

    public string PassId => "unicode.standardized_variants";
    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        long edges = 0;
        long comps = 0;
        IIngestionBatch batch = context.Pipeline.CreateBatch(context.ProvenanceCode);

        string?[] paths =
        [
            UnicodeConfusablePass.ResolveSource(context.SourceDirectory, "ucd", "StandardizedVariants.txt"),
            UnicodeConfusablePass.ResolveSource(context.SourceDirectory, "ucd", "emoji", "emoji-variation-sequences.txt"),
            UnicodeConfusablePass.ResolveSource(context.SourceDirectory, "emoji", "emoji-variation-sequences.txt"),
        ];

        foreach (string? path in paths)
        {
            if (path is null) { continue; }
            foreach (string raw in File.ReadLines(path))
            {
                ct.ThrowIfCancellationRequested();
                string line = UnicodeConfusablePass.StripComment(raw);
                if (string.IsNullOrWhiteSpace(line)) { continue; }
                string[] parts = line.Split(';');
                if (parts.Length < 1) { continue; }
                int[] cps = UnicodeConfusablePass.ParseCpSeq(parts[0]);
                if (cps.Length != 2) { continue; }

                EntityHandle comp = UnicodeConfusablePass.EmitCompositionFromCps(batch, cps);
                comps++;
                Hash32 baseHash = Blake3.HashCodepoint(cps[0]);
                EntityHandle baseHandle = new(baseHash, "codepoint");
                EdgeMemberSpec[] members =
                [
                    new EdgeMemberSpec(baseHandle, "source", 0),
                    new EdgeMemberSpec(comp, "target", 1),
                ];
                batch.AddEdge("has_standardized_variant", context.ProvenanceCode, members);
                edges++;

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
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode standardized variant edges: {Edges}; composition entities: {Comps}")]
        public static partial void Materialized(ILogger logger, long edges, long comps);
    }
}
