using System.Globalization;
using System.IO;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text.Ucd;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Producer pass for codepoint atoms.
///
/// Parses ucd.all.flat.xml directly via UcdFlatXmlReader; for each codepoint
/// emits substrate.entity(codepoint) + entity_classification + POINTZM
/// physicality (S³ centroid by UCA rank from the embedded blob's
/// hartonomous_ucd_cp_centroid mmap lookup — legitimate perf-cache use for
/// the centroid value, NOT a substrate-ingestion bypass).
///
/// Mirrors the WiktionaryDecomposer / TatoebaDecomposer producer pattern:
/// CreateBatch(provenance) → loop AddEntity → flush at BatchSize → SubmitBatchAsync.
/// Bulk pre-dedupe via GetExistingEntityHashesAsync per AP-19.
/// </summary>
internal sealed partial class CodepointAtomPass : IUnicodeSeedPass
{
    private const int BatchFlushSize = 25_000;
    private const int PreDedupeChunk = 4096;

    public string PassId => "unicode.codepoint_atoms";
    public IReadOnlyList<string> Dependencies => ["unicode.reference_vocabularies"];

    public async Task RunAsync(UnicodePassContext context, CancellationToken ct)
    {
        string zipPath = ResolveFlatXmlPath(context.SourceDirectory);
        long entityCount = 0;

        IIngestionBatch batch = context.Pipeline.CreateBatch(context.ProvenanceCode);
        List<Hash32> pendingHashes = new(PreDedupeChunk);
        List<int> pendingCodepoints = new(PreDedupeChunk);

        using UcdFlatXmlReader reader = new(zipPath);
        foreach (CodepointRecord record in reader.ReadAll())
        {
            ct.ThrowIfCancellationRequested();
            Hash32 hash = Blake3.HashCodepoint(record.Codepoint);
            pendingHashes.Add(hash);
            pendingCodepoints.Add(record.Codepoint);

            if (pendingHashes.Count >= PreDedupeChunk)
            {
                entityCount += await FlushPendingAsync(
                    context, batch, pendingHashes, pendingCodepoints, ct);

                if (batch.EntityCount >= BatchFlushSize)
                {
                    await context.Pipeline.SubmitBatchAsync(batch, ct);
                    batch = context.Pipeline.CreateBatch(context.ProvenanceCode);
                }
            }
        }

        if (pendingHashes.Count > 0)
        {
            entityCount += await FlushPendingAsync(
                context, batch, pendingHashes, pendingCodepoints, ct);
        }

        if (batch.EntityCount > 0)
        {
            await context.Pipeline.SubmitBatchAsync(batch, ct);
        }

        Log.Materialized(context.Logger, entityCount);
        await context.ReportAsync(PassId, entityCount, 0, ct);
    }

    private static async Task<long> FlushPendingAsync(
        UnicodePassContext context,
        IIngestionBatch batch,
        List<Hash32> hashes,
        List<int> codepoints,
        CancellationToken ct)
    {
        HashSet<HashKey> existing = await context.Pipeline.GetExistingEntityHashesAsync(hashes, ct);
        long emitted = 0;
        for (int i = 0; i < hashes.Count; i++)
        {
            if (existing.Contains(new HashKey(hashes[i]))) { continue; }
            int cp = codepoints[i];
            (double x, double y, double z, double m) = PhysicalityEmitter.CodepointS3Position(cp);
            double[] point4 = [x, y, z, m];
            ulong hilbert = Hilbert.Index(point4, 16);
            batch.AddEntity(hashes[i], "codepoint", x, y, z, m, (long)hilbert);
            emitted++;
        }
        hashes.Clear();
        codepoints.Clear();
        return emitted;
    }

    private static string ResolveFlatXmlPath(string sourceDirectory)
    {
        // Source directory may point at /vault/Data, /vault/Data/Unicode, or
        // /vault/Data/Unicode/Public/{ver}/. Resolve to the flat XML zip.
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
            $"ucd.all.flat.zip not found relative to source directory '{sourceDirectory}'. " +
            $"Tried: {string.Join(", ", candidates)}");
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Codepoint atoms emitted: {Count}")]
        public static partial void Materialized(ILogger logger, long count);
    }
}
