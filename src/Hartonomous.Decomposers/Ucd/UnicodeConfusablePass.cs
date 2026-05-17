using System.Globalization;
using System.IO;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Producer pass for UTS #39 confusable pairs.
///   confusable_with  (text_composition → text_composition)
///
/// Parses security/confusables.txt directly. Each line: src_cp_seq ; tgt_cp_seq ; class.
/// Emits text_composition entities for both sides + the confusable_with edge.
/// </summary>
internal sealed partial class UnicodeConfusablePass : IUnicodeSeedPass
{
    private const int BatchFlushSize = 25_000;

    public string PassId => "unicode.confusables";
    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        string? path = ResolveSource(context.SourceDirectory, "security", "confusables.txt");
        if (path is null)
        {
            Log.SourceMissing(context.Logger, "confusables.txt");
            return;
        }

        long edges = 0;
        long comps = 0;
        IIngestionBatch batch = context.Pipeline.CreateBatch(context.ProvenanceCode);

        foreach (string raw in File.ReadLines(path))
        {
            ct.ThrowIfCancellationRequested();
            string line = StripComment(raw);
            if (string.IsNullOrWhiteSpace(line)) { continue; }
            string[] parts = line.Split(';');
            if (parts.Length < 2) { continue; }
            int[] src = ParseCpSeq(parts[0]);
            int[] tgt = ParseCpSeq(parts[1]);
            if (src.Length == 0 || tgt.Length == 0) { continue; }

            EntityHandle srcComp = EmitCompositionFromCps(batch, src);
            EntityHandle tgtComp = EmitCompositionFromCps(batch, tgt);
            comps += 2;

            EdgeMemberSpec[] members =
            [
                new EdgeMemberSpec(srcComp, "source", 0),
                new EdgeMemberSpec(tgtComp, "target", 1),
            ];
            batch.AddEdge("confusable_with", context.ProvenanceCode, members);
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

    internal static string StripComment(string raw)
    {
        int hash = raw.IndexOf('#');
        return (hash >= 0 ? raw.Substring(0, hash) : raw).Trim();
    }

    internal static int[] ParseCpSeq(string s)
    {
        s = s.Trim();
        if (string.IsNullOrEmpty(s)) { return System.Array.Empty<int>(); }
        string[] toks = s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        int[] cps = new int[toks.Length];
        for (int i = 0; i < toks.Length; i++)
        {
            cps[i] = int.Parse(toks[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return cps;
    }

    internal static EntityHandle EmitCompositionFromCps(IIngestionBatch batch, int[] cps)
    {
        if (cps.Length == 1)
        {
            // Single-cp content: the codepoint entity IS the content (no separate text_composition needed)
            // but the edge type expects text_composition target. Emit a wrapper text_composition over the single cp.
        }
        Hash32[] childHashes = new Hash32[cps.Length];
        for (int i = 0; i < cps.Length; i++) { childHashes[i] = Blake3.HashCodepoint(cps[i]); }
        Hash32 compHash = BaseDecomposer.ComputeMerkleHash(childHashes);
        return batch.AddEntity(compHash, "text_composition");
    }

    internal static string? ResolveSource(string sourceDirectory, params string[] subPath)
    {
        string[] candidates =
        [
            Path.Combine(new[] { sourceDirectory, "Unicode", "Public", "17.0.0" }.Concat(subPath).ToArray()),
            Path.Combine(new[] { sourceDirectory, "Public", "17.0.0" }.Concat(subPath).ToArray()),
            Path.Combine(new[] { sourceDirectory, "17.0.0" }.Concat(subPath).ToArray()),
            Path.Combine(new[] { sourceDirectory, "Unicode" }.Concat(subPath).ToArray()),
            Path.Combine(new[] { sourceDirectory }.Concat(subPath).ToArray()),
        ];
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate)) { return candidate; }
        }
        return null;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode confusable edges: {Edges}; composition entities: {Comps}")]
        public static partial void Materialized(ILogger logger, long edges, long comps);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Source file not found, skipping: {File}")]
        public static partial void SourceMissing(ILogger logger, string file);
    }
}
