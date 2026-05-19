using System.Collections.Generic;
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
///
/// AP-19 compliance: parsed records are buffered into chunks; per-chunk all
/// candidate language_name entity hashes (subtag + descriptions + preferred
/// value + suppress script) are probed via
/// <see cref="IIngestionPipeline.GetExistingEntityHashesAsync"/> ONCE before
/// emit. Existing entities get handle-only references; only the diff lands
/// in the producer batch.
/// </summary>
public sealed partial class Bcp47Decomposer : BaseDecomposer
{
    public override string ProvenanceCode => "ietf_bcp47";
    public override string DisplayName => "IETF BCP 47 Decomposer (language-subtag-registry)";
    public override IReadOnlyList<Phase> Phases => [Phase.Iso639];

    private const double TrustPriorMu = 90000.0;
    private const int PreDedupeChunk = 128;

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

    /// <summary>
    /// A parsed BCP 47 record buffered for the AP-19 pre-dedupe chunk.
    /// Holds the subtag identity + content payloads we plan to emit.
    /// </summary>
    private sealed class PendingRecord
    {
        public string Subtag = string.Empty;
        public Hash32 SubtagHash;
        public List<(string Text, Hash32 Hash)> Descriptions = new();
        public string? Preferred;
        public Hash32 PreferredHash;
        public string? SuppressScript;
        public Hash32 SuppressScriptHash;
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
        List<PendingRecord> pending = new(PreDedupeChunk);

        async Task FlushPendingAsync()
        {
            if (pending.Count == 0) { return; }

            // Step 1: precompute the chunk's candidate entity hashes.
            HashSet<HashKey> candidateSet = new(pending.Count * 2);
            List<Hash32> candidates = new(pending.Count * 2);
            void Push(Hash32 h)
            {
                if (candidateSet.Add(new HashKey(h))) { candidates.Add(h); }
            }
            foreach (PendingRecord r in pending)
            {
                Push(r.SubtagHash);
                foreach ((_, Hash32 dh) in r.Descriptions)
                {
                    if (!dh.Equals(r.SubtagHash)) { Push(dh); }
                }
                if (r.Preferred is not null) { Push(r.PreferredHash); }
                if (r.SuppressScript is not null) { Push(r.SuppressScriptHash); }
            }

            // Step 2: one bulk probe per chunk (AP-19).
            HashSet<HashKey> existing = await pipeline.GetExistingEntityHashesAsync(candidates, ct);

            EntityHandle EmitOrReference(Hash32 hash)
            {
                if (existing.Contains(new HashKey(hash)))
                {
                    return new EntityHandle(hash, "language_name");
                }
                EntityHandle h = batch.AddEntity(hash, "language_name");
                existing.Add(new HashKey(hash));
                entityCount++;
                return h;
            }

            foreach (PendingRecord r in pending)
            {
                EntityHandle subtagHandle = EmitOrReference(r.SubtagHash);

                foreach ((_, Hash32 dh) in r.Descriptions)
                {
                    if (dh.Equals(r.SubtagHash)) { continue; }
                    EntityHandle descHandle = EmitOrReference(dh);
                    batch.AddEdge("has_alternate_name", ProvenanceCode,
                    [
                        new EdgeMemberSpec(subtagHandle, "source", 0),
                        new EdgeMemberSpec(descHandle, "target", 1),
                    ],
                    ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                    EdgeArenaRouter.EventsFor("has_alternate_name"));
                    edgeCount++;
                }

                if (r.Preferred is not null)
                {
                    EntityHandle prefHandle = EmitOrReference(r.PreferredHash);
                    batch.AddEdge("superseded_by", ProvenanceCode,
                    [
                        new EdgeMemberSpec(subtagHandle, "source", 0),
                        new EdgeMemberSpec(prefHandle, "target", 1),
                    ],
                    ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                    EdgeArenaRouter.EventsFor("superseded_by"));
                    edgeCount++;
                }

                if (r.SuppressScript is not null)
                {
                    EntityHandle scriptHandle = EmitOrReference(r.SuppressScriptHash);
                    batch.AddEdge("has_script", ProvenanceCode,
                    [
                        new EdgeMemberSpec(subtagHandle, "source", 0),
                        new EdgeMemberSpec(scriptHandle, "target", 1),
                    ],
                    ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                    EdgeArenaRouter.EventsFor("has_script"));
                    edgeCount++;
                }
            }
            pending.Clear();

            if (batch.EntityCount + batch.EdgeCount >= PreDedupeChunk * 8)
            {
                await pipeline.SubmitBatchAsync(batch, ct);
                batch = pipeline.CreateBatch(ProvenanceCode);
            }
        }

        // BCP 47 registry is %%-separated records. Each record has Type, Subtag (or Tag),
        // Description (may repeat), plus optional Suppress-Script, Preferred-Value, Prefix.
        Dictionary<string, string> fields = new(System.StringComparer.OrdinalIgnoreCase);
        List<string> descriptions = new();

        void StagePending()
        {
            if (!fields.TryGetValue("Subtag", out string? subtag) && !fields.TryGetValue("Tag", out subtag))
            {
                return;
            }
            if (string.IsNullOrEmpty(subtag)) { return; }

            PendingRecord rec = new()
            {
                Subtag = subtag,
                SubtagHash = Blake3.Hash32(Encoding.UTF8.GetBytes(subtag)),
            };

            foreach (string desc in descriptions)
            {
                Hash32 dh = Blake3.Hash32(Encoding.UTF8.GetBytes(desc));
                rec.Descriptions.Add((desc, dh));
            }

            if (fields.TryGetValue("Preferred-Value", out string? preferred) && !string.IsNullOrEmpty(preferred))
            {
                rec.Preferred = preferred;
                rec.PreferredHash = Blake3.Hash32(Encoding.UTF8.GetBytes(preferred));
            }

            if (fields.TryGetValue("Suppress-Script", out string? script) && !string.IsNullOrEmpty(script))
            {
                rec.SuppressScript = script;
                rec.SuppressScriptHash = Blake3.Hash32(Encoding.UTF8.GetBytes(script));
            }

            pending.Add(rec);
        }

        foreach (string raw in File.ReadLines(path))
        {
            ct.ThrowIfCancellationRequested();
            if (raw == "%%")
            {
                StagePending();
                fields.Clear();
                descriptions.Clear();
                if (pending.Count >= PreDedupeChunk)
                {
                    await FlushPendingAsync();
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
        }
        // Final record
        if (fields.Count > 0)
        {
            StagePending();
        }
        await FlushPendingAsync();

        if (batch.EntityCount > 0 || batch.EdgeCount > 0)
        {
            await pipeline.SubmitBatchAsync(batch, ct);
        }

        Log.Materialized(Logger, entityCount, edgeCount);
        await reporter.ReportAsync(new ProgressSnapshot
        {
            DecomposerCode = ProvenanceCode,
            CurrentPhase = "bcp47",
            EntitiesCreated = entityCount,
            EdgesCreated = edgeCount,
        }, ct);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "BCP 47 decomposition complete: entities={Entities}, edges={Edges}")]
        public static partial void Materialized(ILogger logger, long entities, long edges);

        [LoggerMessage(Level = LogLevel.Warning, Message = "BCP 47 source language-subtag-registry.txt not found; pass skipped")]
        public static partial void SourceMissing(ILogger logger);
    }
}
