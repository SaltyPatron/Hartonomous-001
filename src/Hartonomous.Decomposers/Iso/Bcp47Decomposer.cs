using System.IO;
using System.Text;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Iso;

/// <summary>
/// IETF BCP 47 (RFC 5646) language subtag registry decomposer. Parses the
/// IANA-published language-subtag-registry.txt (record-stream of language /
/// script / region / variant / extlang / grandfathered / redundant subtags).
///
/// Each subtag is a language_name entity; cross-source attestation accumulates
/// on the same entity as ISO 639/15924/3166 publishers also attest.
/// Suppress-Script + Preferred-Value + Prefix relations emit as edges between
/// language_name entities, complementing the ISO 639 / ISO 15924 cross-link surface.
/// </summary>
public sealed partial class Bcp47Decomposer : BaseDecomposer
{
    public override string ProvenanceCode => "ietf_bcp47";
    public override string DisplayName => "IETF BCP 47 Decomposer (language-subtag-registry)";
    public override IReadOnlyList<Phase> Phases => [Phase.Iso639];

    private const double TrustPriorMu = 90000.0;
    private const int BatchFlushSize = 5_000;

    private readonly string _sourceDir;

    public Bcp47Decomposer(DecomposerConfig config, ILogger<Bcp47Decomposer> logger)
        : base(config, logger)
    {
        _sourceDir = config.SourceDirectory;
    }

    protected override IReadOnlyList<string> GetSourcePaths()
    {
        string? p = ResolvePath();
        return p is null ? System.Array.Empty<string>() : [p];
    }

    private string? ResolvePath()
    {
        string[] candidates =
        [
            Path.Combine(_sourceDir, "ISO639", "iana", "language-subtag-registry.txt"),
            Path.Combine(_sourceDir, "iana", "language-subtag-registry.txt"),
            Path.Combine(_sourceDir, "language-subtag-registry.txt"),
        ];
        foreach (string c in candidates) { if (File.Exists(c)) { return c; } }
        return null;
    }

    protected override async Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        string? path = ResolvePath();
        if (path is null)
        {
            Log.SourceMissing(Logger);
            return;
        }

        long entityCount = 0;
        long edgeCount = 0;
        IIngestionBatch batch = pipeline.CreateBatch(ProvenanceCode);

        // BCP 47 registry is %%-separated records. Each record has Type, Subtag (or Tag),
        // Description (may repeat), plus optional Suppress-Script, Preferred-Value, Prefix.
        Dictionary<string, string> fields = new(System.StringComparer.OrdinalIgnoreCase);
        List<string> descriptions = new();

        foreach (string raw in File.ReadLines(path))
        {
            ct.ThrowIfCancellationRequested();
            if (raw == "%%")
            {
                await EmitRecord(batch, fields, descriptions, ct);
                fields.Clear();
                descriptions.Clear();
                if (batch.EntityCount + batch.EdgeCount >= BatchFlushSize)
                {
                    await pipeline.SubmitBatchAsync(batch, ct);
                    batch = pipeline.CreateBatch(ProvenanceCode);
                }
                continue;
            }
            int colon = raw.IndexOf(':');
            if (colon < 0) { continue; }
            string key = raw.Substring(0, colon).Trim();
            string val = raw.Substring(colon + 1).Trim();
            if (key.Equals("Description", System.StringComparison.OrdinalIgnoreCase))
            {
                descriptions.Add(val);
            }
            else
            {
                fields[key] = val;
            }
            entityCount = batch.EntityCount;
            edgeCount = batch.EdgeCount;
        }
        // Final record
        if (fields.Count > 0)
        {
            await EmitRecord(batch, fields, descriptions, ct);
        }

        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await pipeline.SubmitBatchAsync(batch, ct);
        }

        Log.Materialized(Logger, batch.EntityCount, batch.EdgeCount);
        await reporter.ReportAsync(new ProgressSnapshot
        {
            DecomposerCode = ProvenanceCode,
            CurrentPhase = "bcp47",
            EntitiesCreated = entityCount,
            EdgesCreated = edgeCount,
        }, ct);
    }

    private static Task EmitRecord(
        IIngestionBatch batch,
        Dictionary<string, string> fields,
        List<string> descriptions,
        CancellationToken ct)
    {
        if (!fields.TryGetValue("Subtag", out string? subtag) && !fields.TryGetValue("Tag", out subtag))
        {
            return Task.CompletedTask;
        }
        if (string.IsNullOrEmpty(subtag)) { return Task.CompletedTask; }

        // Subtag entity identity = BLAKE3 of UTF-8 subtag string
        Hash32 subtagHash = Blake3.Hash32(Encoding.UTF8.GetBytes(subtag));
        EntityHandle subtagHandle = batch.AddEntity(subtagHash, "language_name");

        // Each Description as alternate name
        foreach (string desc in descriptions)
        {
            Hash32 descHash = Blake3.Hash32(Encoding.UTF8.GetBytes(desc));
            if (descHash.Equals(subtagHash)) { continue; }
            EntityHandle descHandle = batch.AddEntity(descHash, "language_name");
            batch.AddEdge("has_alternate_name", "ietf_bcp47",
            [
                new EdgeMemberSpec(subtagHandle, "source", 0),
                new EdgeMemberSpec(descHandle, "target", 1),
            ]);
        }

        // Preferred-Value (when present) = superseded_by edge
        if (fields.TryGetValue("Preferred-Value", out string? preferred) && !string.IsNullOrEmpty(preferred))
        {
            Hash32 prefHash = Blake3.Hash32(Encoding.UTF8.GetBytes(preferred));
            EntityHandle prefHandle = batch.AddEntity(prefHash, "language_name");
            batch.AddEdge("superseded_by", "ietf_bcp47",
            [
                new EdgeMemberSpec(subtagHandle, "source", 0),
                new EdgeMemberSpec(prefHandle, "target", 1),
            ]);
        }

        // Suppress-Script (when present) = has_script edge
        if (fields.TryGetValue("Suppress-Script", out string? script) && !string.IsNullOrEmpty(script))
        {
            Hash32 scriptHash = Blake3.Hash32(Encoding.UTF8.GetBytes(script));
            EntityHandle scriptHandle = batch.AddEntity(scriptHash, "language_name");
            batch.AddEdge("has_script", "ietf_bcp47",
            [
                new EdgeMemberSpec(subtagHandle, "source", 0),
                new EdgeMemberSpec(scriptHandle, "target", 1),
            ]);
        }

        return Task.CompletedTask;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "BCP 47 decomposition complete: entities={Entities}, edges={Edges}")]
        public static partial void Materialized(ILogger logger, int entities, int edges);

        [LoggerMessage(Level = LogLevel.Warning, Message = "BCP 47 source language-subtag-registry.txt not found; pass skipped")]
        public static partial void SourceMissing(ILogger logger);
    }
}
