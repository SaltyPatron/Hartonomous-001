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
/// ISO 15924 (script codes) decomposer. Parses iso15924.txt — Unicode-published
/// 4-letter script identifiers (Latn, Cyrl, Hans, Hant, Arab, etc.) + English/French
/// names + numeric IDs + Unicode version introduced.
///
/// Emits text_composition entities for each script's code, English name, French name.
/// Fires has_alternate_name edges between code↔English-name and code↔French-name pairs
/// (cross-lingual attestation: ISO 15924 publishes both English + French canonical names).
///
/// Per universal-cross-source-attestation: script_name entities accumulate cross-source
/// consensus as Unicode UCD (per-cp sc attribute), CLDR (per-locale defaultScript),
/// and corpus attestations fire events on the same shared identities.
///
/// AP-19 compliance: candidate language_name entity hashes (code + English name +
/// French name) are buffered per chunk and probed via
/// <see cref="IIngestionPipeline.GetExistingEntityHashesAsync"/> ONCE per chunk
/// before emit. Only missing entities are added to the producer batch; existing
/// entities get a handle-only reference for downstream edge FKs.
/// </summary>
public sealed partial class Iso15924Decomposer : BaseDecomposer
{
    public override string ProvenanceCode => "iso_15924";
    public override string DisplayName => "ISO 15924 Decomposer (script codes)";
    public override IReadOnlyList<Phase> Phases => [Phase.Iso639];

    private const double TrustPriorMu = 95000.0;
    private const int PreDedupeChunk = 256;

    private readonly string _sourceDir;

    public Iso15924Decomposer(DecomposerConfig config, ILogger<Iso15924Decomposer> logger)
        : base(config, logger)
    {
        _sourceDir = config.SourceDirectory;
    }

    protected override IReadOnlyList<string> GetSourcePaths()
    {
        string? p = ResolveSourcePath();
        return p is null ? System.Array.Empty<string>() : [p];
    }

    private string? ResolveSourcePath()
    {
        string[] candidates =
        [
            Path.Combine(_sourceDir, "Unicode", "iso15924", "iso15924.txt"),
            Path.Combine(_sourceDir, "iso15924", "iso15924.txt"),
            Path.Combine(_sourceDir, "iso15924.txt"),
        ];
        foreach (string c in candidates) { if (File.Exists(c)) { return c; } }
        return null;
    }

    /// <summary>
    /// In-process parsed record from one iso15924.txt row. Buffered and
    /// flushed in <see cref="PreDedupeChunk"/>-sized chunks so AP-19's bulk
    /// existence probe fires once per chunk per kind.
    /// </summary>
    private readonly record struct PendingRecord(
        string Code, Hash32 CodeHash,
        string EnglishName, Hash32 EnHash,
        string FrenchName, Hash32 FrHash);

    protected override async Task DecomposeCoreAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        string? path = ResolveSourcePath();
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

            // Step 1: precompute the chunk's candidate entity hashes locally.
            List<Hash32> candidates = new(pending.Count * 3);
            foreach (PendingRecord r in pending)
            {
                candidates.Add(r.CodeHash);
                if (r.EnglishName.Length > 0 && !r.EnHash.Equals(r.CodeHash))
                {
                    candidates.Add(r.EnHash);
                }
                if (r.FrenchName.Length > 0 && !r.FrHash.Equals(r.CodeHash) && !r.FrHash.Equals(r.EnHash))
                {
                    candidates.Add(r.FrHash);
                }
            }

            // Step 2: one bulk probe per chunk (AP-19).
            HashSet<HashKey> existing = await pipeline.GetExistingEntityHashesAsync(candidates, ct);

            // Step 3: emit only the diff. Existing entities get handle-only
            // references; missing entities get full AddEntity + AddSignificance.
            EntityHandle EmitOrReference(Hash32 hash, string typeCode, bool addSignificance)
            {
                if (existing.Contains(new HashKey(hash)))
                {
                    return new EntityHandle(hash, typeCode);
                }
                EntityHandle h = batch.AddEntity(hash, typeCode);
                if (addSignificance)
                {
                    batch.AddSignificance(h, "source_authority", TrustPriorMu);
                }
                // Track new emission for accurate accounting.
                existing.Add(new HashKey(hash));
                entityCount++;
                return h;
            }

            foreach (PendingRecord r in pending)
            {
                EntityHandle codeHandle = EmitOrReference(r.CodeHash, "language_name", addSignificance: true);

                if (r.EnglishName.Length > 0 && !r.EnHash.Equals(r.CodeHash))
                {
                    EntityHandle enHandle = EmitOrReference(r.EnHash, "language_name", addSignificance: true);
                    batch.AddEdge("has_alternate_name", ProvenanceCode,
                    [
                        new EdgeMemberSpec(codeHandle, "source", 0),
                        new EdgeMemberSpec(enHandle, "target", 1),
                    ],
                    ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                    EdgeArenaRouter.EventsFor("has_alternate_name"));
                    edgeCount++;
                }

                if (r.FrenchName.Length > 0 && !r.FrHash.Equals(r.CodeHash) && !r.FrHash.Equals(r.EnHash))
                {
                    EntityHandle frHandle = EmitOrReference(r.FrHash, "language_name", addSignificance: true);
                    batch.AddEdge("has_alternate_name", ProvenanceCode,
                    [
                        new EdgeMemberSpec(codeHandle, "source", 0),
                        new EdgeMemberSpec(frHandle, "target", 1),
                    ],
                    ReadOnlySpan<EdgeSignificanceSpec>.Empty,
                    EdgeArenaRouter.EventsFor("has_alternate_name"));
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

        foreach (string raw in File.ReadLines(path))
        {
            ct.ThrowIfCancellationRequested();
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) { continue; }
            string[] parts = line.Split(';');
            if (parts.Length < 4) { continue; }
            string code = parts[0].Trim();
            // skip parts[1] = numeric id
            string englishName = parts[2].Trim();
            string frenchName = parts[3].Trim();
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(englishName)) { continue; }

            Hash32 codeHash = Blake3.Hash32(Encoding.UTF8.GetBytes(code));
            Hash32 enHash = englishName.Length > 0 ? Blake3.Hash32(Encoding.UTF8.GetBytes(englishName)) : default;
            Hash32 frHash = frenchName.Length > 0 ? Blake3.Hash32(Encoding.UTF8.GetBytes(frenchName)) : default;
            pending.Add(new PendingRecord(code, codeHash, englishName, enHash, frenchName, frHash));

            if (pending.Count >= PreDedupeChunk)
            {
                await FlushPendingAsync();
            }
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
            CurrentPhase = "iso_15924",
            EntitiesCreated = entityCount,
            EdgesCreated = edgeCount,
        }, ct);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "ISO 15924 decomposition complete: {Entities} entities, {Edges} edges")]
        public static partial void Materialized(ILogger logger, long entities, long edges);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ISO 15924 source file iso15924.txt not found under source directory; pass skipped")]
        public static partial void SourceMissing(ILogger logger);
    }
}
