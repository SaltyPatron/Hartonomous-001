using System.Globalization;
using System.IO;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text.Ucd;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Producer pass for decomposition edges:
///   has_canonical_decomposition     (codepoint → text_composition)
///   has_compatibility_decomposition (codepoint → text_composition)
///   canonical_composes_to           (text_composition → codepoint)  -- reverse, only for canonical 2-element decomps with Comp_Ex=N
///
/// Per substrate-extracts-semantics-throws-away-source-format: parses flat XML's
/// `dt`/`dm`/`Comp_Ex` per-cp attributes; for each codepoint with a non-empty
/// decomposition, emits a text_composition entity whose Merkle identity is over
/// the ordered child codepoint hashes, then emits the typed edge from source cp
/// to that composition.
/// </summary>
internal sealed partial class UnicodeDecompositionEdgePass : IUnicodeSeedPass
{
    private const int BatchFlushSize = 25_000;

    public string PassId => "unicode.decomposition_edges";
    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        string zipPath = ResolveFlatXmlPath(context.SourceDirectory);
        long edgeCount = 0;
        long compositionEntityCount = 0;

        IIngestionBatch batch = context.Pipeline.CreateBatch(context.ProvenanceCode);
        using UcdFlatXmlReader reader = new(zipPath);
        foreach (CodepointRecord record in reader.ReadAll())
        {
            ct.ThrowIfCancellationRequested();
            if (!record.Assigned) { continue; }

            CodepointAttributes a = record.Attributes;
            string dt = a.DecompositionType;
            string dm = a.DecompositionMapping;
            if (string.IsNullOrEmpty(dm) || dm == "#" || dt == "none") { continue; }

            // Parse the decomposition mapping into child codepoints
            string[] cpTokens = dm.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (cpTokens.Length == 0) { continue; }
            Hash32[] childHashes = new Hash32[cpTokens.Length];
            for (int i = 0; i < cpTokens.Length; i++)
            {
                if (cpTokens[i] == "#") { continue; }
                int childCp = int.Parse(cpTokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                childHashes[i] = Blake3.HashCodepoint(childCp);
            }

            // text_composition Merkle identity
            Hash32 compHash = BaseDecomposer.ComputeMerkleHash(childHashes);
            EntityHandle compHandle = batch.AddEntity(compHash, "text_composition");
            compositionEntityCount++;

            // Edge from source codepoint → text_composition
            string edgeCode = (dt is "can" or "canonical") ? "has_canonical_decomposition" : "has_compatibility_decomposition";
            Hash32 srcHash = Blake3.HashCodepoint(record.Codepoint);
            EntityHandle srcHandle = new(srcHash, "codepoint");
            EdgeMemberSpec[] members =
            [
                new EdgeMemberSpec(srcHandle, "source", 0),
                new EdgeMemberSpec(compHandle, "target", 1),
            ];
            batch.AddEdge(edgeCode, context.ProvenanceCode, members);
            edgeCount++;

            // canonical_composes_to reverse edge: only for canonical 2-element
            // decomps with Comp_Ex=N (per UAX #15)
            if (edgeCode == "has_canonical_decomposition" && childHashes.Length == 2 && !a.CompositionExclusion)
            {
                EdgeMemberSpec[] composeMembers =
                [
                    new EdgeMemberSpec(compHandle, "source", 0),
                    new EdgeMemberSpec(srcHandle, "target", 1),
                ];
                batch.AddEdge("canonical_composes_to", context.ProvenanceCode, composeMembers);
                edgeCount++;
            }

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

        Log.Materialized(context.Logger, edgeCount, compositionEntityCount);
        await context.ReportAsync(PassId, compositionEntityCount, edgeCount, ct);
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
        [LoggerMessage(Level = LogLevel.Information, Message = "Unicode decomposition edges emitted: {EdgeCount}; composition entities: {EntityCount}")]
        public static partial void Materialized(ILogger logger, long edgeCount, long entityCount);
    }
}
