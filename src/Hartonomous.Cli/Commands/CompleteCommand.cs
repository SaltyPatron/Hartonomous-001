using System.CommandLine;
using System.Globalization;
using System.Text;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Operations;
using Hartonomous.Core.Text;
using Hartonomous.Engine.Data;
using Hartonomous.Engine.Ingestion;
using Hartonomous.Engine.Operations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Cli.Commands;

internal static class CompleteCommand
{
    private static readonly string[] ConnAliases = ["--connection", "-c"];
    private static readonly string[] LangAliases = ["--lang"];
    private static readonly string[] DepthAliases = ["--max-depth"];
    private static readonly string[] ResultsAliases = ["--max-results"];

    public static Command Build(Func<string> defaultConnectionString)
    {
        Option<string> connOpt = new(ConnAliases, defaultConnectionString, "Connection string");
        Argument<string[]> promptArg = new("prompt", "Code prefix to complete. Decomposed via the canonical text decomposer; the resulting text_composition is the seed for substrate.complete.");
        promptArg.Arity = ArgumentArity.OneOrMore;
        Option<string?> langOpt = new(LangAliases, () => null, "Programming language code (e.g. python, rust, csharp). Filters seeds via entity_language.");
        Option<int> depthOpt = new(DepthAliases, () => 4, "Maximum recompose_text depth.");
        Option<int> resultsOpt = new(ResultsAliases, () => 25, "Maximum candidate continuations to evaluate.");

        Command cmd = new(
            "complete",
            "Code-completion AI op. Decomposes the prefix via the canonical text decomposer, "
            + "calls substrate.complete (constrained to the code_completion arena, fallback "
            + "semantic_relevance), and recomposes the best continuation via substrate.recompose_text.");
        cmd.AddArgument(promptArg);
        cmd.AddOption(connOpt);
        cmd.AddOption(langOpt);
        cmd.AddOption(depthOpt);
        cmd.AddOption(resultsOpt);

        cmd.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            string conn = ctx.ParseResult.GetValueForOption(connOpt)!;
            string[] promptParts = ctx.ParseResult.GetValueForArgument(promptArg);
            string prompt = string.Join(' ', promptParts);
            string? lang = ctx.ParseResult.GetValueForOption(langOpt);
            int depth = ctx.ParseResult.GetValueForOption(depthOpt);
            int results = ctx.ParseResult.GetValueForOption(resultsOpt);

            await using NpgsqlDataSource ds = NpgsqlDataSource.Create(conn);
            NpgsqlReferenceDataReader refReader = new(ds);

            using ILoggerFactory lf = LoggerFactory.Create(b =>
            {
                b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
                b.SetMinimumLevel(LogLevel.Information);
            });

            await using StreamingIngestionPipeline pipeline = new(
                conn, refReader,
                lf.CreateLogger<StreamingIngestionPipeline>());

            // Step 0: ingest prompt as substrate content via the canonical text
            // decomposer. The root text_composition becomes the seed hash for
            // substrate.complete; its child_hash rows in substrate.sequence are
            // the word_form / bpe_token candidates the SQL function activates.
            IIngestionBatch batch = pipeline.CreateBatch();
            byte[] utf8 = Encoding.UTF8.GetBytes(prompt);
            TextDecomposeResult ingest = SubstrateTextDecomposer.EmitStatic(
                batch, utf8,
                new TextDecomposeOptions(
                    ProvenanceCode: "user_session",
                    TopEntityType: "text_composition",
                    TrustMu: 1000.0));
            await pipeline.SubmitBatchAsync(batch, CancellationToken.None);
            byte[] seedHash = ingest.RootHash.ToByteArray();

            await pipeline.FlushAsync(CancellationToken.None);

            SubstrateOpsRepository repo = new(ds, lf.CreateLogger<SubstrateOpsRepository>());
            CodeCompletionOp op = new(
                ds,
                repo,
                new InlineSeedPromptIngestion(seedHash),
                lf.CreateLogger<BaseAiOperation>());

            CompleteRequest req = new()
            {
                SeedHash = seedHash,
                MaxDepth = depth,
                MaxResults = results,
                ExtraOptions = lang is null
                    ? null
                    : new Dictionary<string, string>(StringComparer.Ordinal) { ["lang"] = lang },
            };

            OperationResponse resp = await op.ExecuteAsync(req, CancellationToken.None);

            Console.WriteLine($"==== complete (lang={lang ?? "any"}) ====");
            Console.WriteLine($"  prompt: {prompt}");
            Console.WriteLine($"  seed:   {Convert.ToHexString(seedHash)[..16]}…");
            Console.WriteLine();
            Console.WriteLine(string.IsNullOrEmpty(resp.AnswerText)
                ? "(honest abstention — no substrate path above threshold)"
                : resp.AnswerText);
            Console.WriteLine();
            Console.WriteLine($"sql_elapsed: {resp.ExtraDiagnostics?["sql_elapsed_ms"] ?? "-"}ms");
            Console.WriteLine($"seed_count:  {resp.ExtraDiagnostics?["seed_count"] ?? "-"}");
            Console.WriteLine($"total:       {resp.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)}ms");

            Environment.ExitCode = string.IsNullOrEmpty(resp.AnswerText) ? 1 : 0;
        });

        return cmd;
    }

}
