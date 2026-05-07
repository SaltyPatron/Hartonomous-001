using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Hartonomous.Engine.Data;
using Npgsql;

namespace Hartonomous.Cli.Commands;

/// <summary>
/// Displays phase status from <c>monitor.phase_status</c> and substrate health metrics.
/// </summary>
internal sealed class StatusCommand(NpgsqlDataSource dataSource)
{
    public Command Build()
    {
        Option<bool> jsonOpt = new("--json", getDefaultValue: () => false, description: "Emit health summary as raw JSON from substrate.health_summary().");
        Option<bool> phasesOpt = new("--phases", getDefaultValue: () => false, description: "Show phase status table from monitor.phase_status.");

        Command status = new("status", "Show substrate health and phase status.");
        status.AddOption(jsonOpt);
        status.AddOption(phasesOpt);

        status.SetHandler(async (InvocationContext ic) =>
        {
            bool asJson = ic.ParseResult.GetValueForOption(jsonOpt);
            bool showPhases = ic.ParseResult.GetValueForOption(phasesOpt);

            NpgsqlSessionStore store = new(dataSource);

            if (asJson)
            {
                string? json = await store.GetHealthSummaryJsonAsync(CancellationToken.None);
                Console.WriteLine(json ?? "{}");
                return;
            }

            if (showPhases)
            {
                IReadOnlyList<PhaseStatusRow> rows = await store.GetPhaseStatusOverviewAsync(CancellationToken.None);
                if (rows.Count == 0)
                {
                    Console.WriteLine("No phase status rows (no phases have run yet, or monitor.phase_status is empty).");
                    return;
                }
                Console.WriteLine($"{"Phase",-25} {"Status",-12} {"Entities":>12} {"Edges":>10} {"Duration":>10}");
                Console.WriteLine(new string('-', 72));
                foreach (PhaseStatusRow r in rows)
                {
                    string dur = r.DurationSeconds.HasValue
                        ? TimeSpan.FromSeconds(r.DurationSeconds.Value).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                        : "-";
                    Console.WriteLine(
                        $"{r.PhaseCode,-25} {r.Status,-12} {r.EntityCount,12:N0} {r.EdgeCount,10:N0} {dur,10}");
                }
                return;
            }

            // Default: show substrate entity/edge counts
            IReadOnlyList<(string EntityType, long EntityCount, long EdgeCount)> counts =
                await store.GetSubstrateCountsAsync(CancellationToken.None);

            if (counts.Count == 0)
            {
                Console.WriteLine("Substrate is empty.");
                return;
            }

            SubstrateTotals? totals = await store.GetSubstrateTotalsAsync(CancellationToken.None);

            Console.WriteLine($"{"Entity Type",-25} {"Classified":>12} {"Edges":>10}");
            Console.WriteLine(new string('-', 48));
            foreach ((string et, long ec, long edg) in counts)
            {
                Console.WriteLine($"{et,-25} {ec,12:N0} {edg,10:N0}");
            }
            Console.WriteLine(new string('-', 48));
            if (totals is not null)
            {
                Console.WriteLine($"{"SUBSTRATE TOTAL",-25} {totals.TotalEntities,12:N0} {totals.TotalEdges,10:N0}");
            }
        });

        return status;
    }
}
