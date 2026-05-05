using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Engine;
using Hartonomous.Engine.Data;
using Hartonomous.Engine.Inference;
using Hartonomous.Engine.Ingestion;
using Hartonomous.Engine.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Cli.Commands;

/// <summary>
/// Runs a forward pass through the substrate: prompt becomes substrate content,
/// substrate's significance-weighted A* traversal produces the recomposed answer.
/// </summary>
internal sealed class QueryCommand(NpgsqlDataSource dataSource, ILoggerFactory loggerFactory)
{
    public Command Build()
    {
        Argument<string[]> textArg = new("text",
            "Prompt. Decomposed into substrate entities; the substrate's A* traversal IS the forward pass.");
        textArg.Arity = ArgumentArity.OneOrMore;

        Command query = new("query",
            "Run a forward pass through the substrate. The prompt becomes substrate content and "
            + "the substrate's significance-weighted A* traversal across all arenas produces the "
            + "recomposed answer. No caller-specified arena, depth, cost-budget, or result-cap — "
            + "those would compromise the invention.");
        query.AddArgument(textArg);

        query.SetHandler(async (InvocationContext ctx) =>
        {
            string[] textParts = ctx.ParseResult.GetValueForArgument(textArg);
            string text = string.Join(' ', textParts);

            NpgsqlReferenceDataReader refReader = new(dataSource);

            // Subset codepoint cache to prompt — per AP-7: don't full-load 303k
            // codepoints for an inference path that needs ~50.
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

            SubstrateInferenceEngine engine = new(
                dataSource, pipeline, refReader,
                loggerFactory.CreateLogger<SubstrateInferenceEngine>());

            Console.WriteLine("=== Forward pass ===");
            Console.WriteLine($"  prompt: {text}");
            Console.WriteLine();

            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            InferenceResult result = await engine.InferAsync(new InferenceQuery { Text = text }, CancellationToken.None);
            sw.Stop();

            Console.WriteLine("=== Substrate output ===");
            Console.WriteLine($"  answer: {(string.IsNullOrEmpty(result.Answer) ? "(no path — honest abstention)" : result.Answer)}");
            Console.WriteLine();
            Console.WriteLine("=== Trace ===");
            Console.WriteLine($"  prompt seeds:     {result.Seeds.Count} (text_composition root, A* expands word_form children)");
            Console.WriteLine($"  distinct targets: {result.NodesVisited}");
            Console.WriteLine($"  elapsed:          {sw.Elapsed.TotalMilliseconds:F1} ms");

            await pipeline.FlushAsync(CancellationToken.None);
        });

        return query;
    }
}
