using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Engine.Query;
using Npgsql;

namespace Hartonomous.Cli.Commands;

/// <summary>
/// Reads a recomposed safetensors file's <c>__metadata__.hartonomous_provenance_chain</c>
/// and verifies every (tensor, source, arena, μ) tuple via
/// <c>substrate.recompose_audit_walk</c>.
/// </summary>
internal sealed class AuditWalkCommand(NpgsqlDataSource dataSource)
{
    private static readonly string[] AuditWalkSafetensorsCandidates = ["model.safetensors"];

    public Command Build()
    {
        Option<string> outputDirOpt = new("--output-dir",
            "Path to a recomposed safetensors directory containing model.safetensors (or shards) with __metadata__ audit chain.");
        outputDirOpt.IsRequired = true;

        Command cmd = new("audit-walk",
            "Read a recomposed safetensors file's __metadata__.hartonomous_provenance_chain "
            + "and verify every (tensor, source, arena, μ) tuple via "
            + "substrate.recompose_audit_walk. Reports drift between claimed and actual μ.");
        cmd.AddOption(outputDirOpt);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            string outputDir = ctx.ParseResult.GetValueForOption(outputDirOpt)!;

            string? safetensorsPath = null;
            foreach (string candidate in AuditWalkSafetensorsCandidates)
            {
                string p = Path.Combine(outputDir, candidate);
                if (File.Exists(p)) { safetensorsPath = p; break; }
            }
            if (safetensorsPath is null)
            {
                foreach (string p in Directory.EnumerateFiles(outputDir, "model-*.safetensors"))
                {
                    safetensorsPath = p; break;
                }
            }
            if (safetensorsPath is null)
            {
                Console.Error.WriteLine($"No model.safetensors[-shard] found in {outputDir}");
                ctx.ExitCode = 2;
                return;
            }

            // Read 8-byte LE header length, then header JSON, extract __metadata__.
            await using FileStream fs = File.OpenRead(safetensorsPath);
            byte[] sizeBuf = new byte[8];
            int read = 0;
            while (read < 8)
            {
                int n = await fs.ReadAsync(sizeBuf.AsMemory(read, 8 - read), CancellationToken.None);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }
            ulong headerLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(sizeBuf);
            byte[] headerBytes = new byte[headerLen];
            read = 0;
            while (read < (int)headerLen)
            {
                int n = await fs.ReadAsync(headerBytes.AsMemory(read, (int)headerLen - read), CancellationToken.None);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            using JsonDocument doc = JsonDocument.Parse(headerBytes);
            if (!doc.RootElement.TryGetProperty("__metadata__", out JsonElement meta))
            {
                Console.Error.WriteLine("No __metadata__ block in safetensors header.");
                ctx.ExitCode = 3;
                return;
            }

            Console.WriteLine($"=== Audit chain for {safetensorsPath} ===");
            foreach (JsonProperty prop in meta.EnumerateObject())
            {
                Console.WriteLine($"  {prop.Name,-40} {prop.Value.GetString() ?? "(non-string)"}");
            }

            if (!meta.TryGetProperty("hartonomous_provenance_chain", out JsonElement chain))
            {
                Console.WriteLine();
                Console.WriteLine("(No hartonomous_provenance_chain key — audit walk skipped.)");
                return;
            }

            NpgsqlSubstrateQuery query = new(dataSource);
            IReadOnlyList<(int Idx, string TensorHashHex, double Claimed, double Actual, bool Verified, string Detail)> rows =
                await query.AuditWalkAsync(chain.GetRawText(), CancellationToken.None);

            int verifiedCount = 0;
            foreach ((int idx, string th, double claimed, double actual, bool verified, string detail) in rows)
            {
                if (verified)
                {
                    verifiedCount++;
                }

                string actualStr = double.IsNaN(actual) ? "NULL" : actual.ToString("F0", CultureInfo.InvariantCulture);
                Console.WriteLine($"  [{idx,4}] tensor={th[..16]}… claimed={claimed,10:F0} actual={actualStr,10} {(verified ? "✓" : "✗")} {detail}");
            }
            Console.WriteLine();
            Console.WriteLine($"Verified {verifiedCount}/{rows.Count} chain entries.");
            if (verifiedCount < rows.Count)
            {
                ctx.ExitCode = 1;
            }
        });

        return cmd;
    }
}
