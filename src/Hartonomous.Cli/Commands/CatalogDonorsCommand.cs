using System.CommandLine;
using Hartonomous.Decomposers.Cataloging;
using Hartonomous.Decomposers.Safetensors.Adapters;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Cli.Commands;

internal static class CatalogDonorsCommand
{
    private static readonly string[] HubAliases = ["--hub", "-h"];
    private static readonly string[] OutAliases = ["--out", "-o"];

    public static Command Build()
    {
        Option<string> hubOpt = new(
            HubAliases,
            description: "Path to the HuggingFace hub root (e.g. D:\\Models\\hub) containing per-model directories.")
        { IsRequired = true };

        Option<string> outOpt = new(
            OutAliases,
            getDefaultValue: () => Path.Combine("docs", "specs", "donors"),
            description: "Output directory for per-model manifests, the unified tensor-pattern-catalog.json, and the rolled-up README.md.");

        Command cmd = new(
            "catalog-donors",
            "Walk the donor hub, classify every tensor in every package via the registered architecture adapters, "
            + "and emit one manifest.json per model + a unified pattern catalog. Rejected packages (AWQ/GGUF) and "
            + "unsupported V1 architectures are recorded with an explicit status and required-adapter hint.");
        cmd.AddOption(hubOpt);
        cmd.AddOption(outOpt);

        cmd.SetHandler(async (string hub, string outDir) =>
        {
            string fullOut = Path.IsPathRooted(outDir) ? outDir : Path.GetFullPath(outDir);
            using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddSimpleConsole(o =>
                {
                    o.SingleLine = true;
                    o.TimestampFormat = "HH:mm:ss ";
                });
                builder.SetMinimumLevel(LogLevel.Information);
            });

            ILogger<DonorPackageCataloger> logger = loggerFactory.CreateLogger<DonorPackageCataloger>();

            IArchitectureAdapter[] adapters =
            [
                new RerankerAdapter(loggerFactory.CreateLogger<RerankerAdapter>()),
                new EmbeddingAdapter(loggerFactory.CreateLogger<EmbeddingAdapter>()),
                new MoeAdapter(loggerFactory.CreateLogger<MoeAdapter>()),
                new DenseLlmAdapter(loggerFactory.CreateLogger<DenseLlmAdapter>()),
            ];

            DonorPackageCataloger cataloger = new(logger, loggerFactory, adapters);
            Console.WriteLine($"==== Catalog donors: hub={hub} → out={fullOut} ====");

            CatalogRunSummary summary = await cataloger.CatalogHubAsync(hub, fullOut, CancellationToken.None);

            Console.WriteLine();
            Console.WriteLine("==== Run summary ====");
            Console.WriteLine($"Discovered:           {summary.Discovered}");
            Console.WriteLine($"Ingested:             {summary.Ingested}");
            Console.WriteLine($"Unsupported (V1):     {summary.UnsupportedV1}");
            Console.WriteLine($"Rejected (AWQ/GGUF):  {summary.Rejected}");
            Console.WriteLine($"Discovery failed:     {summary.DiscoveryFailed}");
            Console.WriteLine($"Unique patterns:      {summary.UniquePatterns}");
            Console.WriteLine($"Unclassified tensors: {summary.UnclassifiedTensors}");
            Console.WriteLine();
            Console.WriteLine($"Catalog written under: {summary.OutputRoot}");

            Environment.ExitCode = summary.UnclassifiedTensors == 0 && summary.DiscoveryFailed == 0 ? 0 : 1;
        }, hubOpt, outOpt);

        return cmd;
    }
}
