using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Cli.Configuration;
using Hartonomous.Core;
using Hartonomous.Core.Data;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Orchestration;
using Hartonomous.Core.Text;
using Hartonomous.Decomposers.Ucd;
using Hartonomous.Decomposers.Iso639;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Cli.Commands;

/// <summary>
/// Inspects and runs the phase dependency DAG.
/// Injects <see cref="IConfiguration"/> from the host to load
/// <see cref="HartonomousOptions"/> without rebuilding the config stack.
/// </summary>
internal sealed class PhasesCommand(IConfiguration configuration)
{
    public Command Build()
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
        Option<string> connOpt = new(
            CliConfiguration.ConnAliases,
            getDefaultValue: CliConfiguration.DefaultConnectionString,
            description: "Npgsql connection string.");
        Option<string?> sourceOpt = new(
            aliases: ["--source", "-s"],
            description: "Root directory containing all source data. Omit to use Hartonomous:DataRoot from appsettings/env.");
        Option<string?> modelSourceOpt = new(
            aliases: ["--model-source"],
            description: "Override the model hub root used by ModelDecomp (Decomposers.Safetensors.HubPath). Absolute paths bypass --source/DataRoot. Omit to use config.");
        Option<bool> skipDepsOpt = new(
            aliases: ["--skip-deps"],
            getDefaultValue: () => false,
            description: "Skip dependency phases (assume they already ran).");
        Option<bool> forceOpt = new(
            aliases: ["--force", "-f"],
            getDefaultValue: () => false,
            description: "Re-run the phase even if monitor.phase_status says it already completed.");

        Command run = new("run", "Execute phases in dependency order.");
        run.AddOption(phaseOpt);
        run.AddOption(dryRunOpt);
        run.AddOption(connOpt);
        run.AddOption(sourceOpt);
        run.AddOption(modelSourceOpt);
        run.AddOption(skipDepsOpt);
        run.AddOption(forceOpt);
        run.SetHandler(async (InvocationContext ic) =>
        {
            string? phaseStr = ic.ParseResult.GetValueForOption(phaseOpt);
            bool dryRun = ic.ParseResult.GetValueForOption(dryRunOpt);
            string conn = ic.ParseResult.GetValueForOption(connOpt)!;
            string? source = ic.ParseResult.GetValueForOption(sourceOpt);
            string? modelSource = ic.ParseResult.GetValueForOption(modelSourceOpt);
            bool skipDeps = ic.ParseResult.GetValueForOption(skipDepsOpt);
            bool force = ic.ParseResult.GetValueForOption(forceOpt);

            if (dryRun)
            {
                ic.ExitCode = PrintDryRun(phaseStr);
                return;
            }

            ic.ExitCode = await RunPhasesAsync(phaseStr, conn, source, modelSource, skipDeps, force, CancellationToken.None);
        });

        Option<string> statusConnOpt = new(
            CliConfiguration.ConnAliases,
            getDefaultValue: CliConfiguration.DefaultConnectionString,
            description: "Npgsql connection string.");

        Command statusCmd = new("status", "Show the status of all phases.");
        statusCmd.AddOption(statusConnOpt);
        statusCmd.SetHandler(async (InvocationContext ic) =>
        {
            string conn = ic.ParseResult.GetValueForOption(statusConnOpt)!;
            await using NpgsqlDataSource phaseDs = NpgsqlDataSource.Create(conn);
            NpgsqlSessionStore sessionStore = new(phaseDs);
            IReadOnlyDictionary<string, string> statusMap = await sessionStore.GetPhaseStatusMapAsync(CancellationToken.None);

            IReadOnlyList<Phase> order = PhaseDag.TopologicalOrder();
            Console.WriteLine($"{"Phase",-25}{"Status",-15}{"Dependencies Met?",-20}");
            Console.WriteLine(new string('-', 60));
            foreach (Phase p in order)
            {
                IReadOnlyList<Phase> deps = PhaseDag.GetDependencies(p);
                string status = statusMap.TryGetValue(p.ToString(), out string? persisted)
                    ? persisted
                    : "not_started";
                string depsMet = deps.Count == 0 || deps.All(dep =>
                    statusMap.TryGetValue(dep.ToString(), out string? depStatus)
                    && string.Equals(depStatus, "completed", StringComparison.OrdinalIgnoreCase))
                    ? "yes"
                    : "no";
                Console.WriteLine($"{p,-25}{status,-15}{depsMet,-20}");
            }
        });

        phases.AddCommand(list);
        phases.AddCommand(run);
        phases.AddCommand(statusCmd);
        return phases;
    }

    private async Task<int> RunPhasesAsync(
        string? phaseStr, string conn, string? sourceRoot, string? modelSourceRoot,
        bool skipDeps, bool force, CancellationToken ct)
    {
        int exitCode = 0;

        // Log level resolves from HARTONOMOUS_LOG_LEVEL env var (Trace, Debug,
        // Information, Warning, Error). Defaults to Information for normal runs.
        LogLevel logLevel = LogLevel.Information;
        string? envLevel = Environment.GetEnvironmentVariable("HARTONOMOUS_LOG_LEVEL");
        if (!string.IsNullOrWhiteSpace(envLevel) && Enum.TryParse(envLevel, true, out LogLevel parsed))
        {
            logLevel = parsed;
        }

        using ILoggerFactory logFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(o =>
            {
                o.IncludeScopes = true;
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss.fff ";
            });
            builder.SetMinimumLevel(logLevel);
        });

        // Load per-decomposer configuration from the host's IConfiguration
        // (already populated from appsettings.json + HARTONOMOUS__ env vars).
        HartonomousOptions opts =
            configuration.GetSection("Hartonomous").Get<HartonomousOptions>()
            ?? new HartonomousOptions();

        // CLI --source (when supplied non-empty) overrides DataRoot.
        if (!string.IsNullOrWhiteSpace(sourceRoot))
        {
            opts.DataRoot = sourceRoot;
        }
        // CLI --model-source (when supplied non-empty) overrides Decomposers.Safetensors.HubPath.
        // Absolute paths bypass DataRoot via CliPathResolver.
        if (!string.IsNullOrWhiteSpace(modelSourceRoot))
        {
            opts.Decomposers.Safetensors.HubPath = modelSourceRoot;
        }
        string dataRoot = opts.DataRoot;

        string Resolve(string p) => CliPathResolver.Resolve(dataRoot, p);

        DecomposerConfig ucdConfig = new() { SourceDirectory = Resolve(opts.Decomposers.Ucd.SourcePath), ConnectionString = conn };
        DecomposerConfig iso639Config = new() { SourceDirectory = Resolve(opts.Decomposers.Iso639.SourcePath), ConnectionString = conn };
        DecomposerConfig wordnetConfig = new() { SourceDirectory = Resolve(opts.Decomposers.WordNet.SourcePath), ConnectionString = conn };
        DecomposerConfig omwConfig = new() { SourceDirectory = Resolve(opts.Decomposers.Omw.SourcePath), ConnectionString = conn, LanguageFilter = opts.Decomposers.Omw.LanguageFilter };
        DecomposerConfig udConfig = new() { SourceDirectory = Resolve(opts.Decomposers.Ud.SourcePath), ConnectionString = conn, LanguageFilter = opts.Decomposers.Ud.LanguageFilter };
        DecomposerConfig modelConfig = new()
        {
            SourceDirectory = Resolve(opts.Decomposers.Safetensors.HubPath),
            ConnectionString = conn,
            ModelFilter = opts.Decomposers.Safetensors.ModelFilter is { Length: > 0 } ? opts.Decomposers.Safetensors.ModelFilter : null,
        };
        DecomposerConfig wiktionaryConfig = new() { SourceDirectory = Resolve(opts.Decomposers.Wiktionary.SourcePath), ConnectionString = conn, LanguageFilter = opts.Decomposers.Wiktionary.LanguageFilter };
        DecomposerConfig tatoebaConfig = new() { SourceDirectory = Resolve(opts.Decomposers.Tatoeba.SourcePath), ConnectionString = conn, LanguageFilter = opts.Decomposers.Tatoeba.LanguageFilter };
        string textSourceDir = Resolve(opts.Decomposers.Text.SourcePath);

        await using NpgsqlDataSource phaseDs = NpgsqlDataSource.Create(conn);
        NpgsqlReferenceDataReader refDataReader = new(phaseDs);
        NpgsqlJunctionWriter junctionWriter = new(phaseDs);
        NpgsqlReferenceDataWriter refDataWriter = new(phaseDs);

        SubstrateTextDecomposer substrateTextDecomposer = new(phaseDs);

        ThrowingCodepointProperties cpProps = ThrowingCodepointProperties.Instance;

        List<IDecomposer> textDecomposers = [];
        string[] textFiles = Directory.Exists(textSourceDir)
            ? [.. Directory.EnumerateFiles(textSourceDir, "*.txt")]
            : [];
        if (textFiles.Length > 0)
        {
            foreach (string txtFile in textFiles)
            {
                DecomposerConfig textConfig = new() { SourceDirectory = txtFile, ConnectionString = conn };
                textDecomposers.Add(new TextDecomposer(
                    textConfig,
                    logFactory.CreateLogger<TextDecomposer>(),
                    cpProps,
                    refDataReader, junctionWriter, refDataWriter));
            }
        }

        WordNetSynsetBridge wordNetSynsetBridge = new();

        Dictionary<Phase, IReadOnlyList<IDecomposer>> decomposers = new()
        {
            [Phase.UcdUca] = [new UnicodeDecomposer(ucdConfig, logFactory.CreateLogger<UnicodeDecomposer>())],
            [Phase.Iso639] = [new Iso639Decomposer(iso639Config, logFactory.CreateLogger<Iso639Decomposer>(), cpProps, refDataReader, junctionWriter, refDataWriter)],
            [Phase.WordNetOmw] =
            [
                new WordNetDecomposer(wordnetConfig, substrateTextDecomposer, logFactory.CreateLogger<WordNetDecomposer>(), cpProps, wordNetSynsetBridge, refDataReader, junctionWriter, refDataWriter),
                new OmwDecomposer(omwConfig, substrateTextDecomposer, logFactory.CreateLogger<OmwDecomposer>(), cpProps, wordNetSynsetBridge, refDataReader, junctionWriter, refDataWriter),
            ],
            [Phase.UniversalDeps] =
            [
                new UdDecomposer(udConfig, logFactory.CreateLogger<UdDecomposer>(), cpProps, refDataReader, junctionWriter, refDataWriter),
            ],
            [Phase.ModelDecomp] =
            [
                new SafetensorsDecomposer(modelConfig, logFactory.CreateLogger<SafetensorsDecomposer>(), logFactory, checkpointStore: new NpgsqlCheckpointStore(phaseDs), referenceDataReader: refDataReader, junctionWriter: junctionWriter, referenceDataWriter: refDataWriter, alignmentDataSource: phaseDs, substrateTextDecomposer: substrateTextDecomposer),
            ],
            [Phase.Wiktionary] =
            [
                new WiktionaryDecomposer(wiktionaryConfig, substrateTextDecomposer, logFactory.CreateLogger<WiktionaryDecomposer>(), cpProps, refDataReader, junctionWriter, refDataWriter),
            ],
            [Phase.Tatoeba] =
            [
                new TatoebaDecomposer(tatoebaConfig, substrateTextDecomposer, logFactory.CreateLogger<TatoebaDecomposer>(), cpProps, refDataReader, junctionWriter, refDataWriter),
            ],
            [Phase.TextDecomp] = textDecomposers,
            [Phase.SignificanceField] =
            [
                new Hartonomous.Engine.Significance.SignificanceFieldRunner(
                    conn,
                    logFactory.CreateLogger<Hartonomous.Engine.Significance.SignificanceFieldRunner>()),
            ],
        };

        await using StreamingIngestionPipeline pipeline = new(conn, refDataReader, logFactory.CreateLogger<StreamingIngestionPipeline>());

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

        if (force && phaseStr is not null)
        {
            await sessionStore.ResetPhaseCheckpointAsync(phaseStr, ct);
        }

        if (phaseStr is not null)
        {
            if (!Enum.TryParse<Phase>(phaseStr, ignoreCase: true, out Phase target))
            {
                Console.Error.WriteLine($"Unknown phase: '{phaseStr}'");
                return 1;
            }

            bool dependencyFailed = false;
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
                            exitCode = 1;
                            dependencyFailed = true;
                            break;
                        }
                    }
                }
            }

            if (!dependencyFailed)
            {
                PhaseResult result = await runner.RunPhaseAsync(target, ct);
                Console.WriteLine($"\n{result.Phase}: {result.Status} ({result.Elapsed.TotalSeconds:F1}s)");
                if (result.ErrorMessage is not null)
                {
                    Console.Error.WriteLine($"  Error: {result.ErrorMessage}");
                }

                if (result.Status == PhaseStatus.Failed)
                {
                    exitCode = 1;
                }
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
            if (results.Any(static result => result.Status == PhaseStatus.Failed))
            {
                exitCode = 1;
            }
        }

        await pipeline.FlushAsync(ct);

        StreamingPipelineStats sStats = pipeline.Stats;
        Console.WriteLine($"\nStreaming pipeline emitted: {sStats.EntitiesEmitted:N0} entities, {sStats.EdgesEmitted:N0} edges, {sStats.EdgeMembersEmitted:N0} edge_members, {sStats.JunctionsEmitted:N0} junctions, {sStats.PhysicalitiesEmitted:N0} physicalities, {sStats.EntitySignificancesEmitted:N0} entity_sigs, {sStats.EdgeSignificancesEmitted:N0} edge_sigs ({sStats.CopyCommits:N0} drain commits, {sStats.CopyErrors:N0} errors)");
        if (sStats.CopyErrors > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"ERROR: {sStats.CopyErrors:N0} chunk drain errors during ingestion. Substrate may be incomplete.");
            exitCode = 1;
        }

        return exitCode;
    }

    private static int PrintDryRun(string? phaseFilter)
    {
        IReadOnlyList<Phase> order = PhaseDag.TopologicalOrder();

        if (phaseFilter is not null)
        {
            if (!Enum.TryParse<Phase>(phaseFilter, ignoreCase: true, out Phase target))
            {
                Console.Error.WriteLine($"Unknown phase: '{phaseFilter}'");
                Console.Error.WriteLine($"Valid phases: {string.Join(", ", Enum.GetNames<Phase>())}");
                return 1;
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

        return 0;
    }

    private static async Task<HashSet<int>> CollectDistinctCodepointsAsync(
        IEnumerable<string> textFiles, CancellationToken ct)
    {
        HashSet<int> codepoints = [];
        foreach (string textFile in textFiles)
        {
            byte[] utf8Bytes = await File.ReadAllBytesAsync(textFile, ct);
            int idx = 0;
            while (idx < utf8Bytes.Length)
            {
                (int cp, int consumed) = Hartonomous.Core.Text.Segmentation.Utf8.DecodeOne(utf8Bytes.AsSpan(idx));
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

    private static string ResolveModelSource(string source)
    {
        string hubChild = Path.Combine(source, "hub");
        return Directory.Exists(hubChild) ? hubChild : source;
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
}
