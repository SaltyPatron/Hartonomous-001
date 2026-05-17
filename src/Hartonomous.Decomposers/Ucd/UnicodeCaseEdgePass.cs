using System.IO;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text.Ucd;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Producer pass for simple case mapping edges (substrate content):
///   maps_to_lowercase  (codepoint → codepoint)
///   maps_to_uppercase  (codepoint → codepoint)
///   maps_to_titlecase  (codepoint → codepoint)
///   case_folds_to      (codepoint → codepoint, simple case folding)
///
/// Parses ucd.all.flat.xml directly via UcdFlatXmlReader; for each codepoint
/// whose `suc`/`slc`/`stc`/`scf` attribute resolves to a different codepoint
/// (not self-reference, not absent), emits the corresponding substrate.edge
/// row with edge_member participants in role order (source codepoint, target
/// codepoint) and lets drain-completion populate the LINESTRINGZM geometry.
///
/// Full multi-cp case mappings (lc/uc/tc/cf) are handled by
/// UnicodeFullCaseMappingEdgePass — those land has_full_case_mapping
/// (codepoint → text_composition) per the edge_type catalog.
/// </summary>
internal sealed partial class UnicodeCaseEdgePass : IUnicodeSeedPass
{
    private const int BatchFlushSize = 25_000;

    public string PassId => "unicode.case_edges";
    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        string zipPath = ResolveFlatXmlPath(context.SourceDirectory);
        long edgeCount = 0;

        IIngestionBatch batch = context.Pipeline.CreateBatch(context.ProvenanceCode);
        using UcdFlatXmlReader reader = new(zipPath);
        foreach (CodepointRecord record in reader.ReadAll())
        {
            ct.ThrowIfCancellationRequested();
            if (!record.Assigned) { continue; }

            CodepointAttributes a = record.Attributes;
            int cp = record.Codepoint;
            Hash32 srcHash = Blake3.HashCodepoint(cp);

            edgeCount += EmitIfMapped(batch, srcHash, cp, a.SimpleLowercase, "maps_to_lowercase", context.ProvenanceCode);
            edgeCount += EmitIfMapped(batch, srcHash, cp, a.SimpleUppercase, "maps_to_uppercase", context.ProvenanceCode);
            edgeCount += EmitIfMapped(batch, srcHash, cp, a.SimpleTitlecase, "maps_to_titlecase", context.ProvenanceCode);
            edgeCount += EmitIfMapped(batch, srcHash, cp, a.SimpleCaseFolding, "case_folds_to", context.ProvenanceCode);

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

        Log.Materialized(context.Logger, edgeCount);
        await context.ReportAsync(PassId, 0, edgeCount, ct);
    }

    private static long EmitIfMapped(
        IIngestionBatch batch,
        Hash32 srcHash,
        int srcCp,
        int targetCp,
        string edgeTypeCode,
        string provenanceCode)
    {
        if (targetCp == 0 || targetCp == srcCp) { return 0; }
        Hash32 tgtHash = Blake3.HashCodepoint(targetCp);
        EntityHandle srcHandle = new(srcHash, "codepoint");
        EntityHandle tgtHandle = new(tgtHash, "codepoint");
        EdgeMemberSpec[] members =
        [
            new EdgeMemberSpec(srcHandle, "source", 0),
            new EdgeMemberSpec(tgtHandle, "target", 1),
        ];
        batch.AddEdge(edgeTypeCode, provenanceCode, members);
        return 1;
    }

    private static string ResolveFlatXmlPath(string sourceDirectory)
    {
        string[] candidates =
        [
            Path.Combine(sourceDirectory, "Unicode", "Public", "17.0.0", "ucdxml", "ucd.all.flat.zip"),
            Path.Combine(sourceDirectory, "Public", "17.0.0", "ucdxml", "ucd.all.flat.zip"),
            Path.Combine(sourceDirectory, "17.0.0", "ucdxml", "ucd.all.flat.zip"),
            Path.Combine(sourceDirectory, "ucdxml", "ucd.all.flat.zip"),
        ];
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate)) { return candidate; }
        }
        throw new FileNotFoundException(
            $"ucd.all.flat.zip not found relative to source directory '{sourceDirectory}'.");
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode case edges emitted: {Count}")]
        public static partial void Materialized(ILogger logger, long count);
    }
}
