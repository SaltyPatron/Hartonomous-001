using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using Hartonomous.Cli.Commands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Hartonomous.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        PrepareNativeLoadPath();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        // Ensure CLI settings are loaded from the executable directory where
        // appsettings.json is copied, regardless of the caller's working dir.
        string baseDir = AppContext.BaseDirectory;
        builder.Configuration
            .AddJsonFile(Path.Combine(baseDir, "appsettings.json"), optional: true, reloadOnChange: false)
            .AddJsonFile(Path.Combine(baseDir, "appsettings.Development.json"), optional: true, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables(prefix: "HARTONOMOUS__");

        string connStr = builder.Configuration["Hartonomous:ConnectionString"]
                      ?? CliConfiguration.DefaultConnectionString();
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connStr));
        builder.Services.AddSingleton<QuerySubstrateCommand>();
        builder.Services.AddSingleton<AuditWalkCommand>();
        builder.Services.AddSingleton<HealthCommand>();
        builder.Services.AddSingleton<ExportModelCommand>();
        builder.Services.AddSingleton<SynthesizeModelCommand>();
        builder.Services.AddSingleton<PhasesCommand>();
        builder.Services.AddSingleton<SessionCommand>();
        builder.Services.AddSingleton<StatusCommand>();
        builder.Services.AddSingleton<QueryCommand>();
        builder.Services.AddSingleton<RecallCommand>();
        builder.Services.AddSingleton<GodelCommand>();

        IHost host = builder.Build();
        IServiceProvider sp = host.Services;

        RootCommand root = new("Hartonomous CLI");
        root.AddCommand(sp.GetRequiredService<PhasesCommand>().Build());
        root.AddCommand(sp.GetRequiredService<SessionCommand>().Build());
        root.AddCommand(sp.GetRequiredService<StatusCommand>().Build());
        root.AddCommand(sp.GetRequiredService<QueryCommand>().Build());
        root.AddCommand(sp.GetRequiredService<GodelCommand>().Build());
        root.AddCommand(sp.GetRequiredService<RecallCommand>().Build());
        root.AddCommand(sp.GetRequiredService<ExportModelCommand>().Build());
        root.AddCommand(sp.GetRequiredService<SynthesizeModelCommand>().Build());
        root.AddCommand(CompareModelCommand.Build());
        root.AddCommand(sp.GetRequiredService<QuerySubstrateCommand>().Build());
        root.AddCommand(sp.GetRequiredService<AuditWalkCommand>().Build());
        root.AddCommand(sp.GetRequiredService<HealthCommand>().Build());
        root.AddCommand(BootstrapCommand.Build());
        root.AddCommand(CatalogDonorsCommand.Build());
        root.AddCommand(EmbedLookupCommand.Build(CliConfiguration.DefaultConnectionString));
        root.AddCommand(ClassifyCommand.Build(CliConfiguration.DefaultConnectionString));
        root.AddCommand(RerankCommand.Build(CliConfiguration.DefaultConnectionString));
        root.AddCommand(QuoteCommand.Build(CliConfiguration.DefaultConnectionString));
        root.AddCommand(CompleteCommand.Build(CliConfiguration.DefaultConnectionString));

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
}
