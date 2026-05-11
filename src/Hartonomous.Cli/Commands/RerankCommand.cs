using System.CommandLine;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Operations;
using Hartonomous.Engine.Data;
using Hartonomous.Engine.Operations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Cli.Commands;

internal static class RerankCommand
{
    private static readonly string[] ConnAliases = ["--connection", "-c"];
    private static readonly string[] CandidatesAliases = ["--candidates"];
    private static readonly string[] CandidatesFileAliases = ["--candidates-file"];
    private static readonly string[] ArenaAliases = ["--arena", "-a"];
    private static readonly string[] KAliases = ["--k", "-k"];

    public static Command Build(Func<string> defaultConnectionString)
    {
        Option<string> connOpt = new(ConnAliases, defaultConnectionString, "Connection string");
        Option<string?> candsOpt = new(
            CandidatesAliases,
            description: "Comma-separated hex-encoded entity hashes to rerank. Either --candidates or --candidates-file is required.");
        Option<string?> candsFileOpt = new(
            CandidatesFileAliases,
            description: "Path to a text file with one hex-encoded entity hash per line. Either --candidates or --candidates-file is required.");
        Option<string> arenaOpt = new(
            ArenaAliases,
            description: "Arena code from substrate.significance_context (e.g. semantic_relevance, source_authority, code_completion).")
        { IsRequired = true };
        Option<int> kOpt = new(KAliases, () => 25, "Top-k after rerank.");

        Command cmd = new(
            "rerank",
            "Rerank a candidate entity set by Glicko-2 mu in the named arena. "
            + "Wraps substrate.rerank. Unrated candidates default to 1500 mu / 350 sigma.");
        cmd.AddOption(connOpt);
        cmd.AddOption(candsOpt);
        cmd.AddOption(candsFileOpt);
        cmd.AddOption(arenaOpt);
        cmd.AddOption(kOpt);

        cmd.SetHandler(async (string conn, string? candidatesCsv, string? candidatesFile, string arena, int k) =>
        {
            List<Hash32> candidates;
            try
            {
                candidates = LoadCandidates(candidatesCsv, candidatesFile);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or FileNotFoundException) // BOUNDARY: CLI input validation maps bad candidate input to exit code 2.
            {
                Console.Error.WriteLine($"Failed to load candidates: {ex.Message}");
                Environment.ExitCode = 2;
                return;
            }

            await using NpgsqlDataSource ds = NpgsqlDataSource.Create(conn);
            using ILoggerFactory lf = LoggerFactory.Create(b =>
            {
                b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
                b.SetMinimumLevel(LogLevel.Information);
            });
            SubstrateOpsRepository repo = new(ds, lf.CreateLogger<SubstrateOpsRepository>());
            RerankingOp op = new(ds, repo, lf.CreateLogger<BaseAiOperation>());

            RerankingRequest req = new()
            {
                Candidates = candidates,
                ArenaCode = arena,
                MaxResults = k,
            };

            OperationResponse resp = await op.ExecuteAsync(req, CancellationToken.None);

            Console.WriteLine($"==== rerank arena={arena} candidates={candidates.Count} → top {resp.NodesVisited} ====");
            Console.WriteLine();
            Console.WriteLine("rank  hash                                                              mu");
            foreach (ProvenanceTrace t in resp.Trace)
            {
                string hex = Convert.ToHexString(t.EntityHash);
                string mu = t.ContributedMu?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "-";
                Console.WriteLine($"{t.OrdinalPosition,4}  {hex}  {mu,8}");
            }
            Console.WriteLine();
            Console.WriteLine($"sql_elapsed: {resp.ExtraDiagnostics?["sql_elapsed_ms"] ?? "-"}ms");
            Console.WriteLine($"total:       {resp.Elapsed.TotalMilliseconds:F1}ms");

            Environment.ExitCode = resp.NodesVisited > 0 ? 0 : 1;
        }, connOpt, candsOpt, candsFileOpt, arenaOpt, kOpt);

        return cmd;
    }

    private static List<Hash32> LoadCandidates(string? csv, string? file)
    {
        if (!string.IsNullOrEmpty(csv))
        {
            return ParseHexList(csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        if (!string.IsNullOrEmpty(file))
        {
            string[] lines = File.ReadAllLines(file);
            return ParseHexList(lines);
        }
        throw new ArgumentException("Either --candidates or --candidates-file must be supplied.");
    }

    private static List<Hash32> ParseHexList(IEnumerable<string> hexes)
    {
        List<Hash32> result = [];
        foreach (string s in hexes)
        {
            string trimmed = s.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }
            result.Add(Hash32.FromBytes(Convert.FromHexString(trimmed)));
        }
        if (result.Count == 0)
        {
            throw new ArgumentException("No candidates parsed from input.");
        }
        return result;
    }
}
