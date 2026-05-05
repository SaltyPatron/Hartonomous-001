using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Engine.Data;
using Hartonomous.Engine.Godel;
using Hartonomous.Engine.Ingestion;
using Hartonomous.Engine.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Cli.Commands;

/// <summary>
/// Runs a Gödel Engine inference: Observe → Orient → Decide → Act OODA loop
/// with sub-question decomposition, Reflexion retry, and Self-Consistency voting.
/// </summary>
internal sealed class GodelCommand(NpgsqlDataSource dataSource, ILoggerFactory loggerFactory)
{
    public Command Build()
    {
        Argument<string[]> textArg = new("text",
            "Prompt. Decomposed into sub-questions, each is its own forward pass; "
            + "the engine synthesizes a final answer with confidence and a reasoning trace.");
        textArg.Arity = ArgumentArity.OneOrMore;

        Option<string?> outcomeOpt = new("--outcome",
            "Optional: 'accept' or 'reject' the primary answer once produced. "
            + "Triggers Glicko-2 comparison events on the substrate edges that "
            + "supported each candidate (Step 6 of inference.md).");
        outcomeOpt.SetDefaultValue(null);

        Command godel = new("godel",
            "Run a Gödel Engine inference. Three-phase OODA over the substrate: "
            + "Observe (sub-question decomposition + intent classification), "
            + "Orient (arena weighting), "
            + "Decide+Act (cross-arena top-K traversal + Reflexion retry on low confidence "
            + "+ Self-Consistency voting + multi-clause synthesis).");
        godel.AddOption(outcomeOpt);
        godel.AddArgument(textArg);

        godel.SetHandler(async (InvocationContext ctx) =>
        {
            string[] textParts = ctx.ParseResult.GetValueForArgument(textArg);
            string text = string.Join(' ', textParts);
            string? outcome = ctx.ParseResult.GetValueForOption(outcomeOpt);

            NpgsqlReferenceDataReader refReader = new(dataSource);

            // Subset codepoint cache to prompt — per AP-7, never full-load.
            HashSet<int> promptCodepoints = new();
            foreach (System.Text.Rune rune in text.EnumerateRunes())
            {
                promptCodepoints.Add(rune.Value);
            }
            NpgsqlCodepointPropertiesCache codepointCache =
                await NpgsqlCodepointPropertiesCache.LoadForCodepointsAsync(
                    dataSource.ConnectionString,
                    promptCodepoints,
                    loggerFactory.CreateLogger<NpgsqlCodepointPropertiesCache>(),
                    CancellationToken.None);

            await using StreamingIngestionPipeline pipeline = new(
                dataSource.ConnectionString,
                refReader,
                loggerFactory.CreateLogger<StreamingIngestionPipeline>());

            GodelEngine engine = new(
                dataSource, pipeline,
                loggerFactory.CreateLogger<GodelEngine>());

            Console.WriteLine("=== Gödel Engine ===");
            Console.WriteLine($"  prompt: {text}");
            Console.WriteLine();

            GodelResponse response = await engine.RunAsync(text, CancellationToken.None);

            Console.WriteLine("=== Answer ===");
            Console.WriteLine(string.IsNullOrWhiteSpace(response.PrimaryAnswer)
                ? (response.Abstained
                    ? "(honest abstention — no candidate cleared the confidence floor)"
                    : "(empty)")
                : response.PrimaryAnswer);
            Console.WriteLine();

            Console.WriteLine("=== Reasoning trace ===");
            Console.WriteLine(response.ReasoningTrace);
            Console.WriteLine();

            Console.WriteLine("=== Sub-question candidates ===");
            for (int i = 0; i < response.SubQuestionResults.Count; i++)
            {
                SubQuestionResult sq = response.SubQuestionResults[i];
                Console.WriteLine($"  [{i}] '{sq.SubQuestion.Text}' intent={sq.Intent} seeds={sq.SeedCount} targets={sq.DistinctTargets} retries={sq.RetryCount} confidence={sq.Confidence:F1} ({sq.ElapsedMs} ms)");
                for (int k = 0; k < sq.Candidates.Count; k++)
                {
                    GodelCandidate c = sq.Candidates[k];
                    string preview = c.RecomposedText.Length <= 200
                        ? c.RecomposedText
                        : c.RecomposedText[..200] + "…";
                    Console.WriteLine($"      rank {c.Rank}: mu={c.TotalMu:F1} paths={c.PathCount} → {preview}");
                }
            }
            Console.WriteLine();
            Console.WriteLine($"=== Total elapsed: {response.TotalElapsed.TotalMilliseconds:F1} ms ===");

            // Optional outcome feedback (Step 6 of inference.md).
            if (outcome is "accept" or "reject")
            {
                OutcomeRecorder recorder = new(
                    dataSource,
                    loggerFactory.CreateLogger<OutcomeRecorder>());
                if (outcome == "accept")
                {
                    await recorder.RecordAcceptAsync(response, CancellationToken.None);
                    Console.WriteLine("Outcome accepted — Glicko-2 updates emitted.");
                }
                else
                {
                    await recorder.RecordRejectAsync(response, CancellationToken.None);
                    Console.WriteLine("Outcome rejected — Glicko-2 updates emitted (inverted).");
                }
            }

            await pipeline.FlushAsync(CancellationToken.None);
        });

        return godel;
    }
}
