using System.Text;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Encodings;

/// <summary>
/// Base for encoding-standard decomposers. Each per-standard decomposer
/// overrides ProvenanceCode + the byte-to-codepoint table; this base class
/// handles the producer pattern: for each (byte, codepoint) mapping, emit
/// a has_encoding_position edge from the codepoint to a text_composition
/// representing the byte sequence in that encoding's space.
///
/// Substrate-side, multi-encoding consensus accumulates on shared codepoint
/// identities — e.g., U+00E9 (é) fires events from ISO 8859-1 at 0xE9,
/// ISO 8859-15 at 0xE9, Windows-1252 at 0xE9, MacRoman at 0x8E, EBCDIC-1047
/// at 0x51, etc. The encoding_position_consensus arena's leaderboard at
/// query time shows cross-standard agreement / divergence.
/// </summary>
public abstract partial class EncodingDecomposerBase : BaseDecomposer
{
    private const double TrustPriorMu = 90000.0;
    private const int BatchFlushSize = 2_000;

    protected EncodingDecomposerBase(DecomposerConfig config, ILogger logger) : base(config, logger) { }

    public override IReadOnlyList<Phase> Phases => [Phase.UcdUca];
    protected override IReadOnlyList<string> GetSourcePaths() => System.Array.Empty<string>();

    /// <summary>Encoding name (e.g., "ASCII", "ISO 8859-1").</summary>
    protected abstract string EncodingName { get; }

    /// <summary>
    /// Per-byte mapping for single-byte encodings. Index = byte value;
    /// value = codepoint, or 0 (or -1) for unmapped/undefined positions.
    /// Override for multi-byte encodings via TryGetMapping instead.
    /// </summary>
    protected abstract int[] ByteToCodepoint { get; }

    protected override async Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        int[] table = ByteToCodepoint;
        long edges = 0;
        long entities = 0;
        IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);

        for (int b = 0; b < table.Length; b++)
        {
            ct.ThrowIfCancellationRequested();
            int cp = table[b];
            if (cp <= 0) { continue; } // unmapped / undefined

            // Encoding-position content: byte value as 2-hex-char string (e.g., "41" for 0x41 in ASCII).
            // This makes the text_composition target stable + queryable across encodings.
            string byteStr = b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
            Hash32 byteCompHash = Blake3.Hash32(Encoding.UTF8.GetBytes(byteStr));
            EntityHandle byteCompHandle = batch.AddEntity(byteCompHash, "text_composition");
            entities++;

            Hash32 cpHash = Blake3.HashCodepoint(cp);
            EntityHandle cpHandle = new(cpHash, "codepoint");

            batch.AddEdge("has_encoding_position", ProvenanceCode,
            [
                new EdgeMemberSpec(cpHandle, "source", 0),
                new EdgeMemberSpec(byteCompHandle, "target", 1),
            ]);
            edges++;

            if (batch.EntityCount + batch.EdgeCount >= BatchFlushSize)
            {
                await pipeline.SubmitBatchAsync(batch, ct);
                batch = pipeline.CreateBatch(ProvenanceCode);
            }
        }

        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await pipeline.SubmitBatchAsync(batch, ct);
        }

        Log.Materialized(Logger, EncodingName, entities, edges);
        await reporter.ReportAsync(new ProgressSnapshot
        {
            DecomposerCode = ProvenanceCode,
            CurrentPhase = $"encoding:{EncodingName}",
            EntitiesCreated = entities,
            EdgesCreated = edges,
        }, ct);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Encoding {EncodingName} decomposition complete: {Entities} entities, {Edges} edges")]
        public static partial void Materialized(ILogger logger, string encodingName, long entities, long edges);
    }
}
