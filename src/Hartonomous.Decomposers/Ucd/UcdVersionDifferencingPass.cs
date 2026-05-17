using System.IO;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text.Ucd;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Producer pass for per-UCD-version cross-version attestation events.
///
/// For each UCD version staged at /vault/Data/Unicode/Public/{ver}/ucdxml/ucd.all.flat.zip,
/// re-parses the flat XML and fires EdgeRatingEvent entries on the existing
/// per-cp property edges under the unicode_version_consensus arena. Each
/// version's event attribution carries the version string so cross-version
/// stability / divergence is queryable.
///
/// Most per-cp properties are stable across all 30 versions → tight Glicko
/// sigma. A handful of codepoints had property revisions across versions
/// → wider sigma. Per-version attestation gives the substrate cross-version
/// disagreement detection for free.
///
/// SCOPE NOTE: this initial implementation fires has_canonical_decomposition
/// per-version attestation events as the proof-of-pattern. Full per-property
/// per-version attestation (gc, sc, ccc, bc, lb, etc.) follows the same
/// shape — each property's edge identity is stable across versions, the
/// per-version event attests "this version agrees with this canonical
/// decomposition for this codepoint."
/// </summary>
internal sealed partial class UcdVersionDifferencingPass : IUnicodeSeedPass
{
    private const int BatchFlushSize = 25_000;

    public string PassId => "unicode.version_differencing";
    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms", "unicode.decomposition_edges"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        // Discover staged UCD versions.
        // Layout: <source>/Unicode/Public/{ver}/ucdxml/ucd.all.flat.zip
        string[] versionRoots = DiscoverVersionRoots(context.SourceDirectory);
        if (versionRoots.Length == 0)
        {
            Log.NoVersionsFound(context.Logger);
            return;
        }
        Log.VersionsFound(context.Logger, versionRoots.Length);

        long events = 0;
        IIngestionBatch batch = context.Pipeline.CreateBatch(context.ProvenanceCode);

        foreach (string versionRoot in versionRoots)
        {
            ct.ThrowIfCancellationRequested();
            string zipPath = Path.Combine(versionRoot, "ucdxml", "ucd.all.flat.zip");
            if (!File.Exists(zipPath)) { continue; }
            string version = Path.GetFileName(versionRoot);

            using UcdFlatXmlReader reader = new(zipPath);
            foreach (CodepointRecord record in reader.ReadAll())
            {
                if (!record.Assigned) { continue; }
                CodepointAttributes a = record.Attributes;
                if (string.IsNullOrEmpty(a.DecompositionMapping) || a.DecompositionMapping == "#") { continue; }

                // Reconstruct the same text_composition hash the
                // UnicodeDecompositionEdgePass computed, fire per-version
                // attestation event on the edge.
                string[] tokens = a.DecompositionMapping.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) { continue; }
                Hash32[] childHashes = new Hash32[tokens.Length];
                bool valid = true;
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (tokens[i] == "#") { valid = false; break; }
                    int cp = int.Parse(tokens[i], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                    childHashes[i] = Blake3.HashCodepoint(cp);
                }
                if (!valid) { continue; }

                Hash32 compHash = Hartonomous.Core.Decomposition.BaseDecomposer.ComputeMerkleHash(childHashes);
                Hash32 srcHash = Blake3.HashCodepoint(record.Codepoint);
                EntityHandle srcHandle = new(srcHash, "codepoint");
                EntityHandle compHandle = new(compHash, "text_composition");

                string edgeCode = (a.DecompositionType is "can" or "canonical")
                    ? "has_canonical_decomposition"
                    : "has_compatibility_decomposition";

                EdgeMemberSpec[] members =
                [
                    new EdgeMemberSpec(srcHandle, "source", 0),
                    new EdgeMemberSpec(compHandle, "target", 1),
                ];
                EdgeSignificanceSpec[] sig =
                [
                    new EdgeSignificanceSpec("unicode_version_consensus", "positive_evidence", InitialMu: 100000.0),
                ];
                EdgeRatingEvent[] eventsArr =
                [
                    new EdgeRatingEvent(
                        ContextTypeCode: "unicode_version_consensus",
                        AttestationTypeCode: "positive_evidence",
                        Score: 1.0,
                        Weight: 1.0,
                        SourceTensorName: $"ucd_{version}"),
                ];
                batch.AddEdge(edgeCode, context.ProvenanceCode, members, sig, eventsArr);
                events++;

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

        Log.Materialized(context.Logger, events, versionRoots.Length);
        await context.ReportAsync(PassId, 0, events, ct);
    }

    private static string[] DiscoverVersionRoots(string sourceDirectory)
    {
        // Try common staged layouts
        string[] publicRoots =
        [
            Path.Combine(sourceDirectory, "Unicode", "Public"),
            Path.Combine(sourceDirectory, "Public"),
        ];
        foreach (string root in publicRoots)
        {
            if (Directory.Exists(root))
            {
                List<string> versions = new();
                foreach (string dir in Directory.EnumerateDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    // Version dirs look like "17.0.0", "16.0.0", etc.
                    if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^\d+\.\d+(\.\d+)?$"))
                    {
                        if (File.Exists(Path.Combine(dir, "ucdxml", "ucd.all.flat.zip")))
                        {
                            versions.Add(dir);
                        }
                    }
                }
                if (versions.Count > 0) { return versions.ToArray(); }
            }
        }
        return System.Array.Empty<string>();
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "UCD version-attestation events: {Events} across {Versions} versions")]
        public static partial void Materialized(ILogger logger, long events, int versions);

        [LoggerMessage(Level = LogLevel.Information, Message = "Discovered {Count} staged UCD versions for version-attestation")]
        public static partial void VersionsFound(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Warning, Message = "No staged UCD versions found under Unicode/Public/; version-attestation pass skipped")]
        public static partial void NoVersionsFound(ILogger logger);
    }
}
