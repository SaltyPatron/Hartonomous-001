using System;
using System.CommandLine;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Cli.Migrations;
using Npgsql;

namespace Hartonomous.Cli.Commands;

/// <summary>
/// Applies the canonical substrate schema from <c>sql/schema/</c> to a fresh
/// database via the bootstrap manifest.
/// </summary>
internal sealed class BootstrapCommand
{
    private static readonly string[] ManifestAliases = ["--manifest", "-m"];

    public static Command Build()
    {
        Option<string> connOpt = new(
            CliConfiguration.ConnAliases,
            () => CliConfiguration.DefaultConnectionString(),
            "Connection string");
        Option<string> manifestOpt = new(
            ManifestAliases,
            getDefaultValue: () => Path.Combine("sql", "schema", "bootstrap.sql"),
            description: "Path to the bootstrap manifest. Default: sql/schema/bootstrap.sql. The file's @include directives are recursively resolved via MigrationFileLoader.LoadResolved before execution.");

        Command cmd = new("bootstrap",
            "Apply the canonical substrate schema from sql/schema/ to a fresh database. "
            + "No version tracking, no schema_version writes, no migration numbering — the "
            + "manifest @includes the schema/ tree in dependency order; reseed re-applies "
            + "everything. Pre-v1: edit canonical schema files in place, drop+create+bootstrap.");
        cmd.AddOption(connOpt);
        cmd.AddOption(manifestOpt);

        cmd.SetHandler(async (string conn, string manifest) =>
        {
            string fullPath = Path.IsPathRooted(manifest)
                ? manifest
                : Path.GetFullPath(manifest);
            if (!File.Exists(fullPath))
            {
                Console.Error.WriteLine($"Bootstrap manifest not found: {fullPath}");
                Environment.ExitCode = 2;
                return;
            }

            Console.WriteLine($"==== Bootstrap: resolving {fullPath} ====");
            string sql = MigrationFileLoader.LoadResolved(fullPath);

            await using NpgsqlDataSource ds = NpgsqlDataSource.Create(conn);
            await using NpgsqlConnection c = await ds.OpenConnectionAsync(CancellationToken.None);
            await using NpgsqlTransaction tx = await c.BeginTransactionAsync(CancellationToken.None);
            try
            {
                await using NpgsqlCommand command = new(sql, c, tx);
                command.CommandTimeout = 600;
                await command.ExecuteNonQueryAsync(CancellationToken.None);
                await tx.CommitAsync(CancellationToken.None);
                Console.WriteLine("==== Bootstrap complete ====");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(CancellationToken.None);
                Console.Error.WriteLine($"Bootstrap failed: {ex.Message}");
                throw;
            }
        }, connOpt, manifestOpt);

        return cmd;
    }
}
