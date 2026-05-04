using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Cli.Commands;

internal static class QuoteCommand
{
    private static readonly string[] ConnAliases = ["--connection", "-c"];
    private static readonly string[] RecipeAliases = ["--recipe", "-r"];
    private static readonly string[] ArchAliases = ["--model-arch-hash"];
    private static readonly string[] TargetSpecAliases = ["--target-spec"];
    private static readonly string[] ArenaAliases = ["--refinement-arena"];

    public static Command Build(Func<string> defaultConnectionString)
    {
        Option<string> connOpt = new(ConnAliases, defaultConnectionString, "Connection string");
        Option<string> recipeOpt = new(
            RecipeAliases,
            description: "Path to a recipe JSON (provenance_filter, arena_codes, significance_floor) or inline JSON. Drives the qualifying-edge filter for substrate.preview_target_arch.")
        { IsRequired = true };
        Option<string> targetSpecOpt = new(
            TargetSpecAliases,
            description: "Path to a target architecture spec JSON (hidden_size, num_layers, num_attention_heads, vocab_size, ffn_intermediate, moe_experts) or inline JSON. Defines the slot count to fill.")
        { IsRequired = true };
        Option<string?> archOpt = new(
            ArchAliases,
            description: "Optional hex-encoded model_architecture entity hash. If provided, the quote also includes per-tensor refinement summary (delta_mu vs source-only) for the named donor.");
        Option<string> arenaOpt = new(
            ArenaAliases,
            getDefaultValue: () => "corroboration_strength",
            description: "Arena code used by substrate.refinement_summary (only meaningful when --model-arch-hash is supplied).");

        Command cmd = new(
            "quote",
            "Preview the cost + coverage of a recompose recipe BEFORE generating a model. Wraps "
            + "substrate.preview_target_arch (qualifying-edge counts, sparsity, byte estimates per tensor role) "
            + "and substrate.refinement_summary (per-tensor consensus delta vs source-only μ). Returns the data "
            + "the Stripe-billed website will quote against — no files written, no model generated.");
        cmd.AddOption(connOpt);
        cmd.AddOption(recipeOpt);
        cmd.AddOption(targetSpecOpt);
        cmd.AddOption(archOpt);
        cmd.AddOption(arenaOpt);

        cmd.SetHandler(async (string conn, string recipeArg, string targetSpecArg, string? archHex, string arena) =>
        {
            string recipeJson, targetSpecJson;
            try
            {
                recipeJson = LoadJson(recipeArg);
                targetSpecJson = LoadJson(targetSpecArg);
            }
            catch (Exception ex) when (ex is FileNotFoundException or JsonException)
            {
                Console.Error.WriteLine($"Failed to load recipe/target-spec: {ex.Message}");
                Environment.ExitCode = 2;
                return;
            }

            byte[]? archHash = null;
            if (!string.IsNullOrEmpty(archHex))
            {
                try
                {
                    archHash = Convert.FromHexString(archHex);
                }
                catch (FormatException ex)
                {
                    Console.Error.WriteLine($"Invalid hex model-arch-hash: {ex.Message}");
                    Environment.ExitCode = 2;
                    return;
                }
            }

            await using NpgsqlDataSource ds = NpgsqlDataSource.Create(conn);

            Console.WriteLine("==== Recipe quote ====");
            Console.WriteLine();

            (long totalQualifyingEdges, long totalEstimatedBytes, int rowCount) = await PrintPreviewAsync(ds, targetSpecJson, recipeJson, CancellationToken.None);

            if (archHash is not null)
            {
                Console.WriteLine();
                Console.WriteLine($"==== Refinement summary (arena={arena}) ====");
                await PrintRefinementAsync(ds, archHash, arena, CancellationToken.None);
            }

            Console.WriteLine();
            Console.WriteLine("==== Quote totals ====");
            Console.WriteLine($"qualifying_edges_total : {totalQualifyingEdges}");
            Console.WriteLine($"estimated_bytes_total  : {totalEstimatedBytes:N0}");
            Console.WriteLine($"estimated_megabytes    : {totalEstimatedBytes / (1024.0 * 1024.0):F2}");
            Console.WriteLine();
            Console.WriteLine("Cost units (Stripe quote input):");
            long edgeCost = totalQualifyingEdges;
            long byteCost = totalEstimatedBytes / (1024 * 1024);
            long compositeUnits = edgeCost + byteCost * 100;
            Console.WriteLine($"  edge_cost          : {edgeCost}");
            Console.WriteLine($"  byte_cost (MB×100) : {byteCost * 100}");
            Console.WriteLine($"  composite_units    : {compositeUnits}");

            Environment.ExitCode = rowCount > 0 ? 0 : 1;
        }, connOpt, recipeOpt, targetSpecOpt, archOpt, arenaOpt);

        return cmd;
    }

    private static async Task<(long QualifyingEdges, long EstimatedBytes, int RowCount)> PrintPreviewAsync(
        NpgsqlDataSource ds, string targetSpecJson, string recipeJson, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(
            "SELECT tensor_role, qualifying_edges, estimated_nonzero_count, sparsity_ratio, estimated_bytes "
            + "FROM substrate.preview_target_arch($1::jsonb, $2::jsonb)",
            conn);
        cmd.Parameters.AddWithValue(targetSpecJson);
        cmd.Parameters.AddWithValue(recipeJson);

        Console.WriteLine($"{ "tensor_role",-32} { "qualifying_edges",16} { "nonzero",10} { "sparsity",10} { "bytes",16}");
        Console.WriteLine(new string('-', 90));

        long totalQualifying = 0;
        long totalBytes = 0;
        int rowCount = 0;
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string role = r.GetString(0);
            long qualifying = r.GetInt64(1);
            long nonzero = r.GetInt64(2);
            double sparsity = r.GetDouble(3);
            long bytes = r.GetInt64(4);

            Console.WriteLine($"{role,-32} {qualifying,16} {nonzero,10} {sparsity,10:F4} {bytes,16:N0}");
            totalQualifying += qualifying;
            totalBytes += bytes;
            rowCount++;
        }

        return (totalQualifying, totalBytes, rowCount);
    }

    private static async Task PrintRefinementAsync(NpgsqlDataSource ds, byte[] archHash, string arena, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(
            "SELECT tensor_hash, edge_type_code, source_only_mu, consensus_mu, delta_mu, above_threshold "
            + "FROM substrate.refinement_summary($1, $2) "
            + "ORDER BY delta_mu DESC NULLS LAST LIMIT 25",
            conn);
        cmd.Parameters.AddWithValue(archHash);
        cmd.Parameters.AddWithValue(arena);

        Console.WriteLine($"{ "tensor_hash (8)",-18} { "edge_type",-32} { "source_mu",10} { "consensus_mu",14} { "delta",10} { "above"}");
        Console.WriteLine(new string('-', 95));

        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        int rows = 0;
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            byte[] hash = (byte[])r.GetValue(0);
            string edgeType = r.GetString(1);
            double srcMu = r.IsDBNull(2) ? 0.0 : r.GetDouble(2);
            double conMu = r.IsDBNull(3) ? 0.0 : r.GetDouble(3);
            double delta = r.IsDBNull(4) ? 0.0 : r.GetDouble(4);
            bool above = !r.IsDBNull(5) && r.GetBoolean(5);
            string h = Convert.ToHexString(hash)[..8];
            Console.WriteLine($"{h,-18} {edgeType,-32} {srcMu,10:F1} {conMu,14:F1} {delta,10:F1} {above,5}");
            rows++;
        }
        if (rows == 0)
        {
            Console.WriteLine("(no refinement data — model not yet ingested or no edges in this arena)");
        }
    }

    private static string LoadJson(string arg)
    {
        if (arg.TrimStart().StartsWith('{') || arg.TrimStart().StartsWith('['))
        {
            using JsonDocument doc = JsonDocument.Parse(arg);
            return arg;
        }
        string text = File.ReadAllText(arg);
        using JsonDocument _ = JsonDocument.Parse(text);
        return text;
    }
}
