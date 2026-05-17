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
/// </summary>
public sealed partial class Iso15924Decomposer : BaseDecomposer
{
    public override string ProvenanceCode => "iso_15924";
    public override string DisplayName => "ISO 15924 Decomposer (script codes)";
    public override IReadOnlyList<Phase> Phases => [Phase.Iso639];

    private const double TrustPriorMu = 95000.0;
    private const int BatchFlushSize = 5_000;

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

            // Code identity = BLAKE3 of the 4-letter code in UTF-8.
            Hash32 codeHash = Blake3.Hash32(Encoding.UTF8.GetBytes(code));
            EntityHandle codeHandle = batch.AddEntity(codeHash, "language_name");
            batch.AddSignificance(codeHandle, "source_authority", TrustPriorMu);
            entityCount++;

            // English name as alternate.
            Hash32 enHash = Blake3.Hash32(Encoding.UTF8.GetBytes(englishName));
            if (!enHash.Equals(codeHash))
            {
                EntityHandle enHandle = batch.AddEntity(enHash, "language_name");
                batch.AddSignificance(enHandle, "source_authority", TrustPriorMu);
                batch.AddEdge("has_alternate_name", ProvenanceCode,
                [
                    new EdgeMemberSpec(codeHandle, "source", 0),
                    new EdgeMemberSpec(enHandle, "target", 1),
                ]);
                entityCount++;
                edgeCount++;
            }

            // French name as alternate (cross-lingual corroboration).
            if (frenchName.Length > 0)
            {
                Hash32 frHash = Blake3.Hash32(Encoding.UTF8.GetBytes(frenchName));
                if (!frHash.Equals(codeHash) && !frHash.Equals(enHash))
                {
                    EntityHandle frHandle = batch.AddEntity(frHash, "language_name");
                    batch.AddSignificance(frHandle, "source_authority", TrustPriorMu);
                    batch.AddEdge("has_alternate_name", ProvenanceCode,
                    [
                        new EdgeMemberSpec(codeHandle, "source", 0),
                        new EdgeMemberSpec(frHandle, "target", 1),
                    ]);
                    entityCount++;
                    edgeCount++;
                }
            }

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
