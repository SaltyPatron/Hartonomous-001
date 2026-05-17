using System.Globalization;
using System.IO;
using System.Text;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Producer pass for CJK radical+stroke mappings:
///   has_radical_stroke  (codepoint → text_composition)
///
/// Parses CJKRadicals.txt — format: radical_number ; unified_ideograph_cp ; cjk_radical_cp.
/// Emits text_composition with content "<radical_num>.<stroke_count>" (or just radical_num
/// if no stroke info available in this file; full Unihan kRSUnicode handling lands in Step F's
/// UnihanReadingPass which parses ucd.unihan.flat.xml). For each radical entry, emit
/// has_radical_stroke edge from the unified ideograph codepoint to the radical text_composition.
/// </summary>
internal sealed partial class UnicodeRadicalStrokePass : IUnicodeSeedPass
{
    private const int BatchFlushSize = 25_000;

    public string PassId => "unicode.radical_stroke";
    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        string? path = UnicodeConfusablePass.ResolveSource(context.SourceDirectory, "ucd", "CJKRadicals.txt");
        if (path is null)
        {
            Log.SourceMissing(context.Logger, "CJKRadicals.txt");
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
            string[] parts = line.Split(';');
            if (parts.Length < 3) { continue; }
            string radicalNum = parts[0].Trim();
            string unifiedHex = parts[1].Trim();
            if (string.IsNullOrEmpty(radicalNum) || string.IsNullOrEmpty(unifiedHex)) { continue; }
            int unifiedCp = int.Parse(unifiedHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            // text_composition content = radical number string (e.g., "85" or "85'")
            byte[] radicalBytes = Encoding.UTF8.GetBytes(radicalNum);
            Hash32 compHash = BaseDecomposer.ComputeHash(radicalBytes);
            EntityHandle comp = batch.AddEntity(compHash, "text_composition");
            comps++;

            Hash32 srcHash = Blake3.HashCodepoint(unifiedCp);
            EntityHandle srcHandle = new(srcHash, "codepoint");
            EdgeMemberSpec[] members =
            [
                new EdgeMemberSpec(srcHandle, "source", 0),
                new EdgeMemberSpec(comp, "target", 1),
            ];
            batch.AddEdge("has_radical_stroke", context.ProvenanceCode, members);
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
        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode radical-stroke edges: {Edges}; composition entities: {Comps}")]
        public static partial void Materialized(ILogger logger, long edges, long comps);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Source file not found, skipping: {File}")]
        public static partial void SourceMissing(ILogger logger, string file);
    }
}
