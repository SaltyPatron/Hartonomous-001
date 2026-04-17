using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Cli.Migrations;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Orchestration;
using Hartonomous.Decomposers.Iso639;
using Hartonomous.Decomposers.Ucd;
using Hartonomous.Decomposers.Omw;
using Hartonomous.Decomposers.Safetensors;
using Hartonomous.Decomposers.WordNet;
using Hartonomous.Engine.Ingestion;
using Hartonomous.Engine.Orchestration;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;

namespace Hartonomous.Cli;

internal static class Program
{
    private static readonly string[] ConnAliases = ["--connection", "-c"];
    private static readonly string[] DirAliases = ["--dir", "-d"];

    public static async Task<int> Main(string[] args)
    {
        RootCommand root = new("Hartonomous CLI");

        Command migrate = BuildMigrateCommand();
        root.AddCommand(migrate);

        Command phases = BuildPhasesCommand();
        root.AddCommand(phases);

        Command session = BuildSessionCommand();
        root.AddCommand(session);

        Command status = BuildStatusCommand();
        root.AddCommand(status);

        return await root.InvokeAsync(args);
    }

    private static string DefaultConnectionString()
    {
        return Environment.GetEnvironmentVariable("HARTONOMOUS_DB")
            ?? "Host=localhost;Port=5433;Username=hartonomous;Password=hartonomous;Database=hartonomous";
    }

    private static Command BuildMigrateCommand()
    {
        Option<string> connOpt = new(
            aliases: ConnAliases,
            getDefaultValue: DefaultConnectionString,
            description: "Npgsql connection string. Defaults to HARTONOMOUS_DB env var or local docker-compose defaults.");

        Option<string> dirOpt = new(
            aliases: DirAliases,
            getDefaultValue: () => Path.Combine(RepoRoot(), "sql", "migrations"),
            description: "Migrations directory.");

        Command migrate = new("migrate", "Apply, roll back, or inspect database migrations.");
        migrate.AddGlobalOption(connOpt);
        migrate.AddGlobalOption(dirOpt);

        Command up = new("up", "Apply all unapplied migrations.");
        up.SetHandler(async (string conn, string dir) =>
        {
            MigrationRunner runner = new(conn, dir);
            await runner.UpAsync(CancellationToken.None);
        }, connOpt, dirOpt);

        Command down = new("down", "Roll back the most recently applied migrations.");
        Argument<int> stepsArg = new("steps", getDefaultValue: () => 1, description: "Number of migrations to roll back.");
        down.AddArgument(stepsArg);
        down.SetHandler(async (string conn, string dir, int steps) =>
        {
            MigrationRunner runner = new(conn, dir);
            await runner.DownAsync(steps, CancellationToken.None);
        }, connOpt, dirOpt, stepsArg);

        Command status = new("status", "Show applied vs pending migrations and detect checksum drift.");
        status.SetHandler(async (string conn, string dir) =>
        {
            MigrationRunner runner = new(conn, dir);
            await runner.StatusAsync(CancellationToken.None);
        }, connOpt, dirOpt);

        migrate.AddCommand(up);
        migrate.AddCommand(down);
        migrate.AddCommand(status);
        return migrate;
    }

    private static Command BuildPhasesCommand()
    {
        Command phases = new("phases", "Inspect and run the phase dependency DAG.");

        Command list = new("list", "Print all phases in topological (execution) order.");
        list.SetHandler(() =>
        {
            IReadOnlyList<Phase> order = PhaseDag.TopologicalOrder();
            Console.WriteLine($"{"Phase",-25}{"Dependencies",-50}");
            Console.WriteLine(new string('-', 75));
            foreach (Phase p in order)
            {
                IReadOnlyList<Phase> deps = PhaseDag.GetDependencies(p);
                string depStr = deps.Count == 0 ? "(none)" : string.Join(", ", deps);
                Console.WriteLine($"{p,-25}{depStr,-50}");
            }
        });

        Option<string?> phaseOpt = new("--phase", "Run a specific phase (by name). Omit to run all.");
        Option<bool> dryRunOpt = new("--dry-run", "Print execution plan without running.");
        Option<string> connOpt2 = new(
            aliases: ConnAliases,
            getDefaultValue: DefaultConnectionString,
            description: "Npgsql connection string.");
        Option<string> sourceOpt = new(
            aliases: ["--source", "-s"],
            getDefaultValue: () => @"D:\Models",
            description: "Root directory containing all source data (UCD, ISO639, etc.).");

        Option<bool> skipDepsOpt = new(
            aliases: ["--skip-deps"],
            getDefaultValue: () => false,
            description: "Skip dependency phases (assume they already ran).");

        Command run = new("run", "Execute phases in dependency order.");
        run.AddOption(phaseOpt);
        run.AddOption(dryRunOpt);
        run.AddOption(connOpt2);
        run.AddOption(sourceOpt);
        run.AddOption(skipDepsOpt);
        run.SetHandler(async (string? phaseStr, bool dryRun, string conn, string source, bool skipDeps) =>
        {
            if (dryRun)
            {
                PrintDryRun(phaseStr);
                return;
            }

            await RunPhasesAsync(phaseStr, conn, source, skipDeps, CancellationToken.None);
        }, phaseOpt, dryRunOpt, connOpt2, sourceOpt, skipDepsOpt);

        Command statusCmd = new("status", "Show the status of all phases.");
        statusCmd.SetHandler(() =>
        {
            IReadOnlyList<Phase> order = PhaseDag.TopologicalOrder();
            Console.WriteLine($"{"Phase",-25}{"Status",-15}{"Dependencies Met?",-20}");
            Console.WriteLine(new string('-', 60));
            foreach (Phase p in order)
            {
                IReadOnlyList<Phase> deps = PhaseDag.GetDependencies(p);
                string depsMet = deps.Count == 0 ? "yes" : "pending";
                Console.WriteLine($"{p,-25}{"NotStarted",-15}{depsMet,-20}");
            }
        });

        phases.AddCommand(list);
        phases.AddCommand(run);
        phases.AddCommand(statusCmd);
        return phases;
    }

    private static async Task RunPhasesAsync(string? phaseStr, string conn, string sourceRoot, bool skipDeps, CancellationToken ct)
    {
        using ILoggerFactory logFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        DecomposerConfig ucdConfig = new()
        {
            SourceDirectory = Path.Combine(sourceRoot, "UCD", "Public", "UCD", "latest"),
            ConnectionString = conn,
        };

        DecomposerConfig iso639Config = new()
        {
            SourceDirectory = Path.Combine(sourceRoot, "ISO639"),
            ConnectionString = conn,
        };

        DecomposerConfig wordnetConfig = new()
        {
            SourceDirectory = Path.Combine(sourceRoot, "princeton-wordnet", "WordNet-3.0", "dict"),
            ConnectionString = conn,
        };

        DecomposerConfig omwConfig = new()
        {
            SourceDirectory = Path.Combine(sourceRoot, "omw"),
            ConnectionString = conn,
        };

        DecomposerConfig modelConfig = new()
        {
            SourceDirectory = ResolveModelSource(sourceRoot),
            ConnectionString = conn,
        };

        Dictionary<Phase, IReadOnlyList<IDecomposer>> decomposers = new()
        {
            [Phase.UcdUca] = [new UcdUcaDecomposer(ucdConfig, logFactory.CreateLogger<UcdUcaDecomposer>())],
            [Phase.Iso639] = [new Iso639Decomposer(iso639Config, logFactory.CreateLogger<Iso639Decomposer>())],
            [Phase.WordNetOmw] =
            [
                new WordNetDecomposer(wordnetConfig, logFactory.CreateLogger<WordNetDecomposer>()),
                new OmwDecomposer(omwConfig, logFactory.CreateLogger<OmwDecomposer>()),
            ],
            [Phase.ModelDecomp] =
            [
                new SafetensorsDecomposer(modelConfig, logFactory.CreateLogger<SafetensorsDecomposer>()),
            ],
        };

        await using NpgsqlIngestionPipeline pipeline = new(conn, logFactory.CreateLogger<NpgsqlIngestionPipeline>());
        ConsoleProgressReporter reporter = new();
        SequentialPhaseRunner runner = new(decomposers, pipeline, reporter, logFactory.CreateLogger<SequentialPhaseRunner>());

        if (phaseStr is not null)
        {
            if (!Enum.TryParse<Phase>(phaseStr, ignoreCase: true, out Phase target))
            {
                Console.Error.WriteLine($"Unknown phase: '{phaseStr}'");
                return;
            }

            if (skipDeps)
            {
                runner.MarkAllCompleted();
            }
            else
            {
                HashSet<Phase> required = [];
                CollectDependencies(target, required);

                foreach (Phase dep in PhaseDag.TopologicalOrder())
                {
                    if (required.Contains(dep))
                    {
                        PhaseResult depResult = await runner.RunPhaseAsync(dep, ct);
                        if (depResult.Status == PhaseStatus.Failed)
                        {
                            Console.Error.WriteLine($"Dependency {dep} failed: {depResult.ErrorMessage}");
                            return;
                        }
                    }
                }
            }

            PhaseResult result = await runner.RunPhaseAsync(target, ct);
            Console.WriteLine($"\n{result.Phase}: {result.Status} ({result.Elapsed.TotalSeconds:F1}s)");
            if (result.ErrorMessage is not null)
            {
                Console.Error.WriteLine($"  Error: {result.ErrorMessage}");
            }
        }
        else
        {
            IReadOnlyList<PhaseResult> results = await runner.RunAllAsync(ct);
            Console.WriteLine("\n=== Phase Results ===");
            foreach (PhaseResult r in results)
            {
                Console.Write($"  {r.Phase,-25} {r.Status,-15} {r.Elapsed.TotalSeconds,8:F1}s");
                if (r.ErrorMessage is not null)
                {
                    Console.Write($"  ERROR: {r.ErrorMessage}");
                }
                Console.WriteLine();
            }
        }

        Console.WriteLine($"\nPipeline stats: {pipeline.Stats.EntitiesSubmitted:N0} entities, {pipeline.Stats.EdgesSubmitted:N0} edges, {pipeline.Stats.BatchesCommitted:N0} batches committed");
    }

    private static void PrintDryRun(string? phaseFilter)
    {
        IReadOnlyList<Phase> order = PhaseDag.TopologicalOrder();

        if (phaseFilter is not null)
        {
            if (!Enum.TryParse<Phase>(phaseFilter, ignoreCase: true, out Phase target))
            {
                Console.Error.WriteLine($"Unknown phase: '{phaseFilter}'");
                Console.Error.WriteLine($"Valid phases: {string.Join(", ", Enum.GetNames<Phase>())}");
                return;
            }

            HashSet<Phase> required = [];
            CollectDependencies(target, required);
            required.Add(target);

            Console.WriteLine($"Dry-run plan for phase: {target}");
            Console.WriteLine($"{"Step",-6}{"Phase",-25}{"Dependencies",-40}");
            Console.WriteLine(new string('-', 71));

            int step = 1;
            foreach (Phase p in order)
            {
                if (required.Contains(p))
                {
                    IReadOnlyList<Phase> deps = PhaseDag.GetDependencies(p);
                    string depStr = deps.Count == 0 ? "(none)" : string.Join(", ", deps);
                    Console.WriteLine($"{step++,-6}{p,-25}{depStr,-40}");
                }
            }
        }
        else
        {
            Console.WriteLine("Dry-run plan: all phases in topological order");
            Console.WriteLine($"{"Step",-6}{"Phase",-25}{"Dependencies",-40}");
            Console.WriteLine(new string('-', 71));

            int step = 1;
            foreach (Phase p in order)
            {
                IReadOnlyList<Phase> deps = PhaseDag.GetDependencies(p);
                string depStr = deps.Count == 0 ? "(none)" : string.Join(", ", deps);
                Console.WriteLine($"{step++,-6}{p,-25}{depStr,-40}");
            }
        }
    }

    /// <summary>
    /// Lets <c>--source</c> be a Models root (full ingest), a hub dir, a single
    /// <c>models--{publisher}--{name}</c> dir, or a single snapshot dir — without
    /// requiring callers to copy files into a staging location for smoke runs.
    /// </summary>
    private static string ResolveModelSource(string source)
    {
        string hubChild = Path.Combine(source, "hub");
        if (Directory.Exists(hubChild))
        {
            return hubChild;
        }
        return source;
    }

    private static void CollectDependencies(Phase phase, HashSet<Phase> collected)
    {
        foreach (Phase dep in PhaseDag.GetDependencies(phase))
        {
            if (collected.Add(dep))
            {
                CollectDependencies(dep, collected);
            }
        }
    }

    private static Command BuildSessionCommand()
    {
        Option<string> connOpt = new(
            aliases: ConnAliases,
            getDefaultValue: DefaultConnectionString,
            description: "Npgsql connection string.");

        Command session = new("session", "Manage significance computation sessions.");
        session.AddGlobalOption(connOpt);

        Argument<string> descArg = new("description", "Session description.");
        Option<string?> phaseOpt = new("--phase", "Phase code for this session.");
        Command create = new("create", "Create a new open session.");
        create.AddArgument(descArg);
        create.AddOption(phaseOpt);
        create.SetHandler(async (string conn, string desc, string? phase) =>
        {
            await using Npgsql.NpgsqlDataSource ds = Npgsql.NpgsqlDataSource.Create(conn);
            await using Npgsql.NpgsqlConnection c = await ds.OpenConnectionAsync();
            await using Npgsql.NpgsqlCommand cmd = new("SELECT monitor.create_session($1, $2)", c);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Text, desc);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Text,
                (object?)phase ?? DBNull.Value);
            object? result = await cmd.ExecuteScalarAsync();
            Console.WriteLine($"Session created: {result}");
        }, connOpt, descArg, phaseOpt);

        Command close = new("close", "Close the active session and capture significance snapshot.");
        close.SetHandler(async (string conn) =>
        {
            await using Npgsql.NpgsqlDataSource ds = Npgsql.NpgsqlDataSource.Create(conn);
            await using Npgsql.NpgsqlConnection c = await ds.OpenConnectionAsync();
            await using Npgsql.NpgsqlCommand cmd = new("SELECT monitor.close_session()", c);
            object? result = await cmd.ExecuteScalarAsync();
            Console.WriteLine($"Session closed: {result}");
        }, connOpt);

        Command list = new("list", "List all sessions.");
        list.SetHandler(async (string conn) =>
        {
            await using Npgsql.NpgsqlDataSource ds = Npgsql.NpgsqlDataSource.Create(conn);
            await using Npgsql.NpgsqlConnection c = await ds.OpenConnectionAsync();
            await using Npgsql.NpgsqlCommand cmd = new(
                "SELECT session_id, description, phase_code, status, created_at, closed_at " +
                "FROM monitor.session ORDER BY session_id", c);
            await using Npgsql.NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();

            Console.WriteLine($"{"ID",-5}{"Description",-35}{"Phase",-20}{"Status",-10}{"Created",-25}{"Closed",-25}");
            Console.WriteLine(new string('-', 120));
            while (await reader.ReadAsync())
            {
                Console.WriteLine(
                    $"{reader.GetInt64(0),-5}" +
                    $"{reader.GetString(1),-35}" +
                    $"{(reader.IsDBNull(2) ? "-" : reader.GetString(2)),-20}" +
                    $"{reader.GetString(3),-10}" +
                    $"{reader.GetDateTime(4).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),-25}" +
                    $"{(reader.IsDBNull(5) ? "-" : reader.GetDateTime(5).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),-25}");
            }
        }, connOpt);

        Argument<long> archiveIdArg = new("session-id", "Session ID to archive.");
        Command archive = new("archive", "Archive a closed session.");
        archive.AddArgument(archiveIdArg);
        archive.SetHandler(async (string conn, long id) =>
        {
            await using Npgsql.NpgsqlDataSource ds = Npgsql.NpgsqlDataSource.Create(conn);
            await using Npgsql.NpgsqlConnection c = await ds.OpenConnectionAsync();
            await using Npgsql.NpgsqlCommand cmd = new("CALL monitor.archive_session($1)", c);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Bigint, id);
            await cmd.ExecuteNonQueryAsync();
            Console.WriteLine($"Session {id} archived.");
        }, connOpt, archiveIdArg);

        Argument<long> showIdArg = new("session-id", "Session ID to show.");
        Command show = new("show", "Show session details and event count.");
        show.AddArgument(showIdArg);
        show.SetHandler(async (string conn, long id) =>
        {
            await using Npgsql.NpgsqlDataSource ds = Npgsql.NpgsqlDataSource.Create(conn);
            await using Npgsql.NpgsqlConnection c = await ds.OpenConnectionAsync();

            await using (Npgsql.NpgsqlCommand cmd = new(
                "SELECT description, phase_code, status, created_at, closed_at FROM monitor.session WHERE session_id = $1", c))
            {
                cmd.Parameters.AddWithValue(NpgsqlDbType.Bigint, id);
                await using Npgsql.NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    Console.WriteLine($"Session {id}: {reader.GetString(0)}");
                    Console.WriteLine($"  Phase: {(reader.IsDBNull(1) ? "-" : reader.GetString(1))}");
                    Console.WriteLine($"  Status: {reader.GetString(2)}");
                    Console.WriteLine($"  Created: {reader.GetDateTime(3):yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine($"  Closed: {(reader.IsDBNull(4) ? "-" : reader.GetDateTime(4).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}");
                }
                else
                {
                    Console.Error.WriteLine($"Session {id} not found.");
                    return;
                }
            }

            await using (Npgsql.NpgsqlCommand cmd2 = new(
                "SELECT COUNT(*) FROM monitor.comparison_event WHERE session_id = $1", c))
            {
                cmd2.Parameters.AddWithValue(NpgsqlDbType.Bigint, id);
                Console.WriteLine($"  Comparison events: {await cmd2.ExecuteScalarAsync()}");
            }

            await using (Npgsql.NpgsqlCommand cmd3 = new(
                "SELECT COUNT(*) FROM monitor.significance_snapshot WHERE session_id = $1", c))
            {
                cmd3.Parameters.AddWithValue(NpgsqlDbType.Bigint, id);
                Console.WriteLine($"  Snapshot rows: {await cmd3.ExecuteScalarAsync()}");
            }
        }, connOpt, showIdArg);

        session.AddCommand(create);
        session.AddCommand(close);
        session.AddCommand(list);
        session.AddCommand(archive);
        session.AddCommand(show);
        return session;
    }

    private static Command BuildStatusCommand()
    {
        Option<string> connOpt = new(
            aliases: ConnAliases,
            getDefaultValue: DefaultConnectionString,
            description: "Npgsql connection string.");

        Option<bool> snapshotOpt = new("--snapshot", "Take a health snapshot before reporting.");

        Command status = new("status", "Show substrate health dashboard.");
        status.AddOption(connOpt);
        status.AddOption(snapshotOpt);
        status.SetHandler(async (string conn, bool snapshot) =>
        {
            await using Npgsql.NpgsqlDataSource ds = Npgsql.NpgsqlDataSource.Create(conn);
            await using Npgsql.NpgsqlConnection c = await ds.OpenConnectionAsync();

            if (snapshot)
            {
                await using Npgsql.NpgsqlCommand snap = new("CALL monitor.snapshot_health()", c);
                await snap.ExecuteNonQueryAsync();
                Console.WriteLine("Health snapshot captured.");
                Console.WriteLine();
            }

            Console.WriteLine("=== Phase Overview ===");
            await using (Npgsql.NpgsqlCommand cmd = new(
                "SELECT phase_code, status, entity_count, edge_count, " +
                "EXTRACT(EPOCH FROM (completed_at - started_at))::int AS duration_s " +
                "FROM monitor.phase_status ORDER BY started_at NULLS LAST", c))
            {
                await using Npgsql.NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
                Console.WriteLine($"{"Phase",-25}{"Status",-15}{"Entities",-12}{"Edges",-12}{"Duration(s)",-12}");
                Console.WriteLine(new string('-', 76));
                while (await reader.ReadAsync())
                {
                    Console.WriteLine(
                        $"{reader.GetString(0),-25}" +
                        $"{reader.GetString(1),-15}" +
                        $"{reader.GetInt64(2),-12}" +
                        $"{reader.GetInt64(3),-12}" +
                        $"{(reader.IsDBNull(4) ? "-" : reader.GetInt32(4).ToString(CultureInfo.InvariantCulture)),-12}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== Substrate Totals ===");
            await using (Npgsql.NpgsqlCommand cmd = new(
                "SELECT total_entities, total_edges, total_physicalities, total_significance_records " +
                "FROM monitor.substrate_dashboard", c))
            {
                await using Npgsql.NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    Console.WriteLine($"  Entities:     {reader.GetInt64(0):N0}");
                    Console.WriteLine($"  Edges:        {reader.GetInt64(1):N0}");
                    Console.WriteLine($"  Physicalities:{reader.GetInt64(2):N0}");
                    Console.WriteLine($"  Significance: {reader.GetInt64(3):N0}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== Active Runs ===");
            await using (Npgsql.NpgsqlCommand cmd = new(
                "SELECT decomposer_code, phase_code, batch_number, entities_ingested, started_at " +
                "FROM monitor.v_active_runs", c))
            {
                await using Npgsql.NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
                bool any = false;
                while (await reader.ReadAsync())
                {
                    any = true;
                    Console.WriteLine($"  {reader.GetString(0)} ({reader.GetString(1)}) " +
                        $"batch {reader.GetInt32(2)}: {reader.GetInt64(3)} entities");
                }
                if (!any)
                {
                    Console.WriteLine("  (none)");
                }
            }
        }, connOpt, snapshotOpt);

        return status;
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Hartonomous.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? Directory.GetCurrentDirectory();
    }
}
