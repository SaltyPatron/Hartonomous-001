using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
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

        Command query = BuildQueryCommand();
        root.AddCommand(query);

        Command exportModel = BuildExportModelCommand();
        root.AddCommand(exportModel);

        Command compareModel = BuildCompareModelCommand();
        root.AddCommand(compareModel);

        return await root.InvokeAsync(args);
    }

    private static Command BuildCompareModelCommand()
    {
        Option<string> origOpt = new("--original", "Original safetensors file");
        origOpt.IsRequired = true;
        Option<string> exportedOpt = new("--exported", "Substrate-exported safetensors file");
        exportedOpt.IsRequired = true;

        Command compare = new("compare-models", "Compare two safetensors files tensor-by-tensor (relative Frobenius error).");
        compare.AddOption(origOpt);
        compare.AddOption(exportedOpt);

        compare.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            string origPath = ctx.ParseResult.GetValueForOption(origOpt)!;
            string expPath = ctx.ParseResult.GetValueForOption(exportedOpt)!;
            await CompareModelsAsync(origPath, expPath, CancellationToken.None);
        });

        return compare;
    }

    private static async Task CompareModelsAsync(string origPath, string expPath, CancellationToken ct)
    {
        List<Hartonomous.Decomposers.Safetensors.SafetensorsTensorInfo> origInfos =
            Hartonomous.Decomposers.Safetensors.SafetensorsReader.ReadHeader(origPath);
        List<Hartonomous.Decomposers.Safetensors.SafetensorsTensorInfo> expInfos =
            Hartonomous.Decomposers.Safetensors.SafetensorsReader.ReadHeader(expPath);

        Dictionary<string, Hartonomous.Decomposers.Safetensors.SafetensorsTensorInfo> origMap = new(StringComparer.Ordinal);
        foreach (Hartonomous.Decomposers.Safetensors.SafetensorsTensorInfo ti in origInfos) { origMap[ti.Name] = ti; }
        Dictionary<string, Hartonomous.Decomposers.Safetensors.SafetensorsTensorInfo> expMap = new(StringComparer.Ordinal);
        foreach (Hartonomous.Decomposers.Safetensors.SafetensorsTensorInfo ti in expInfos) { expMap[ti.Name] = ti; }

        Console.WriteLine($"Original: {origInfos.Count} tensors  |  Exported: {expInfos.Count} tensors");
        Console.WriteLine();

        int total = 0;
        int identical = 0;
        int allZero = 0;
        int matched = 0;
        double sumRelErr = 0;
        int sumRelN = 0;

        Dictionary<int, (int n, double sumRel)> byRank = new();

        foreach (string name in origMap.Keys)
        {
            ct.ThrowIfCancellationRequested();
            if (!expMap.TryGetValue(name, out Hartonomous.Decomposers.Safetensors.SafetensorsTensorInfo? expTi)) { continue; }
            Hartonomous.Decomposers.Safetensors.SafetensorsTensorInfo origTi = origMap[name];
            if (!origTi.Shape.SequenceEqual(expTi.Shape)) { continue; }
            total++;

            double[] o = Hartonomous.Decomposers.Safetensors.SafetensorsReader.ReadTensorAsDouble(origTi);
            double[] e = Hartonomous.Decomposers.Safetensors.SafetensorsReader.ReadTensorAsDouble(expTi);
            if (o.Length != e.Length) { continue; }

            double normO = 0, normDiff = 0, expAbsMax = 0;
            for (int i = 0; i < o.Length; i++)
            {
                double d = o[i] - e[i];
                normO += o[i] * o[i];
                normDiff += d * d;
                double ea = Math.Abs(e[i]);
                if (ea > expAbsMax) { expAbsMax = ea; }
            }
            normO = Math.Sqrt(normO);
            normDiff = Math.Sqrt(normDiff);

            bool isAllZero = expAbsMax == 0;
            double relErr = normO > 0 ? normDiff / normO : (normDiff == 0 ? 0 : double.PositiveInfinity);

            if (isAllZero) { allZero++; }
            else if (normDiff == 0) { identical++; matched++; }
            else { matched++; sumRelErr += relErr; sumRelN++; }

            int rank = origTi.Shape.Length;
            if (!byRank.TryGetValue(rank, out (int n, double sumRel) b)) { b = (0, 0.0); }
            byRank[rank] = (b.n + 1, b.sumRel + (isAllZero ? 1.0 : relErr));

            if (total <= 30 || isAllZero || (relErr > 0.5 && relErr < double.PositiveInfinity))
            {
                string status = isAllZero ? "ZERO" : normDiff == 0 ? "EXACT" : $"rel_err={relErr:F4}";
                Console.WriteLine($"  [{rank}D shape={string.Join('x', origTi.Shape)}] {name}: {status}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"=== Summary ===");
        Console.WriteLine($"  total compared:   {total}");
        Console.WriteLine($"  exact match:      {identical}");
        Console.WriteLine($"  zero-filled:      {allZero}  (substrate had no content for these)");
        Console.WriteLine($"  partial reconstruction: {matched - identical}");
        if (sumRelN > 0)
        {
            Console.WriteLine($"  mean rel_err on partial: {sumRelErr / sumRelN:F4}");
        }
        Console.WriteLine();
        Console.WriteLine($"=== By rank ===");
        foreach (KeyValuePair<int, (int n, double sumRel)> kv in byRank.OrderBy(k => k.Key))
        {
            Console.WriteLine($"  {kv.Key}D: {kv.Value.n} tensors, mean rel_err = {kv.Value.sumRel / kv.Value.n:F4}");
        }
        await Task.CompletedTask;
    }

    private static Command BuildExportModelCommand()
    {
        Option<string> connOpt = new(ConnAliases, () => DefaultConnectionString(), "Connection string");
        Option<long> archIdOpt = new("--arch-id", "model_architecture entity id to export");
        archIdOpt.IsRequired = true;
        Option<string> outputOpt = new("--output", "Output safetensors path");
        outputOpt.IsRequired = true;
        Option<long[]> sourceIdsOpt = new("--source-id",
            "model_source_id to filter on (repeat for multiple). Omit for unfiltered export.");
        sourceIdsOpt.AllowMultipleArgumentsPerToken = true;
        Option<double?> minMuOpt = new("--min-significance",
            "Minimum significance mu (inclusive) — restricts the export to entities at or above this rating.");
        Option<string?> contextOpt = new("--context",
            "Significance arena context code (e.g. 'model_trust', 'semantic_relevance').");
        Option<int?> limitOpt = new("--limit",
            "Maximum number of tensors to include in the distilled export.");

        Command exportModel = new("export-model",
            "Recompose a substrate model_architecture into a safetensors file. "
            + "With filter options the export becomes a distillation query (architecture.md "
            + "\"Distillation = WHERE clause\").");
        exportModel.AddOption(connOpt);
        exportModel.AddOption(archIdOpt);
        exportModel.AddOption(outputOpt);
        exportModel.AddOption(sourceIdsOpt);
        exportModel.AddOption(minMuOpt);
        exportModel.AddOption(contextOpt);
        exportModel.AddOption(limitOpt);

        exportModel.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            string conn = ctx.ParseResult.GetValueForOption(connOpt)!;
            long archId = ctx.ParseResult.GetValueForOption(archIdOpt);
            string output = ctx.ParseResult.GetValueForOption(outputOpt)!;
            long[] sourceIds = ctx.ParseResult.GetValueForOption(sourceIdsOpt) ?? [];
            double? minMu = ctx.ParseResult.GetValueForOption(minMuOpt);
            string? context = ctx.ParseResult.GetValueForOption(contextOpt);
            int? limit = ctx.ParseResult.GetValueForOption(limitOpt);

            await using NpgsqlDataSource ds = NpgsqlDataSource.Create(conn);
            Hartonomous.Engine.Data.NpgsqlEntityReader entityReader = new(ds);
            Hartonomous.Engine.Data.NpgsqlPhysicalityReader physReader = new(ds);
            Hartonomous.Engine.Query.NpgsqlSubstrateQuery query = new(ds);

            Hartonomous.Recomposers.SafetensorsRecomposer recomposer = new(
                entityReader, entityReader, physReader, query);

            bool filtered = sourceIds.Length > 0 || minMu.HasValue || !string.IsNullOrEmpty(context) || limit.HasValue;

            Console.WriteLine($"=== {(filtered ? "Distilling" : "Exporting")} model_architecture {archId} → {output} ===");
            if (filtered)
            {
                if (sourceIds.Length > 0) { Console.WriteLine($"  --source-id={string.Join(',', sourceIds)}"); }
                if (minMu.HasValue) { Console.WriteLine($"  --min-significance={minMu.Value}"); }
                if (!string.IsNullOrEmpty(context)) { Console.WriteLine($"  --context={context}"); }
                if (limit.HasValue) { Console.WriteLine($"  --limit={limit.Value}"); }
            }
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

            Hartonomous.Core.Recomposition.RecompositionOptions opts = new() { MaxDepth = 20 };
            Hartonomous.Recomposers.SafetensorsFile file;
            if (filtered)
            {
                Hartonomous.Core.Query.SubstrateQueryFilter filter = new()
                {
                    ModelSourceIds = sourceIds.Length > 0 ? sourceIds : null,
                    MinSignificanceMu = minMu,
                    ContextTypeCode = context,
                    Limit = limit,
                };
                file = await recomposer.RecomposeFilteredAsync(archId, filter, opts, CancellationToken.None);
            }
            else
            {
                file = await recomposer.RecomposeAsync(archId, opts, CancellationToken.None);
            }

            await using (FileStream fs = File.Create(output))
            {
                await Hartonomous.Recomposers.SafetensorsWriter.WriteAsync(file, fs, CancellationToken.None);
            }

            sw.Stop();
            FileInfo fi = new(output);
            Console.WriteLine($"Tensors written: {file.Tensors.Count}");
            Console.WriteLine($"File size: {fi.Length:N0} bytes");
            Console.WriteLine($"Elapsed: {sw.Elapsed.TotalSeconds:F1}s");
        });

        return exportModel;
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
            ?? "Host=localhost;Port=5433;Username=hartonomous;Password=hartonomous;Database=hartonomous;" +
               "Include Error Detail=true;" +
               // Keep a warm pool so the per-connection cold-start cost (postgres
               // backend fork + C extension init) is paid once at startup, not
               // repeatedly per query. Targets the 14900KS/24-core host.
               "Minimum Pool Size=8;Maximum Pool Size=32;Multiplexing=true;" +
               // Heavy seed-phase batches (WordNet/Wiktionary/Tatoeba) include
               // tens of thousands of entities + edges + junctions per batch;
               // the substrate function commit can run several minutes server-
               // side as indexes grow. Default Npgsql timeout (30s) cuts the
               // client wire on `tx.CommitAsync` while the server is still
               // working. 600s gives the server ample headroom; queries that
               // legitimately hang remain bounded.
               "Command Timeout=600;" +
               "Application Name=hartonomous-cli;";
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

        Option<bool> forceOpt = new(
            aliases: ["--force", "-f"],
            getDefaultValue: () => false,
            description: "Re-run the phase even if monitor.phase_status says it already completed. Combine with --skip-deps to retry one phase whose checkpoint partially failed without re-running its predecessors.");

        Command run = new("run", "Execute phases in dependency order.");
        run.AddOption(phaseOpt);
        run.AddOption(dryRunOpt);
        run.AddOption(connOpt2);
        run.AddOption(sourceOpt);
        run.AddOption(skipDepsOpt);
        run.AddOption(forceOpt);
        run.SetHandler(async (InvocationContext ic) =>
        {
            string? phaseStr = ic.ParseResult.GetValueForOption(phaseOpt);
            bool dryRun = ic.ParseResult.GetValueForOption(dryRunOpt);
            string conn = ic.ParseResult.GetValueForOption(connOpt2)!;
            string source = ic.ParseResult.GetValueForOption(sourceOpt)!;
            bool skipDeps = ic.ParseResult.GetValueForOption(skipDepsOpt);
            bool force = ic.ParseResult.GetValueForOption(forceOpt);

            if (dryRun)
            {
                PrintDryRun(phaseStr);
                return;
            }

            await RunPhasesAsync(phaseStr, conn, source, skipDeps, force, CancellationToken.None);
        });

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

    private static async Task RunPhasesAsync(string? phaseStr, string conn, string sourceRoot, bool skipDeps, bool force, CancellationToken ct)
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
            // T0: English alignments only.
            LanguageFilter = new[] { "en", "eng" },
        };

        DecomposerConfig udConfig = new()
        {
            SourceDirectory = Path.Combine(sourceRoot, "ud-treebanks", "ud-treebanks-v2.17"),
            ConnectionString = conn,
            // T0 plan: ingest English treebanks first; expand language coverage in later
            // tiers once the seed chain is verified end-to-end. UD uses ISO 639-1 (2-letter)
            // prefixes on .conllu filenames ("en_ewt-ud-train.conllu"); the language
            // reference table uses ISO 639-3 (3-letter) codes. Include both forms so
            // the filter matches regardless of which code surface the decomposer compares.
            LanguageFilter = new[] { "en", "eng" },
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
            // T0: English entries only.
            LanguageFilter = new[] { "en", "eng" },
        };

        DecomposerConfig tatoebaConfig = new()
        {
            SourceDirectory = Path.Combine(sourceRoot, "tatoeba"),
            ConnectionString = conn,
            // T0: English sentences only.
            LanguageFilter = new[] { "en", "eng" },
        };

        // TextDecomp: point at the test_data/text directory. Each .txt file is a document.
        string textSourceDir = Path.Combine(sourceRoot, "test_data", "text");

        await using NpgsqlDataSource phaseDs = NpgsqlDataSource.Create(conn);
        NpgsqlReferenceDataReader refDataReader = new(phaseDs);
        NpgsqlJunctionWriter junctionWriter = new(phaseDs);
        NpgsqlReferenceDataWriter refDataWriter = new(phaseDs);

        // Codepoint-property cache for all decomposers that go through
        // TextSegmentationEmitter (Tatoeba, WordNet glosses/examples,
        // Wiktionary text bodies, runtime TextDecomposer). Loaded eagerly
        // for the full Unicode range so non-English seed content (Greek,
        // CJK, Arabic, RTL, combining marks) segments correctly per UAX #29.
        NpgsqlCodepointPropertiesCache cpProps = await NpgsqlCodepointPropertiesCache.LoadAsync(
            conn,
            logFactory.CreateLogger<NpgsqlCodepointPropertiesCache>(),
            ct);

        // Build per-file text decomposers if the directory exists.
        List<IDecomposer> textDecomposers = [];
        string[] textFiles = Directory.Exists(textSourceDir)
            ? [.. Directory.EnumerateFiles(textSourceDir, "*.txt")]
            : [];
        if (textFiles.Length > 0)
        {
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
                new WordNetDecomposer(wordnetConfig, logFactory.CreateLogger<WordNetDecomposer>(), cpProps, refDataReader, junctionWriter, refDataWriter),
                new OmwDecomposer(omwConfig, logFactory.CreateLogger<OmwDecomposer>(), refDataReader, junctionWriter, refDataWriter),
            ],
            [Phase.UniversalDeps] =
            [
                new UdDecomposer(udConfig, logFactory.CreateLogger<UdDecomposer>(), refDataReader, junctionWriter, refDataWriter),
            ],
            [Phase.ModelDecomp] =
            [
                new SafetensorsDecomposer(modelConfig, logFactory.CreateLogger<SafetensorsDecomposer>(), logFactory, checkpointStore: new NpgsqlCheckpointStore(phaseDs), referenceDataReader: refDataReader, junctionWriter: junctionWriter, referenceDataWriter: refDataWriter, codepointProperties: cpProps, alignmentDataSource: phaseDs),
            ],
            [Phase.Wiktionary] =
            [
                new WiktionaryDecomposer(wiktionaryConfig, logFactory.CreateLogger<WiktionaryDecomposer>(), cpProps, refDataReader, junctionWriter, refDataWriter),
            ],
            [Phase.Tatoeba] =
            [
                new TatoebaDecomposer(tatoebaConfig, logFactory.CreateLogger<TatoebaDecomposer>(), cpProps, refDataReader, junctionWriter, refDataWriter),
            ],
            [Phase.TextDecomp] = textDecomposers,
            [Phase.SignificanceField] =
            [
                new Hartonomous.Engine.Significance.SignificanceFieldRunner(
                    conn,
                    logFactory.CreateLogger<Hartonomous.Engine.Significance.SignificanceFieldRunner>()),
            ],
        };
        await using NpgsqlIngestionPipeline pipeline = new(conn, refDataReader, logFactory.CreateLogger<NpgsqlIngestionPipeline>());
        ConsoleProgressReporter reporter = new();
        NpgsqlSessionStore sessionStore = new(phaseDs);
        SequentialPhaseRunner runner = new(
            decomposers, pipeline, reporter,
            logFactory.CreateLogger<SequentialPhaseRunner>(),
            sessionStore)
        {
            ForceRerun = force,
        };
        await runner.HydrateStatusAsync(ct);

        // --force also clears stale model_pass_checkpoint and monitor.phase_status
        // rows for the target phase so the orchestrator inside the safetensors
        // decomposer doesn't skip "already-completed" passes that committed
        // partial state. Without this, --force only bypasses the runner-level
        // gate but the per-pass checkpoint still short-circuits.
        if (force && phaseStr is not null)
        {
            // Two separate commands — Npgsql's prepared-statement protocol
            // (used implicitly under multiplexing) rejects multi-statement
            // batches with "cannot insert multiple commands into a prepared
            // statement". Split into one DELETE + one TRUNCATE.
            await using NpgsqlDataSource resetDs = NpgsqlDataSource.Create(conn);
            await using NpgsqlConnection resetConn = await resetDs.OpenConnectionAsync(ct);
            await using (NpgsqlCommand del = new(
                "DELETE FROM monitor.phase_status WHERE phase_code = $1", resetConn))
            {
                del.Parameters.AddWithValue(phaseStr);
                await del.ExecuteNonQueryAsync(ct);
            }
            await using (NpgsqlCommand trunc = new(
                "TRUNCATE TABLE substrate.model_pass_checkpoint", resetConn))
            {
                await trunc.ExecuteNonQueryAsync(ct);
            }
        }

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

    private static Command BuildQueryCommand()
    {
        Option<string> connOpt = new(
            aliases: ConnAliases,
            getDefaultValue: DefaultConnectionString,
            description: "Npgsql connection string.");

        Argument<string[]> textArg = new("text", "Prompt. Decomposed into substrate entities; the substrate's A* traversal IS the forward pass.");
        textArg.Arity = ArgumentArity.OneOrMore;

        Command query = new("query", "Run a forward pass through the substrate. The prompt becomes substrate content and the substrate's significance-weighted A* traversal across all arenas produces the recomposed answer. No caller-specified arena, depth, cost-budget, or result-cap — those would compromise the invention.");
        query.AddOption(connOpt);
        query.AddArgument(textArg);

        query.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            string conn = ctx.ParseResult.GetValueForOption(connOpt)!;
            string[] textParts = ctx.ParseResult.GetValueForArgument(textArg);
            string text = string.Join(' ', textParts);

            await using Npgsql.NpgsqlDataSource ds = Npgsql.NpgsqlDataSource.Create(conn);
            Hartonomous.Engine.Data.NpgsqlReferenceDataReader refReader = new(ds);
            Hartonomous.Engine.Data.NpgsqlEntityReader entityReader = new(ds);
            Hartonomous.Engine.Traversal.NpgsqlTraversal traversal = new(ds, refReader);

            using Microsoft.Extensions.Logging.ILoggerFactory lf =
                Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning));
            Hartonomous.Engine.Inference.SubstrateInferenceEngine engine = new(
                traversal, entityReader, refReader, lf.CreateLogger<Hartonomous.Engine.Inference.SubstrateInferenceEngine>());

            Hartonomous.Core.Engine.InferenceQuery q = new() { Text = text };

            Console.WriteLine($"=== Forward pass ===");
            Console.WriteLine($"  prompt: {text}");
            Console.WriteLine();

            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            Hartonomous.Core.Engine.InferenceResult result = await engine.InferAsync(q, CancellationToken.None);
            sw.Stop();

            Console.WriteLine($"=== Substrate output ===");
            Console.WriteLine($"  answer: {(string.IsNullOrEmpty(result.Answer) ? "(no path — honest abstention)" : result.Answer)}");
            Console.WriteLine();
            Console.WriteLine($"=== Trace ===");
            Console.WriteLine($"  seeds activated:  {result.SeedEntityIds.Count}");
            Console.WriteLine($"  composite paths:  {result.Paths.Count}");
            Console.WriteLine($"  nodes visited:    {result.NodesVisited}");
            Console.WriteLine($"  elapsed:          {sw.Elapsed.TotalMilliseconds:F1} ms");

            if (result.Paths.Count == 0)
            {
                return;
            }
            Console.WriteLine();
            Console.WriteLine($"=== Top {Math.Min(5, result.Paths.Count)} contributing paths ===");
            await using Npgsql.NpgsqlConnection rconn = await ds.OpenConnectionAsync(CancellationToken.None);
            for (int i = 0; i < Math.Min(5, result.Paths.Count); i++)
            {
                Hartonomous.Core.Engine.TraversalPath path = result.Paths[i];
                long targetId = path.Steps.Count > 0 ? path.Steps[^1].EntityId : 0;
                string targetText = await TryRecomposeAsync(rconn, targetId);
                Console.WriteLine($"  [{i+1}] significance={path.PathSignificance:F4} depth={path.Steps.Count - 1} → {targetText}");
            }
        });

        return query;
    }

    private static async Task<string> TryRecomposeAsync(Npgsql.NpgsqlConnection conn, long entityId)
    {
        if (entityId <= 0)
        {
            return "<no target>";
        }
        await using Npgsql.NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT substrate.recompose_text($1)";
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter { Value = entityId });
        try
        {
            object? result = await cmd.ExecuteScalarAsync();
            if (result is string s && !string.IsNullOrEmpty(s))
            {
                return s.Length > 120 ? s[..117] + "..." : s;
            }
            return $"<entity {entityId}>";
        }
        catch (Exception ex) // BOUNDARY: CLI display surface — recomposition errors on a single entity must not abort listing the other paths in the result.
        {
            return $"<entity {entityId} (recompose error: {ex.Message[..Math.Min(60, ex.Message.Length)]})>";
        }
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
