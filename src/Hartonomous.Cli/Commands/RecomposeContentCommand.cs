using System.CommandLine;
using System.IO;
using Hartonomous.Core.Data;
using Npgsql;

namespace Hartonomous.Cli.Commands;

/// <summary>
/// Recompose UTF-8 content from a substrate entity hash. The load-bearing
/// reconstruction-property demonstration: ONE substrate query via the C
/// extension's pg_recompose_walk + cp_from_hash mmap lookup + UTF-8 byte
/// assembly, returning the full content as bytea. Holistic stack — C does
/// the DFS walk + memory-direct codepoint resolution; SQL hosts the
/// recursive query; C# orchestrates with one ExecuteScalarAsync.
///
/// Per the substrate's geometry-as-indexed-manifest contract: total walk
/// runtime is O(tier-depth × bbtree-probe-microseconds), not O(document-length).
/// Bible-size documents (~200K words) reconstruct in sub-second.
/// </summary>
internal static class RecomposeContentCommand
{
    private static readonly string[] ConnAliases = ["--connection", "-c"];
    private static readonly string[] HashAliases = ["--hash", "-h"];
    private static readonly string[] OutAliases = ["--out", "-o"];
    private static readonly string[] DepthAliases = ["--max-depth"];

    public static Command Build(Func<string> defaultConnectionString)
    {
        Option<string> connOpt = new(ConnAliases, defaultConnectionString, "Connection string");
        Option<string> hashOpt = new(
            HashAliases,
            description: "Document entity hash (hex-encoded 32 bytes / 64 hex chars).")
        { IsRequired = true };
        Option<string?> outOpt = new(
            OutAliases,
            description: "Output file path. If omitted, writes assembled UTF-8 to stdout.");
        Option<int> depthOpt = new(
            DepthAliases,
            getDefaultValue: () => 16,
            description: "Maximum walk depth (defends against pathological cycles; default 16).");

        Command cmd = new(
            "recompose-content",
            "Reconstruct UTF-8 content from a substrate document/content entity hash via the substrate's "
            + "geometry-as-indexed-manifest tree walk. ONE PG round trip; sub-second for Bible-size documents.");
        cmd.AddOption(connOpt);
        cmd.AddOption(hashOpt);
        cmd.AddOption(outOpt);
        cmd.AddOption(depthOpt);

        cmd.SetHandler(async (string conn, string hashHex, string? outPath, int maxDepth) =>
        {
            byte[] hashBytes;
            try { hashBytes = Convert.FromHexString(hashHex); }
            catch (FormatException ex)
            {
                Console.Error.WriteLine($"Invalid --hash (expected 64 hex chars): {ex.Message}");
                Environment.ExitCode = 2;
                return;
            }
            if (hashBytes.Length != 32)
            {
                Console.Error.WriteLine($"Invalid --hash length: {hashBytes.Length}; expected 32 bytes (64 hex chars)");
                Environment.ExitCode = 2;
                return;
            }

            await using NpgsqlDataSource ds = NpgsqlDataSource.Create(conn);
            await using NpgsqlConnection connection = await ds.OpenConnectionAsync();
            await using NpgsqlCommand command = NpgsqlSubstrateCommand.CreateFunction(
                connection,
                SubstrateFunctionNames.RecomposeContent,
                new object?[] { hashBytes, maxDepth });
            command.CommandTimeout = 60;

            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            object? raw = await command.ExecuteScalarAsync();
            sw.Stop();
            if (raw is not byte[] bytes)
            {
                Console.Error.WriteLine($"substrate.recompose_content returned {raw?.GetType().Name ?? "NULL"} (expected bytea)");
                Environment.ExitCode = 1;
                return;
            }

            if (outPath is not null)
            {
                await File.WriteAllBytesAsync(outPath, bytes);
                Console.Error.WriteLine($"Wrote {bytes.Length:N0} bytes to {outPath} in {sw.ElapsedMilliseconds}ms");
            }
            else
            {
                using Stream stdout = Console.OpenStandardOutput();
                await stdout.WriteAsync(bytes);
                Console.Error.WriteLine($"Wrote {bytes.Length:N0} bytes to stdout in {sw.ElapsedMilliseconds}ms");
            }
        }, connOpt, hashOpt, outOpt, depthOpt);

        return cmd;
    }
}
