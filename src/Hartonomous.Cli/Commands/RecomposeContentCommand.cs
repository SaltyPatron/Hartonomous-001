using System.CommandLine;
using System.IO;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Recomposers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Hartonomous.Cli.Commands;

/// <summary>
/// Recompose UTF-8 content from a substrate entity hash via the C# bulk-tier
/// <see cref="ContentRecomposer"/> (Gate 1 reopened item #36 in the
/// modular-wishing-koala plan).
///
/// <para>
/// Architecture: N+1 BULK PG queries (one geom-fetch + one hash-resolve per
/// composition tier; ~5–6 tiers for text), parsed entirely in C# against
/// the substrate's mantissa-packed LINESTRINGZM physicality manifest.
/// Codepoint leaves resolve via the embedded UCD blob (microsecond reverse
/// lookup); the previous PG-side recursive-CTE walker
/// (<c>substrate.recompose_content</c>) was wrong-shape and is gone.
/// </para>
///
/// <para>
/// Performance contract: sub-second for Bible-size documents (Moby Dick,
/// ~250K codepoint leaves, verified in TextRoundTripTests.MobyDick_FullRoundTrip).
/// </para>
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
            getDefaultValue: () => 32,
            description: "Maximum tier descent depth (defends against pathological cycles; default 32).");

        Command cmd = new(
            "recompose-content",
            "Reconstruct UTF-8 content from a substrate document/content entity hash via the C# "
            + "bulk-tier walker over LINESTRINGZM mantissa-packed child manifests. Sub-second for "
            + "Bible-size documents (~250K codepoint leaves).");
        cmd.AddOption(connOpt);
        cmd.AddOption(hashOpt);
        cmd.AddOption(outOpt);
        cmd.AddOption(depthOpt);

        cmd.SetHandler(async (string conn, string hashHex, string? outPath, int maxDepth) =>
        {
            byte[] hashBytes;
            try { hashBytes = Convert.FromHexString(hashHex); }
            catch (FormatException ex)  // BOUNDARY: CLI argument validation surfaces the parse error to stderr with exit code 2; not a substrate-internal catch.
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
            ContentRecomposer recomposer = new(ds, NullLogger<ContentRecomposer>.Instance);

            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            byte[] bytes = await recomposer.RecomposeAsync(new Hash32(hashBytes), maxDepth, CancellationToken.None);
            sw.Stop();

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
