using System.CommandLine;
using Hartonomous.Core.Operations;
using Hartonomous.Engine.Data;
using Hartonomous.Engine.Operations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Cli.Commands;

internal static class ClassifyCommand
{
    private static readonly string[] ConnAliases = ["--connection", "-c"];
    private static readonly string[] HashAliases = ["--seed-hash"];
    private static readonly string[] KindAliases = ["--kind"];
    private static readonly string[] KAliases = ["--k", "-k"];

    public static Command Build(Func<string> defaultConnectionString)
    {
        Option<string> connOpt = new(ConnAliases, defaultConnectionString, "Connection string");
        Option<string> hashOpt = new(
            HashAliases,
            description: "Hex-encoded seed entity hash (BLAKE3, 32 bytes → 64 hex chars).")
        { IsRequired = true };
        Option<string> kindOpt = new(
            KindAliases,
            getDefaultValue: () => "pos",
            description: "Junction kind: pos | sense | pattern_deprel (Glicko-2-ranked) | language | morph_feature | classification (alphabetical).");
        Option<int> kOpt = new(KAliases, () => 10, "Top-k labels.");

        Command cmd = new(
            "classify",
            "Top-k labels for an entity from a junction table, ranked by Glicko-2 mu where applicable. "
            + "Wraps substrate.classify. Junction kinds: pos | sense | pattern_deprel | language | morph_feature | classification.");
        cmd.AddOption(connOpt);
        cmd.AddOption(hashOpt);
        cmd.AddOption(kindOpt);
        cmd.AddOption(kOpt);

        cmd.SetHandler(async (string conn, string seedHashHex, string kind, int k) =>
        {
            byte[] seedHash;
            try
            {
                seedHash = Convert.FromHexString(seedHashHex);
            }
            catch (FormatException ex) // BOUNDARY: CLI argument validation maps invalid hex to exit code 2.
            {
                Console.Error.WriteLine($"Invalid hex seed hash: {ex.Message}");
                Environment.ExitCode = 2;
                return;
            }

            await using NpgsqlDataSource ds = NpgsqlDataSource.Create(conn);
            using ILoggerFactory loggerFactory = LoggerFactory.Create(b =>
            {
                b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
                b.SetMinimumLevel(LogLevel.Information);
            });
            SubstrateOpsRepository repo = new(ds, loggerFactory.CreateLogger<SubstrateOpsRepository>());
            ClassificationOp op = new(
                ds,
                repo,
                new InlineSeedPromptIngestion(seedHash),
                loggerFactory.CreateLogger<BaseAiOperation>());

            ClassifyRequest req = new()
            {
                SeedHash = seedHash,
                MaxResults = k,
                ExtraOptions = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["junction_kind"] = kind,
                },
            };

            OperationResponse resp = await op.ExecuteAsync(req, CancellationToken.None);

            Console.WriteLine($"==== classify ({kind}) → top {resp.NodesVisited} ====");
            Console.WriteLine();
            Console.WriteLine("rank  label_id  label_code            mu            sigma");
            int rank = 1;
            foreach (ProvenanceTrace t in resp.Trace)
            {
                string mu = t.ContributedMu?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "-";
                Console.WriteLine($"{rank,4}  {t.EntityTypeId,8}  {t.ProvenanceCode,-20}  {mu,12}");
                rank++;
            }
            Console.WriteLine();
            Console.WriteLine($"sql_elapsed: {resp.ExtraDiagnostics?["sql_elapsed_ms"] ?? "-"}ms");
            Console.WriteLine($"total:       {resp.Elapsed.TotalMilliseconds:F1}ms");

            Environment.ExitCode = resp.NodesVisited > 0 ? 0 : 1;
        }, connOpt, hashOpt, kindOpt, kOpt);

        return cmd;
    }
}

internal sealed record ClassifyRequest : OperationRequest;
