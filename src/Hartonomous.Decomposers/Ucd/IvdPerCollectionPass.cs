using System.Globalization;
using System.IO;
using System.Text;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Producer pass for IVD per-collection ideographic variants:
///   has_ideographic_variant_in_collection  (codepoint → text_composition)
///
/// IVD = Ideographic Variation Database (UTS #37). 5 collections per spec:
/// adobe-japan1, hanyo-denshi, krname, moji_joho, msarg. Each collection
/// fires under its own provenance (ivd_adobe_japan1, etc.) so cross-collection
/// consensus accumulates on the same edge identities for variants that
/// multiple collections attest.
///
/// Source: /vault/Data/Unicode/ivd/&lt;collection&gt;/ — per-collection
/// identifier files. Format varies by collection; this pass parses the
/// IVD_Sequences.txt-style format: base_cp ; vs_cp ; collection_id ; sequence_name.
/// The text_composition target carries "{collection_id}:{sequence_name}" as
/// content so the variant identity is collection-qualified.
/// </summary>
internal sealed partial class IvdPerCollectionPass : IUnicodeSeedPass
{
    private const int BatchFlushSize = 25_000;

    public string PassId => "unicode.ivd_per_collection";
    public IReadOnlyList<string> Dependencies => ["unicode.codepoint_atoms"];

    private static readonly (string Dir, string Provenance)[] Collections =
    [
        ("adobe-japan1", "ivd_adobe_japan1"),
        ("hanyo-denshi", "ivd_hanyo_denshi"),
        ("krname",       "ivd_krname"),
        ("moji_joho",    "ivd_moji_joho"),
        ("msarg",        "ivd_msarg"),
    ];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        // The canonical IVD_Sequences.txt lives at the top of the ivd/ tree
        // (e.g., /vault/Data/Unicode/ivd/IVD_Sequences.txt) and references
        // all collections in one file. Per-collection subdirs carry glyph
        // images + metadata but the sequence registry is unified.
        string? sequencesPath = UnicodeConfusablePass.ResolveSource(context.SourceDirectory, "ivd", "IVD_Sequences.txt")
            ?? UnicodeConfusablePass.ResolveSource(context.SourceDirectory, "Unicode", "ivd", "IVD_Sequences.txt");
        if (sequencesPath is null)
        {
            Log.SourceMissing(context.Logger, "ivd/IVD_Sequences.txt");
            return;
        }

        long edges = 0;
        long comps = 0;
        IIngestionBatch batch = context.Pipeline.CreateBatch(context.ProvenanceCode);

        // IVD_Sequences.txt format (UTS #37):
        //   <base_cp> <vs_cp> ; <collection> ; <sequence_id>
        // Example: 3402 E0100; Adobe-Japan1; 13698
        foreach (string raw in File.ReadLines(sequencesPath))
        {
            ct.ThrowIfCancellationRequested();
            string line = UnicodeConfusablePass.StripComment(raw);
            if (string.IsNullOrWhiteSpace(line)) { continue; }
            string[] parts = line.Split(';');
            if (parts.Length < 3) { continue; }
            int[] cps = UnicodeConfusablePass.ParseCpSeq(parts[0]);
            if (cps.Length != 2) { continue; }
            string collection = parts[1].Trim();
            string seqId = parts[2].Trim();

            // Resolve collection → provenance
            string provenance = MapCollectionProvenance(collection) ?? context.ProvenanceCode;

            // Variant identity content: "{collection}:{seq_id}" as text_composition
            string variantLabel = $"{collection}:{seqId}";
            Hash32 variantHash = BaseDecomposer.ComputeHash(Encoding.UTF8.GetBytes(variantLabel));
            EntityHandle variantHandle = batch.AddEntity(variantHash, "text_composition");
            comps++;

            Hash32 baseHash = Blake3.HashCodepoint(cps[0]);
            EntityHandle baseHandle = new(baseHash, "codepoint");

            EdgeMemberSpec[] members =
            [
                new EdgeMemberSpec(baseHandle, "source", 0),
                new EdgeMemberSpec(variantHandle, "target", 1),
            ];
            batch.AddEdge("has_ideographic_variant_in_collection", provenance, members);
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

    private static string? MapCollectionProvenance(string collection)
    {
        string normalized = collection.Trim().ToLowerInvariant().Replace("-", "_", System.StringComparison.Ordinal);
        foreach ((string dir, string provenance) in Collections)
        {
            if (dir.Replace("-", "_", System.StringComparison.Ordinal) == normalized) { return provenance; }
        }
        return null;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "IVD per-collection edges: {Edges}; variant composition entities: {Comps}")]
        public static partial void Materialized(ILogger logger, long edges, long comps);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Source file not found, skipping: {File}")]
        public static partial void SourceMissing(ILogger logger, string file);
    }
}
