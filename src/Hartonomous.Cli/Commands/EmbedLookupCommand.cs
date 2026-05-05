using System.CommandLine;
using Hartonomous.Core.Operations;
using Hartonomous.Engine.Data;
using Hartonomous.Engine.Operations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Cli.Commands;

internal static class EmbedLookupCommand
{
    private static readonly string[] ConnAliases = ["--connection", "-c"];
    private static readonly string[] HashAliases = ["--seed-hash"];
    private static readonly string[] TypeAliases = ["--entity-type", "-t"];
    private static readonly string[] KAliases = ["--k", "-k"];
    private static readonly string[] KindAliases = ["--distance-kind"];
    private static readonly string[] ThresholdAliases = ["--threshold"];

    public static Command Build(Func<string> defaultConnectionString)
    {
        Option<string> connOpt = new(ConnAliases, defaultConnectionString, "Connection string");
        Option<string> hashOpt = new(
            HashAliases,
            description: "Hex-encoded seed entity hash (BLAKE3, 32 bytes → 64 hex chars). Lookup runs from this entity's stored physicality.")
        { IsRequired = true };
        Option<string> typeOpt = new(
            TypeAliases,
            description: "Entity type code to search within (e.g. 'lemma', 'word_form', 'tensor', 'synset').")
        { IsRequired = true };
        Option<int> kOpt = new(KAliases, () => 10, "Top-k neighbors to return.");
        Option<string> kindOpt = new(KindAliases, () => "4d", "Distance kind: '4d' (default; POINTZM fast path) | 'frechet' (Fréchet over vertex stream) | 's3' (reserved, not yet wired).");
        Option<double?> thresholdOpt = new(ThresholdAliases, () => null, "Optional max distance threshold; candidates farther than this are skipped.");

        Command cmd = new(
            "embed-lookup",
            "Top-k entities by 4D distance from a seed entity's stored physicality, filtered by entity type. "
            + "Wraps substrate.embed_lookup → pg_similarity_topk. Returns a ranked list of (entity_hash, distance).");
        cmd.AddOption(connOpt);
        cmd.AddOption(hashOpt);
        cmd.AddOption(typeOpt);
        cmd.AddOption(kOpt);
        cmd.AddOption(kindOpt);
        cmd.AddOption(thresholdOpt);

        cmd.SetHandler(async (string conn, string seedHashHex, string entityType, int k, string kind, double? threshold) =>
        {
            byte[] seedHash;
            try
            {
                seedHash = Convert.FromHexString(seedHashHex);
            }
            catch (FormatException ex)
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
            EmbeddingLookupOp op = new(
                ds,
                repo,
                new InlineSeedPromptIngestion(seedHash),
                loggerFactory.CreateLogger<BaseAiOperation>());

            Dictionary<string, string> extras = new(StringComparer.Ordinal)
            {
                ["entity_type"] = entityType,
                ["distance_kind"] = kind,
            };
            if (threshold.HasValue)
            {
                extras["distance_threshold"] = threshold.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            EmbedLookupRequest req = new()
            {
                SeedHash = seedHash,
                MaxResults = k,
                ExtraOptions = extras,
            };

            OperationResponse resp = await op.ExecuteAsync(req, CancellationToken.None);

            Console.WriteLine($"==== embed-lookup → top {resp.NodesVisited} ====");
            Console.WriteLine();
            Console.WriteLine("rank  entity_type_id  hash                                                              distance");
            int rank = 1;
            foreach (ProvenanceTrace t in resp.Trace)
            {
                string hex = Convert.ToHexString(t.EntityHash);
                Console.WriteLine($"{rank,4}  {t.EntityTypeId,14}  {hex}  {t.ContributedMu,12:F6}");
                rank++;
            }
            Console.WriteLine();
            Console.WriteLine($"sql_elapsed: {resp.ExtraDiagnostics?["sql_elapsed_ms"] ?? "-"}ms");
            Console.WriteLine($"total:       {resp.Elapsed.TotalMilliseconds:F1}ms");

            Environment.ExitCode = resp.NodesVisited > 0 ? 0 : 1;
        }, connOpt, hashOpt, typeOpt, kOpt, kindOpt, thresholdOpt);

        return cmd;
    }
}

internal sealed record EmbedLookupRequest : OperationRequest;
