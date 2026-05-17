using System.Globalization;
using System.IO;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text.Ucd;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Producer pass for full (multi-cp) case mapping edges:
///   has_full_case_mapping  (codepoint → text_composition)
///
/// Reads flat XML's `lc`/`uc`/`tc`/`cf` attributes — full case mappings that
/// may differ from the simple forms (e.g. ß → SS, İ locale-conditional rules).
/// When the full form is multi-cp or differs from the simple form, emit a
/// text_composition entity for the target sequence and a has_full_case_mapping edge.
/// </summary>
internal sealed partial class UnicodeFullCaseMappingEdgePass : IUnicodeSeedPass
{
    private const int BatchFlushSize = 25_000;

    public string PassId => "unicode.full_case_mapping_edges";
    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        string zipPath = ResolveFlatXmlPath(context.SourceDirectory);
        long edgeCount = 0;
        long compCount = 0;

        IIngestionBatch batch = context.Pipeline.CreateBatch(context.ProvenanceCode);
        using UcdFlatXmlReader reader = new(zipPath);
        foreach (CodepointRecord record in reader.ReadAll())
        {
            ct.ThrowIfCancellationRequested();
            if (!record.Assigned) { continue; }

            CodepointAttributes a = record.Attributes;
            int cp = record.Codepoint;
            Hash32 srcHash = Blake3.HashCodepoint(cp);
            EntityHandle srcHandle = new(srcHash, "codepoint");

            edgeCount += EmitFullCase(batch, srcHandle, cp, a.FullLowercase, a.SimpleLowercase, context.ProvenanceCode, ref compCount);
            edgeCount += EmitFullCase(batch, srcHandle, cp, a.FullUppercase, a.SimpleUppercase, context.ProvenanceCode, ref compCount);
            edgeCount += EmitFullCase(batch, srcHandle, cp, a.FullTitlecase, a.SimpleTitlecase, context.ProvenanceCode, ref compCount);
            edgeCount += EmitFullCase(batch, srcHandle, cp, a.FullCaseFolding, a.SimpleCaseFolding, context.ProvenanceCode, ref compCount);

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

        Log.Materialized(context.Logger, edgeCount, compCount);
        await context.ReportAsync(PassId, compCount, edgeCount, ct);
    }

    private static long EmitFullCase(
        IIngestionBatch batch,
        EntityHandle srcHandle,
        int srcCp,
        string fullAttr,
        int simpleFallback,
        string provenanceCode,
        ref long compCount)
    {
        if (string.IsNullOrEmpty(fullAttr) || fullAttr == "#") { return 0; }
        string[] tokens = fullAttr.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) { return 0; }
        if (tokens.Length == 1)
        {
            int cp = int.Parse(tokens[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (cp == simpleFallback || cp == srcCp) { return 0; }
        }

        Hash32[] childHashes = new Hash32[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            int cp = int.Parse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            childHashes[i] = Blake3.HashCodepoint(cp);
        }
        Hash32 compHash = BaseDecomposer.ComputeMerkleHash(childHashes);
        EntityHandle compHandle = batch.AddEntity(compHash, "text_composition");
        compCount++;

        EdgeMemberSpec[] members =
        [
            new EdgeMemberSpec(srcHandle, "source", 0),
            new EdgeMemberSpec(compHandle, "target", 1),
        ];
        batch.AddEdge("has_full_case_mapping", provenanceCode, members);
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
        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode full case mapping edges: {Edges}; composition entities: {Comps}")]
        public static partial void Materialized(ILogger logger, long edges, long comps);
    }
}
