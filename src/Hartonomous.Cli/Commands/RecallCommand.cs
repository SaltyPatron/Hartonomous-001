using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text;
using Hartonomous.Engine.Data;
using Hartonomous.Engine.Ingestion;
using Hartonomous.Engine.Query;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Cli.Commands;

/// <summary>
/// Direct substrate recall: prompt → best substrate-grounded answer in one shot.
/// Calls <c>substrate.recall</c> (AP-2 move from inline SQL to repository method).
/// </summary>
internal sealed class RecallCommand(NpgsqlDataSource dataSource, ILoggerFactory loggerFactory)
{
    public Command Build()
    {
        Argument<string[]> textArg = new("text",
            "Prompt for direct recall. The brain's primary operation: substrate decomposition → "
            + "seed activation → cross-arena A* → recompose best target. No goal decomposition, "
            + "no Reflexion retry, no synthesis. Most prompts resolve here.");
        textArg.Arity = ArgumentArity.OneOrMore;

        Command recall = new("recall",
            "Direct substrate recall: prompt → best substrate-grounded answer in one shot. "
            + "The brain's most common access pattern. Use 'godel' instead for compound prompts "
            + "that need goal decomposition.");
        recall.AddArgument(textArg);

        recall.SetHandler(async (InvocationContext ctx) =>
        {
            string[] textParts = ctx.ParseResult.GetValueForArgument(textArg);
            string text = string.Join(' ', textParts);

            NpgsqlReferenceDataReader refReader = new(dataSource);

            await using StreamingIngestionPipeline pipeline = new(
                dataSource.ConnectionString,
                refReader,
                loggerFactory.CreateLogger<StreamingIngestionPipeline>());

            // Step 0: ingest prompt as substrate content (user_session provenance).
            IIngestionBatch batch = pipeline.CreateBatch();
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            TextDecomposeResult ingest =
                SubstrateTextDecomposer.EmitStatic(
                    batch, utf8,
                    new TextDecomposeOptions(
                        ProvenanceCode: "user_session",
                        TopEntityType: "text_composition",
                        TrustMu: 1000.0));
            await pipeline.SubmitBatchAsync(batch, CancellationToken.None);
            byte[] promptHash = ingest.RootHash.ToByteArray();

            System.Diagnostics.Stopwatch barrierSw = System.Diagnostics.Stopwatch.StartNew();
            await pipeline.FlushAsync(CancellationToken.None);
            barrierSw.Stop();

            Console.WriteLine("=== substrate.recall ===");
            Console.WriteLine($"  prompt: {text}");
            Console.WriteLine($"  hash:   {Convert.ToHexString(promptHash)[..16]}…");
            Console.WriteLine($"  drain:  {barrierSw.ElapsedMilliseconds} ms");
            Console.WriteLine();

            NpgsqlSubstrateQuery query = new(dataSource);
            (string? Answer, byte[]? TargetHash, double Confidence, int SeedCount, long TargetCount, int ElapsedMs)? r =
                await query.SubstrateRecallAsync(promptHash, maxSeeds: 3, maxTargets: 25, minConfidence: 0.25, CancellationToken.None);

            Console.WriteLine("=== Answer ===");
            if (r is null || string.IsNullOrEmpty(r.Value.Answer))
            {
                Console.WriteLine("(honest abstention — no substrate path)");
            }
            else
            {
                Console.WriteLine(r.Value.Answer);
            }
            Console.WriteLine();
            Console.WriteLine("=== Trace ===");
            Console.WriteLine($"  seeds activated:  {r?.SeedCount ?? 0}");
            Console.WriteLine($"  targets reached:  {r?.TargetCount ?? 0}");
            Console.WriteLine($"  best target:      {(r?.TargetHash is null ? "(none)" : Convert.ToHexString(r.Value.TargetHash!)[..16] + "…")}");
            Console.WriteLine($"  confidence (mu):  {r?.Confidence ?? 0:F1}");
            Console.WriteLine($"  forward-pass:     {r?.ElapsedMs ?? 0} ms");
        });

        return recall;
    }
}
