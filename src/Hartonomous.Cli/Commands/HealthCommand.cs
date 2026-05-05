using System;
using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Engine.Data;
using Npgsql;

namespace Hartonomous.Cli.Commands;

/// <summary>
/// One-call substrate state probe via <c>substrate.health_summary()</c>.
/// </summary>
internal sealed class HealthCommand(NpgsqlDataSource dataSource)
{
    public Command Build()
    {
        Command health = new("health",
            "One-call substrate state probe via substrate.health_summary(). " +
            "Counts every entity / edge / physicality / significance row by " +
            "type code, mean μ per arena, and database storage size.");

        health.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            NpgsqlSessionStore store = new(dataSource);
            string? json = await store.GetHealthSummaryJsonAsync(CancellationToken.None);
            if (string.IsNullOrEmpty(json))
            {
                Console.Error.WriteLine("substrate.health_summary() returned NULL.");
                ctx.ExitCode = 1;
                return;
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            Console.WriteLine("=== Substrate Health ===");
            Console.WriteLine($"  Total entities:      {root.GetProperty("totalEntities").GetInt64():N0}");
            Console.WriteLine($"  Total edges:         {root.GetProperty("totalEdges").GetInt64():N0}");
            Console.WriteLine($"  Total edge members:  {root.GetProperty("totalEdgeMembers").GetInt64():N0}");
            Console.WriteLine($"  Total physicalities: {root.GetProperty("totalPhysicalities").GetInt64():N0}");
            Console.WriteLine($"  Entity significance: {root.GetProperty("totalEntitySig").GetInt64():N0}");
            Console.WriteLine($"  Edge significance:   {root.GetProperty("totalEdgeSig").GetInt64():N0}");
            Console.WriteLine($"  Storage:             {root.GetProperty("storageSizeBytes").GetInt64():N0} bytes");

            PrintObject(root, "entitiesByType", "Entities by type");
            PrintObject(root, "edgesByType", "Edges by type");
            PrintObject(root, "entityMeanMuByArena", "Entity mean μ by arena");
            PrintObject(root, "edgeMeanMuByArena", "Edge mean μ by arena");
        });

        return health;
    }

    private static void PrintObject(JsonElement root, string property, string title)
    {
        if (!root.TryGetProperty(property, out JsonElement obj))
        {
            return;
        }

        if (obj.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"=== {title} ===");
        bool any = false;
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            Console.WriteLine($"  {p.Name,-30} {p.Value}");
            any = true;
        }
        if (!any)
        {
            Console.WriteLine("  (none)");
        }
    }
}
