using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Hartonomous.Core.Engine;
using Hartonomous.Core.Ingestion;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Engine.Inference;

/// <summary>
/// Substrate inference engine. The forward pass.
///
/// Per the substrate-as-AI invention, the prompt IS substrate content (not a
/// query against a model), and the forward pass IS A* traversal over
/// significance-weighted typed edges (not a matmul). Steps 0-4 of
/// docs/specs/engine/inference.md:
///
///   0. Decompose the prompt via the standard <see cref="TextDecomposer"/>.
///      Codepoint / grapheme_cluster / word_form / text_composition entities
///      land in substrate with provenance='user_session' (lowest trust prior,
///      1000 μ). The prompt's word_forms ARE the seed entities — there is
///      no separate query construction.
///   1-3. Cross-arena A* fan-out, max-pool, recompose. All inside the
///      <c>substrate.infer</c> SQL function (single round trip, no C#
///      orchestration, no Task.WhenAll, no in-memory Dictionary max-pool).
///
/// The C# layer here owns: prompt UTF-8 decode, TextDecomposer invocation,
/// pipeline submission, drain barrier (wait for prompt to land in
/// <c>substrate.entity</c>), and one Npgsql call to <c>substrate.infer</c>.
/// Everything else is substrate-side.
/// </summary>
public sealed partial class SubstrateInferenceEngine : IInferenceEngine
{
    private const double UserSessionTrustMu = 1000.0;

    private readonly NpgsqlDataSource _dataSource;
    private readonly IIngestionPipeline _pipeline;
    private readonly IReferenceDataReader _referenceData;
    private readonly ILogger<SubstrateInferenceEngine> _logger;

    public SubstrateInferenceEngine(
        NpgsqlDataSource dataSource,
        IIngestionPipeline pipeline,
        IReferenceDataReader referenceData,
        ILogger<SubstrateInferenceEngine> logger)
    {
        _dataSource = dataSource;
        _pipeline = pipeline;
        _referenceData = referenceData;
        _logger = logger;
    }

    public async Task<InferenceResult> InferAsync(InferenceQuery query, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        if (query.Text is null || query.Text.Length == 0)
        {
            return EmptyResult(sw);
        }

        // Step 0: prompt → substrate content. Same native text decomposer
        // every text-bearing seed uses; prompts are content (provenance='user_session',
        // lowest trust prior). Pipeline handles arbitrary size — a word,
        // a sentence, or a Moby Dick (1.2 MB) all flow through the same
        // channels → staging → substrate machinery. Deduplication is
        // automatic: a prompt's "the" word_form collapses to the same
        // BLAKE3-keyed entity as the WordNet seed's "the".

        IIngestionBatch batch = _pipeline.CreateBatch();
        // Native text decomposer — same path WordNet / Wiktionary / Tatoeba
        // use. Cross-decomposer dedup is automatic: the prompt's "dog" IS the
        // WordNet "dog" IS the Wiktionary "dog" by hash equality on content.
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(query.Text);
        Hartonomous.Core.Text.TextDecomposeResult ingest =
            Hartonomous.Core.Text.SubstrateTextDecomposer.EmitStatic(
                batch, utf8,
                new Hartonomous.Core.Text.TextDecomposeOptions(
                    ProvenanceCode: "user_session",
                    TopEntityType: "text_composition",
                    TrustMu: UserSessionTrustMu));
        EntityHandle docHandle = ingest.RootHandle;
        byte[] docHash = ingest.RootHash.ToByteArray();
        int promptEntityCount = batch.EntityCount;
        LogPromptIngested(_logger, query.Text.Length, promptEntityCount);

        await _pipeline.SubmitBatchAsync(batch, ct).ConfigureAwait(false);
        // Post-W2E: SubmitBatchAsync writes records into bounded channels;
        // the per-kind drain task COPY-loads its session-local temp table
        // and immediately INSERT-SELECTs into substrate within the same
        // connection. The barrier poll below is still useful because
        // SubmitBatchAsync returns once channels accept the records — actual
        // drain may complete a few ms later. For one-shot prompts this is
        // sub-second; for very large prompts it scales with chunk count.
        bool drained = await WaitForDocumentAsync(docHash, ct).ConfigureAwait(false);
        if (!drained)
        {
            throw new TimeoutException(
                "Prompt did not drain to substrate within 5 minutes. Check pipeline drain task health.");
        }

        // Steps 1-4: substrate-side forward pass. One round trip.
        SubstrateInferOutput inferOut = await CallSubstrateInferAsync(docHash, ct).ConfigureAwait(false);

        sw.Stop();

        return new InferenceResult
        {
            Answer = inferOut.AnswerText ?? string.Empty,
            Seeds = [docHandle],
            Paths = [],
            Entities = new Dictionary<EntityHandle, EntityInfo>(),
            NodesVisited = (int)Math.Min(int.MaxValue, inferOut.DistinctTargets),
            Elapsed = sw.Elapsed,
        };
    }

    /// <summary>
    /// Poll until the prompt's text_composition AND its child sequence rows
    /// have drained into substrate. Waiting only for the entity is insufficient
    /// — substrate.entity and substrate.sequence drain via independent
    /// per-kind drain tasks (post-W2E: each on its own connection with a
    /// session-local temp table), so the document can land in entity before
    /// its sequence rows land. Inference's seed activation walks
    /// substrate.sequence, so we must wait for that too.
    /// </summary>
    private async Task<bool> WaitForDocumentAsync(byte[] hash, CancellationToken ct)
    {
        const int MaxAttempts = 6000; // 5 min @ 50ms
        for (int i = 0; i < MaxAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
                conn,
                SubstrateFunctionNames.PromptDocumentReady,
                new object?[] { hash });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                long entityCount = r.GetInt64(0);
                long sequenceCount = r.GetInt64(1);
                if (entityCount > 0 && sequenceCount > 0)
                {
                    if (i > 0) { LogDrainBarrier(_logger, i * 50); }
                    return true;
                }
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<SubstrateInferOutput> CallSubstrateInferAsync(
        byte[] docHash, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        // p_max_depth=3, p_max_results=25 — tighter than substrate.infer's
        // defaults (5/50). Cross-arena A* expansion is heavy; this keeps
        // a small prompt under a few seconds.
        await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
            conn,
            SubstrateFunctionNames.Infer,
            new object?[] { docHash, 3, 25 });
        cmd.CommandTimeout = 300;
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return new SubstrateInferOutput(null, 0, 0, null, 0.0, 0);
        }
        string? answer = r.IsDBNull(0) ? null : r.GetString(0);
        int seeds = r.IsDBNull(1) ? 0 : r.GetInt32(1);
        long tgts = r.IsDBNull(2) ? 0 : r.GetInt64(2);
        byte[]? bH = r.IsDBNull(3) ? null : (byte[])r.GetValue(3);
        double bMu = r.IsDBNull(4) ? 0 : r.GetDouble(4);
        int elapsed = r.IsDBNull(5) ? 0 : r.GetInt32(5);
        LogSubstrateInfer(_logger, seeds, tgts, bH is null ? "(none)" : Convert.ToHexString(bH).Substring(0, 16), bMu, elapsed);
        return new SubstrateInferOutput(answer, seeds, tgts, bH, bMu, elapsed);
    }

    private static InferenceResult EmptyResult(Stopwatch sw)
    {
        sw.Stop();
        return new InferenceResult
        {
            Answer = string.Empty,
            Seeds = [],
            Paths = [],
            Entities = new Dictionary<EntityHandle, EntityInfo>(),
            NodesVisited = 0,
            Elapsed = sw.Elapsed,
        };
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Prompt ingested: {Chars} chars → {Entities} entities emitted to substrate")]
    private static partial void LogPromptIngested(ILogger logger, int chars, long entities);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Drain barrier crossed in {ElapsedMs}ms (prompt landed in substrate.entity)")]
    private static partial void LogDrainBarrier(ILogger logger, int elapsedMs);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "substrate.infer: {Seeds} seeds, {Targets} distinct targets, best={BestCode} mu={BestMu:F1} sql_elapsed={ElapsedMs}ms")]
    private static partial void LogSubstrateInfer(ILogger logger, int seeds, long targets, string bestCode, double bestMu, int elapsedMs);
}
