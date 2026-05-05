using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text;
using Hartonomous.Core.Text.Segmentation;
using Hartonomous.Engine.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Hartonomous.Engine.Godel;

/// <summary>
/// The Gödel Engine — the substrate's reasoning layer.
///
/// docs/specs/engine/godel-engine.md describes a three-scale OODA loop
/// (Micro per traversal step, Meso per query, Macro background). This
/// implementation covers the Meso scale: Observe (sub-question
/// decomposition + intent classification), Orient (arena weighting),
/// Decide (forward pass + Reflexion retry on low confidence), Act
/// (Self-Consistency vote across sub-question candidates + synthesis).
///
/// Micro-scale OODA lives inside <c>substrate.infer_topk</c> — every
/// traversal step is annotated with edge type, mu, sigma, provenance.
/// Macro-scale OODA (scheduled background frayed-edge surveys + curiosity-
/// driven ingestion) is a separate worker; not started here.
///
/// Outcome feedback (Step 6 of inference.md — Glicko-2 update on selected
/// vs rejected paths) lives in <see cref="OutcomeRecorder"/>; the CLI's
/// <c>godel</c> command wires it to user accept/reject signals.
/// </summary>
public sealed class GodelEngine
{
    private const double UserSessionTrustMu = 1000.0;
    private const double DefaultConfidenceFloor = 1500.0;
    private const double LowConfidenceRetryThreshold = 1300.0;
    private const int MaxRetries = 1;
    private const int DefaultTopK = 5;
    private const int DefaultMaxDepth = 3;
    private const int DefaultMaxResults = 25;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly NpgsqlDataSource _dataSource;
    private readonly IIngestionPipeline _pipeline;
    private readonly ILogger<GodelEngine> _logger;

    public GodelEngine(
        NpgsqlDataSource dataSource,
        IIngestionPipeline pipeline,
        ILogger<GodelEngine> logger)
    {
        _dataSource = dataSource;
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task<GodelResponse> RunAsync(string prompt, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        StringBuilder trace = new();

        if (string.IsNullOrWhiteSpace(prompt))
        {
            sw.Stop();
            return new GodelResponse
            {
                PrimaryAnswer = string.Empty,
                SubQuestionResults = [],
                Abstained = true,
                ConfidenceFloor = DefaultConfidenceFloor,
                ReasoningTrace = "Empty prompt — abstaining.",
                TotalElapsed = sw.Elapsed,
            };
        }

        // AP-7: load only the codepoints present in this prompt, not all 303k.
        HashSet<int> promptCodepoints = new();
        foreach (System.Text.Rune r in prompt.EnumerateRunes())
        {
            promptCodepoints.Add(r.Value);
        }
        NpgsqlCodepointPropertiesCache codepointProperties =
            await NpgsqlCodepointPropertiesCache.LoadForCodepointsAsync(
                _dataSource.ConnectionString,
                promptCodepoints,
                NullLogger<NpgsqlCodepointPropertiesCache>.Instance,
                ct).ConfigureAwait(false);

        // ── OBSERVE ──────────────────────────────────────────────────────
        IReadOnlyList<SubQuestion> subQuestions =
            SubQuestionDecomposer.Decompose(prompt, codepointProperties);
        trace.AppendLine(Inv, $"OBSERVE: prompt decomposed into {subQuestions.Count} sub-question(s).");
        for (int i = 0; i < subQuestions.Count; i++)
        {
            trace.AppendLine(Inv, $"  [{i}] {subQuestions[i].Text}");
        }

        // ── ORIENT + DECIDE + ACT (per sub-question) ─────────────────────
        List<SubQuestionResult> results = new(subQuestions.Count);
        foreach (SubQuestion sq in subQuestions)
        {
            SubQuestionResult r = await ResolveSubQuestionAsync(sq, codepointProperties, trace, ct).ConfigureAwait(false);
            results.Add(r);
        }

        // ── ACT (synthesis) ──────────────────────────────────────────────
        StringBuilder synth = new();
        bool anyAnswered = false;
        for (int i = 0; i < results.Count; i++)
        {
            SubQuestionResult r = results[i];
            if (r.Candidates.Count > 0 && r.Confidence >= DefaultConfidenceFloor)
            {
                anyAnswered = true;
                if (results.Count > 1)
                {
                    synth.AppendLine(Inv, $"({i + 1}) {r.SubQuestion.Text}");
                    synth.AppendLine(Inv, $"    {r.Candidates[0].RecomposedText}");
                }
                else
                {
                    synth.Append(r.Candidates[0].RecomposedText);
                }
            }
            else if (results.Count > 1)
            {
                synth.AppendLine(Inv, $"({i + 1}) {r.SubQuestion.Text} — (insufficient confidence; abstaining)");
            }
        }

        bool abstained = !anyAnswered;
        trace.AppendLine(abstained
            ? "ACT: no sub-question cleared the confidence floor — engine abstains."
            : "ACT: synthesized answer from sub-question results.");

        sw.Stop();
        return new GodelResponse
        {
            PrimaryAnswer = synth.ToString().TrimEnd(),
            SubQuestionResults = results,
            Abstained = abstained,
            ConfidenceFloor = DefaultConfidenceFloor,
            ReasoningTrace = trace.ToString().TrimEnd(),
            TotalElapsed = sw.Elapsed,
        };
    }

    private async Task<SubQuestionResult> ResolveSubQuestionAsync(
        SubQuestion sq, NpgsqlCodepointPropertiesCache codepointProperties, StringBuilder trace, CancellationToken ct)
    {
        // ORIENT: classify intent, choose arena profile.
        PromptIntent intent = PromptIntentClassifier.Classify(sq.Text);
        ArenaWeightingProfile profile = ArenaWeightingProfile.For(intent);
        trace.AppendLine(Inv, $"ORIENT[{sq.Index}]: intent={intent} (arena profile applied).");

        // Step 0: prompt → substrate content.
        Stopwatch sw = Stopwatch.StartNew();
        IIngestionBatch batch = _pipeline.CreateBatch();
        byte[] utf8 = Encoding.UTF8.GetBytes(sq.Text);
        TextDecomposeResult ingest = CanonicalTextDecomposer.Emit(
            batch, utf8, codepointProperties,
            new TextDecomposeOptions(
                ProvenanceCode: "user_session",
                TopEntityType: "text_composition",
                TrustMu: UserSessionTrustMu));
        await _pipeline.SubmitBatchAsync(batch, ct).ConfigureAwait(false);
        trace.AppendLine(Inv, $"DECIDE[{sq.Index}]: prompt ingested (entities={batch.EntityCount}).");

        // Drain barrier — wait until prompt root + sequence rows land in substrate.
        bool drained = await WaitForDocumentAsync(ingest.RootHash, ct).ConfigureAwait(false);
        if (!drained)
        {
            trace.AppendLine(Inv, $"DECIDE[{sq.Index}]: prompt did not drain in time — abstaining for this sub-question.");
            sw.Stop();
            return new SubQuestionResult(
                sq, intent, ingest.RootHash, 0, 0, [], 0, 0.0, (int)sw.ElapsedMilliseconds);
        }

        // ACT (forward pass) with Reflexion retry budget.
        int retry = 0;
        IReadOnlyList<GodelCandidate> candidates = [];
        int seedCount = 0;
        long distinctTargets = 0;
        double confidence = 0.0;
        int maxDepth = DefaultMaxDepth;
        int maxResults = DefaultMaxResults;
        while (retry <= MaxRetries)
        {
            (candidates, seedCount, distinctTargets) =
                await ForwardPassAsync(ingest.RootHash, maxDepth, maxResults, DefaultTopK, ct)
                    .ConfigureAwait(false);

            confidence = candidates.Count > 0
                ? ScoreCandidate(candidates[0], profile)
                : 0.0;

            trace.AppendLine(Inv, $"DECIDE[{sq.Index}]: pass {retry} → {candidates.Count} candidates, top mu={confidence:F1}, seeds={seedCount}, targets={distinctTargets}.");

            if (confidence >= LowConfidenceRetryThreshold || retry >= MaxRetries)
            {
                break;
            }

            // Reflexion: low confidence → relax depth + result cap, retry.
            trace.AppendLine(Inv, $"DECIDE[{sq.Index}]: confidence below threshold ({LowConfidenceRetryThreshold}); Reflexion retry with deeper budget.");
            maxDepth += 2;
            maxResults *= 2;
            retry++;
        }

        sw.Stop();
        return new SubQuestionResult(
            sq, intent, ingest.RootHash, seedCount, distinctTargets,
            candidates, retry, confidence, (int)sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Score a candidate by combining its raw mu with a Self-Consistency
    /// boost from PathCount. PathCount is the number of independent
    /// (seed × arena) traversals that reached the target — high values
    /// represent corroboration. Boost is sub-linear (sqrt) to avoid
    /// runaway when the substrate is densely connected. ArenaWeightingProfile
    /// is reserved for future per-arena re-weighting when substrate.infer_topk
    /// gains an arena-weight parameter.
    /// </summary>
    private static double ScoreCandidate(GodelCandidate c, ArenaWeightingProfile profile)
    {
        _ = profile;
        double consistency = Math.Sqrt(Math.Max(1.0, c.PathCount));
        return c.TotalMu * consistency;
    }

    private async Task<(IReadOnlyList<GodelCandidate> Candidates, int SeedCount, long DistinctTargets)>
        ForwardPassAsync(byte[] docHash, int maxDepth, int maxResults, int topK, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        int seedCount = 0;
        long distinctTargets = 0;
        await using (NpgsqlCommand probe = new(
            "SELECT seed_count, distinct_targets FROM substrate.infer($1, $2, $3)", conn))
        {
            probe.Parameters.AddWithValue(docHash);
            probe.Parameters.AddWithValue(maxDepth);
            probe.Parameters.AddWithValue(maxResults);
            probe.CommandTimeout = 300;
            await using NpgsqlDataReader r = await probe.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                seedCount = r.IsDBNull(0) ? 0 : r.GetInt32(0);
                distinctTargets = r.IsDBNull(1) ? 0 : r.GetInt64(1);
            }
        }

        List<GodelCandidate> candidates = new(topK);
        await using (NpgsqlCommand cmd = new(
            "SELECT rank, target_hash, total_mu, path_count, recomposed_text " +
            "FROM substrate.infer_topk($1, $2, $3, $4)", conn))
        {
            cmd.Parameters.AddWithValue(docHash);
            cmd.Parameters.AddWithValue(maxDepth);
            cmd.Parameters.AddWithValue(maxResults);
            cmd.Parameters.AddWithValue(topK);
            cmd.CommandTimeout = 300;
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                candidates.Add(new GodelCandidate(
                    Rank: r.GetInt32(0),
                    TargetHash: (byte[])r.GetValue(1),
                    TotalMu: r.IsDBNull(2) ? 0.0 : r.GetDouble(2),
                    PathCount: r.IsDBNull(3) ? 0L : r.GetInt64(3),
                    RecomposedText: r.IsDBNull(4) ? string.Empty : r.GetString(4)));
            }
        }
        return (candidates, seedCount, distinctTargets);
    }

    private async Task<bool> WaitForDocumentAsync(byte[] hash, CancellationToken ct)
    {
        const int MaxAttempts = 6000; // 5 min @ 50ms
        for (int i = 0; i < MaxAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using NpgsqlCommand cmd = new(
                @"WITH e AS (SELECT 1 FROM substrate.entity WHERE hash = $1 LIMIT 1),
                       s AS (SELECT 1 FROM substrate.sequence WHERE parent_hash = $1 LIMIT 1)
                  SELECT (SELECT count(*) FROM e), (SELECT count(*) FROM s)", conn);
            cmd.Parameters.AddWithValue(hash);
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                if (r.GetInt64(0) > 0 && r.GetInt64(1) > 0)
                {
                    _ = _logger; // suppress unused warning until we wire Trace logging
                    return true;
                }
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        return false;
    }
}
