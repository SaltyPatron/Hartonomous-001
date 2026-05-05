using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Engine.Query;
using Npgsql;

namespace Hartonomous.Cli.Commands;

/// <summary>
/// Prints the universal substrate inventory for an ingested model via
/// <c>substrate.model_inventory</c>, <c>substrate.model_vocab_recovered</c>,
/// and <c>substrate.refinement_summary</c>.
/// </summary>
internal sealed class QuerySubstrateCommand(NpgsqlDataSource dataSource)
{
    public Command Build()
    {
        Option<string> archHashOpt = new("--arch-hash",
            "model_architecture entity BLAKE3 hash (64 hex chars)");
        archHashOpt.IsRequired = true;

        Command cmd = new("query-substrate",
            "Print the universal substrate inventory for an ingested model: tensor count, "
            + "distinct layers/heads/MoE-experts, vocab recovered, firefly count, refinement preview "
            + "rows. Calls substrate.model_inventory + substrate.model_vocab_recovered + "
            + "substrate.refinement_summary. Drives the future model-config UI's model card.");
        cmd.AddOption(archHashOpt);

        cmd.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            string archHashHex = ctx.ParseResult.GetValueForOption(archHashOpt)!;
            byte[] archHash = Convert.FromHexString(archHashHex);

            NpgsqlSubstrateQuery query = new(dataSource);

            Console.WriteLine($"=== Substrate inventory for arch={archHashHex[..16]}… ===");

            IReadOnlyList<(string Code, long Value, string? Detail)> inventory =
                await query.GetModelInventoryAsync(archHash, CancellationToken.None);
            foreach ((string code, long val, string? detail) in inventory)
            {
                string detailStr = detail is null ? "" : "  (" + detail + ")";
                Console.WriteLine($"  {code,-32} {val,15:N0}{detailStr}");
            }

            long vocabRecovered = await query.GetModelVocabRecoveredAsync(archHash, CancellationToken.None);
            Console.WriteLine($"  {"vocab_recovered",-32} {vocabRecovered,15:N0}");

            Console.WriteLine();
            Console.WriteLine($"=== Refinement summary (top 20 by Δμ in corroboration_strength) ===");
            IReadOnlyList<(string EdgeType, double SrcOnly, double Consensus, double Delta, bool Above)> refinement =
                await query.GetRefinementSummaryAsync(archHash, "corroboration_strength", 20, CancellationToken.None);
            if (refinement.Count == 0)
            {
                Console.WriteLine("  (no refinement summary rows; cross-source corroboration arena empty)");
            }
            foreach ((string edgeType, double srcOnly, double consensus, double delta, bool above) in refinement)
            {
                Console.WriteLine($"  {edgeType,-32} src_only={srcOnly,12:F0} consensus={consensus,12:F0} Δ={delta,+12:F0} {(above ? "✓" : " ")}");
            }
        });

        return cmd;
    }
}
