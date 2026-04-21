using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Cli.Migrations;
using Hartonomous.Core;
using Hartonomous.Core.Data;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Orchestration;
using Hartonomous.Core.Text.Segmentation;
using Hartonomous.Decomposers.Iso639;
using Hartonomous.Decomposers.Ucd;
using Hartonomous.Decomposers.Omw;
using Hartonomous.Decomposers.Safetensors;
using Hartonomous.Decomposers.Tatoeba;
using Hartonomous.Decomposers.Text;
using Hartonomous.Decomposers.Ud;
using Hartonomous.Decomposers.Wiktionary;
using Hartonomous.Decomposers.WordNet;
using Hartonomous.Engine.Data;
using Hartonomous.Engine.Ingestion;
using Hartonomous.Engine.Orchestration;
using Hartonomous.Engine.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Cli;

internal static class Program
{
    private static readonly string[] ConnAliases = ["--connection", "-c"];
    private static readonly string[] DirAliases = ["--dir", "-d"];

    public static async Task<int> Main(string[] args)
    {
        PrepareNativeLoadPath();
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

    private static void PrepareNativeLoadPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string? oneApi = Environment.GetEnvironmentVariable("ONEAPI_ROOT")
                      ?? (Directory.Exists(@"C:\Program Files (x86)\Intel\oneAPI")
                          ? @"C:\Program Files (x86)\Intel\oneAPI"
                          : null);
        if (oneApi is null)
        {
            return;
        }
        string mklBin = Path.Combine(oneApi, "mkl", "latest", "bin");
        string cmpBin = Path.Combine(oneApi, "compiler", "latest", "bin");
        string tbbBin = Path.Combine(oneApi, "tbb", "latest", "bin");
        string current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        Environment.SetEnvironmentVariable("PATH", $"{mklBin};{cmpBin};{tbbBin};{current}");
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
            await using NpgsqlDataSource ds = NpgsqlDataSource.Create(conn);
            MigrationRunner runner = new(new NpgsqlMigrationStore(ds), dir);
            await runner.UpAsync(CancellationToken.None);
        }, connOpt, dirOpt);

        Command down = new("down", "Roll back the most recently applied migrations.");
        Argument<int> stepsArg = new("steps", getDefaultValue: () => 1, description: "Number of migrations to roll back.");
        down.AddArgument(stepsArg);
        down.SetHandler(async (string conn, string dir, int steps) =>
        {
            await using NpgsqlDataSource ds = NpgsqlDataSource.Create(conn);
            MigrationRunner runner = new(new NpgsqlMigrationStore(ds), dir);
            await runner.DownAsync(steps, CancellationToken.None);
        }, connOpt, dirOpt, stepsArg);

        Command status = new("status", "Show applied vs pending migrations and detect checksum drift.");
        status.SetHandler(async (string conn, string dir) =>
        {
            await using NpgsqlDataSource ds = NpgsqlDataSource.Create(conn);
            MigrationRunner runner = new(new NpgsqlMigrationStore(ds), dir);
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

        DecomposerConfig udConfig = new()
        {
            SourceDirectory = Path.Combine(sourceRoot, "ud-treebanks", "ud-treebanks-v2.17"),
            ConnectionString = conn,
        };

        DecomposerConfig modelConfig = new()
        {
            SourceDirectory = ResolveModelSource(sourceRoot),
            ConnectionString = conn,
        };

        DecomposerConfig wiktionaryConfig = new()
        {
            SourceDirectory = Path.Combine(sourceRoot, "wiktionary", "raw-wiktextract-data.jsonl"),
            ConnectionString = conn,
        };

        DecomposerConfig tatoebaConfig = new()
        {
            SourceDirectory = Path.Combine(sourceRoot, "tatoeba"),
            ConnectionString = conn,
        };

        // TextDecomp: point at the test_data/text directory. Each .txt file is a document.
        string textSourceDir = Path.Combine(sourceRoot, "test_data", "text");

        await using NpgsqlDataSource phaseDs = NpgsqlDataSource.Create(conn);
        NpgsqlReferenceDataReader refDataReader = new(phaseDs);
        NpgsqlJunctionWriter junctionWriter = new(phaseDs);
        NpgsqlReferenceDataWriter refDataWriter = new(phaseDs);

        // Build per-file text decomposers if the directory exists.
        List<IDecomposer> textDecomposers = [];
        string[] textFiles = Directory.Exists(textSourceDir)
            ? [.. Directory.EnumerateFiles(textSourceDir, "*.txt")]
            : [];
        if (textFiles.Length > 0)
        {
            HashSet<int> textCodepoints = await CollectDistinctCodepointsAsync(textFiles, ct);
            NpgsqlCodepointPropertiesCache cpProps = await NpgsqlCodepointPropertiesCache.LoadForCodepointsAsync(
                conn,
                textCodepoints,
                logFactory.CreateLogger<NpgsqlCodepointPropertiesCache>(),
                ct);

            foreach (string txtFile in textFiles)
            {
                DecomposerConfig textConfig = new()
                {
                    SourceDirectory = txtFile,
                    ConnectionString = conn,
                };
                textDecomposers.Add(new TextDecomposer(
                    textConfig,
                    logFactory.CreateLogger<TextDecomposer>(),
                    cpProps,
                    refDataReader, junctionWriter, refDataWriter));
            }
        }

        Dictionary<Phase, IReadOnlyList<IDecomposer>> decomposers = new()
        {
            [Phase.UcdUca] = [new UcdUcaDecomposer(ucdConfig, logFactory.CreateLogger<UcdUcaDecomposer>(), refDataReader, junctionWriter, refDataWriter)],
            [Phase.Iso639] = [new Iso639Decomposer(iso639Config, logFactory.CreateLogger<Iso639Decomposer>(), refDataReader, junctionWriter, refDataWriter)],
            [Phase.WordNetOmw] =
            [
                new WordNetDecomposer(wordnetConfig, logFactory.CreateLogger<WordNetDecomposer>(), refDataReader, junctionWriter, refDataWriter),
                new OmwDecomposer(omwConfig, logFactory.CreateLogger<OmwDecomposer>(), refDataReader, junctionWriter, refDataWriter),
            ],
            [Phase.UniversalDeps] =
            [
                new UdDecomposer(udConfig, logFactory.CreateLogger<UdDecomposer>(), refDataReader, junctionWriter, refDataWriter),
            ],
            [Phase.ModelDecomp] =
            [
                new SafetensorsDecomposer(modelConfig, logFactory.CreateLogger<SafetensorsDecomposer>(), logFactory, referenceDataReader: refDataReader, junctionWriter: junctionWriter, referenceDataWriter: refDataWriter),
            ],
            [Phase.Wiktionary] =
            [
                new WiktionaryDecomposer(wiktionaryConfig, logFactory.CreateLogger<WiktionaryDecomposer>(), refDataReader, junctionWriter, refDataWriter),
            ],
            [Phase.Tatoeba] =
            [
                new TatoebaDecomposer(tatoebaConfig, logFactory.CreateLogger<TatoebaDecomposer>(), refDataReader, junctionWriter, refDataWriter),
            ],
            [Phase.TextDecomp] = textDecomposers,
        };
        await using NpgsqlIngestionPipeline pipeline = new(conn, refDataReader, logFactory.CreateLogger<NpgsqlIngestionPipeline>());
        ConsoleProgressReporter reporter = new();
        NpgsqlSessionStore sessionStore = new(phaseDs);
        SequentialPhaseRunner runner = new(
            decomposers, pipeline, reporter,
            logFactory.CreateLogger<SequentialPhaseRunner>(),
            sessionStore);
        await runner.HydrateStatusAsync(ct);

        if (phaseStr is not null)
        {
            if (!Enum.TryParse<Phase>(phaseStr, ignoreCase: true, out Phase target))
            {
                Console.Error.WriteLine($"Unknown phase: '{phaseStr}'");
                return;
            }

            if (skipDeps)
            {
                runner.MarkAllCompletedExcept(target);
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

    private static async Task<HashSet<int>> CollectDistinctCodepointsAsync(
        IEnumerable<string> textFiles,
        CancellationToken ct)
    {
        HashSet<int> codepoints = [];
        foreach (string textFile in textFiles)
        {
            byte[] utf8Bytes = await File.ReadAllBytesAsync(textFile, ct);
            int idx = 0;
            while (idx < utf8Bytes.Length)
            {
                (int cp, int consumed) = Utf8.DecodeOne(utf8Bytes.AsSpan(idx));
                if (consumed == 0)
                {
                    break;
                }

                codepoints.Add(cp);
                idx += consumed;
            }
        }

        return codepoints;
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
            NpgsqlSessionStore store = new(ds);
            long id = await store.CreateSessionAsync(phase ?? string.Empty, desc, CancellationToken.None);
            Console.WriteLine($"Session created: {id}");
        }, connOpt, descArg, phaseOpt);

        Command close = new("close", "Close the active session and capture significance snapshot.");
        close.SetHandler(async (string conn) =>
        {
            await using Npgsql.NpgsqlDataSource ds = Npgsql.NpgsqlDataSource.Create(conn);
            NpgsqlSessionStore store = new(ds);
            bool closed = await store.CloseSessionAsync(CancellationToken.None);
            Console.WriteLine($"Session closed: {closed}");
        }, connOpt);

        Command list = new("list", "List all sessions.");
        list.SetHandler(async (string conn) =>
        {
            await using Npgsql.NpgsqlDataSource ds = Npgsql.NpgsqlDataSource.Create(conn);
            NpgsqlSessionStore store = new(ds);
            IReadOnlyList<SessionSummary> sessions = await store.ListSessionsAsync(CancellationToken.None);

            Console.WriteLine($"{"ID",-5}{"Description",-35}{"Phase",-20}{"Status",-10}{"Created",-25}{"Closed",-25}");
            Console.WriteLine(new string('-', 120));
            foreach (SessionSummary s in sessions)
            {
                Console.WriteLine(
                    $"{s.SessionId,-5}" +
                    $"{s.Description,-35}" +
                    $"{(string.IsNullOrEmpty(s.PhaseCode) ? "-" : s.PhaseCode),-20}" +
                    $"{s.Status,-10}" +
                    $"{s.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),-25}" +
                    $"{(s.ClosedAt.HasValue ? s.ClosedAt.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "-"),-25}");
            }
        }, connOpt);

        Argument<long> archiveIdArg = new("session-id", "Session ID to archive.");
        Command archive = new("archive", "Archive a closed session.");
        archive.AddArgument(archiveIdArg);
        archive.SetHandler(async (string conn, long id) =>
        {
            await using Npgsql.NpgsqlDataSource ds = Npgsql.NpgsqlDataSource.Create(conn);
            NpgsqlSessionStore store = new(ds);
            await store.ArchiveSessionAsync(id, CancellationToken.None);
            Console.WriteLine($"Session {id} archived.");
        }, connOpt, archiveIdArg);

        Argument<long> showIdArg = new("session-id", "Session ID to show.");
        Command show = new("show", "Show session details and event count.");
        show.AddArgument(showIdArg);
        show.SetHandler(async (string conn, long id) =>
        {
            await using Npgsql.NpgsqlDataSource ds = Npgsql.NpgsqlDataSource.Create(conn);
            NpgsqlSessionStore store = new(ds);
            SessionDetail? detail = await store.GetSessionDetailAsync(id, CancellationToken.None);
            if (detail is null)
            {
                Console.Error.WriteLine($"Session {id} not found.");
                return;
            }

            Console.WriteLine($"Session {id}: {detail.Description}");
            Console.WriteLine($"  Phase: {(string.IsNullOrEmpty(detail.PhaseCode) ? "-" : detail.PhaseCode)}");
            Console.WriteLine($"  Status: {detail.Status}");
            Console.WriteLine($"  Created: {detail.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"  Closed: {(detail.ClosedAt.HasValue ? detail.ClosedAt.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "-")}");
            Console.WriteLine($"  Comparison events: {detail.ComparisonEventCount}");
            Console.WriteLine($"  Snapshot rows: {detail.SignificanceSnapshotCount}");
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
            NpgsqlSessionStore store = new(ds);

            if (snapshot)
            {
                await store.SnapshotHealthAsync(CancellationToken.None);
                Console.WriteLine("Health snapshot captured.");
                Console.WriteLine();
            }

            Console.WriteLine("=== Phase Overview ===");
            IReadOnlyList<PhaseStatusRow> phases = await store.GetPhaseStatusOverviewAsync(CancellationToken.None);
            Console.WriteLine($"{"Phase",-25}{"Status",-15}{"Entities",-12}{"Edges",-12}{"Duration(s)",-12}");
            Console.WriteLine(new string('-', 76));
            foreach (PhaseStatusRow p in phases)
            {
                Console.WriteLine(
                    $"{p.PhaseCode,-25}" +
                    $"{p.Status,-15}" +
                    $"{p.EntityCount,-12}" +
                    $"{p.EdgeCount,-12}" +
                    $"{(p.DurationSeconds.HasValue ? p.DurationSeconds.Value.ToString(CultureInfo.InvariantCulture) : "-"),-12}");
            }

            Console.WriteLine();
            Console.WriteLine("=== Substrate Totals ===");
            SubstrateTotals? totals = await store.GetSubstrateTotalsAsync(CancellationToken.None);
            if (totals is not null)
            {
                Console.WriteLine($"  Entities:     {totals.TotalEntities:N0}");
                Console.WriteLine($"  Edges:        {totals.TotalEdges:N0}");
                Console.WriteLine($"  Physicalities:{totals.TotalPhysicalities:N0}");
                Console.WriteLine($"  Significance: {totals.TotalSignificanceRecords:N0}");
            }

            Console.WriteLine();
            Console.WriteLine("=== Active Runs ===");
            IReadOnlyList<ActiveRunRow> runs = await store.GetActiveRunsAsync(CancellationToken.None);
            if (runs.Count == 0)
            {
                Console.WriteLine("  (none)");
            }
            else
            {
                foreach (ActiveRunRow r in runs)
                {
                    Console.WriteLine($"  {r.DecomposerCode} ({r.PhaseCode}) " +
                        $"batch {r.BatchNumber}: {r.EntitiesIngested} entities");
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
