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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hartonomous.Cli;

internal static class Program
{
    private static readonly string[] ConnAliases = ["--connection", "-c"];
    private static readonly string[] DirAliases = ["--dir", "-d"];
    private static readonly string[] AuditWalkSafetensorsCandidates = ["model.safetensors"];
    private static readonly string[] ManifestAliases = ["--manifest", "-m"];

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

        Command godel = BuildGodelCommand();
        root.AddCommand(godel);

        Command recall = BuildRecallCommand();
        root.AddCommand(recall);

        Command exportModel = BuildExportModelCommand();
        root.AddCommand(exportModel);

        Command compareModel = BuildCompareModelCommand();
        root.AddCommand(compareModel);

        Command querySubstrate = BuildQuerySubstrateCommand();
        root.AddCommand(querySubstrate);

        Command auditWalk = BuildAuditWalkCommand();
        root.AddCommand(auditWalk);

        Command health = BuildHealthCommand();
        root.AddCommand(health);

        Command bootstrap = BuildBootstrapCommand();
        root.AddCommand(bootstrap);

        return await root.InvokeAsync(args);
    }

    private static Command BuildBootstrapCommand()
    {
        Option<string> connOpt = new(ConnAliases, () => DefaultConnectionString(), "Connection string");
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
            string sql = Hartonomous.Cli.Migrations.MigrationFileLoader.LoadResolved(fullPath);

            await using NpgsqlDataSource ds = NpgsqlDataSource.Create(conn);
            await using NpgsqlConnection c = await ds.OpenConnectionAsync(CancellationToken.None);
            await using NpgsqlTransaction tx = await c.BeginTransactionAsync(CancellationToken.None);
            try
            {
                await using NpgsqlCommand command = new(sql, c, tx);
                command.CommandTimeout = 600; // bootstrap can apply ~hundreds of statements
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

    private static Command BuildQuerySubstrateCommand()
    {
        Option<string> connOpt = new(ConnAliases, () => DefaultConnectionString(), "Connection string");
        Option<string> archHashOpt = new("--arch-hash", "model_architecture entity BLAKE3 hash (64 hex chars)");
        archHashOpt.IsRequired = true;

        Command cmd = new("query-substrate",
            "Print the universal substrate inventory for an ingested model: tensor count, "
            + "distinct layers/heads/MoE-experts, vocab recovered, firefly count, refinement preview "
            + "rows. Calls substrate.model_inventory + substrate.model_vocab_recovered + "
            + "substrate.refinement_summary. Drives the future model-config UI's model card.");
        cmd.AddOption(connOpt);
        cmd.AddOption(archHashOpt);

        cmd.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            string conn = ctx.ParseResult.GetValueForOption(connOpt)!;
            string archHashHex = ctx.ParseResult.GetValueForOption(archHashOpt)!;
            byte[] archHash = Convert.FromHexString(archHashHex);

            await using NpgsqlDataSource ds = NpgsqlDataSource.Create(conn);
            await using NpgsqlConnection c = await ds.OpenConnectionAsync(CancellationToken.None);

            Console.WriteLine($"=== Substrate inventory for arch={archHashHex[..16]}… ===");

            // model_inventory
            await using (NpgsqlCommand inv = new(
                "SELECT metric_code, metric_value, metric_detail FROM substrate.model_inventory($1) ORDER BY metric_code", c))
            {
                inv.Parameters.Add(new NpgsqlParameter { Value = archHash });
                await using NpgsqlDataReader r = await inv.ExecuteReaderAsync(CancellationToken.None);
                while (await r.ReadAsync(CancellationToken.None))
                {
                    string code = r.GetString(0);
                    long val = r.GetInt64(1);
                    string detail = r.IsDBNull(2) ? "" : "  (" + r.GetString(2) + ")";
                    Console.WriteLine($"  {code,-32} {val,15:N0}{detail}");
                }
            }

            // model_vocab_recovered
            await using (NpgsqlCommand vc = new("SELECT substrate.model_vocab_recovered($1)", c))
            {
                vc.Parameters.Add(new NpgsqlParameter { Value = archHash });
                object? result = await vc.ExecuteScalarAsync(CancellationToken.None);
                Console.WriteLine($"  {"vocab_recovered",-32} {Convert.ToInt64(result, CultureInfo.InvariantCulture),15:N0}");
            }

            // refinement_summary (top 20 by delta_mu)
            Console.WriteLine();
            Console.WriteLine($"=== Refinement summary (top 20 by Δμ in corroboration_strength) ===");
            await using (NpgsqlCommand rs = new(@"
                SELECT edge_type_code, source_only_mu, consensus_mu, delta_mu, above_threshold
                  FROM substrate.refinement_summary($1, 'corroboration_strength')
                 ORDER BY delta_mu DESC NULLS LAST
                 LIMIT 20", c))
            {
                rs.Parameters.Add(new NpgsqlParameter { Value = archHash });
                await using NpgsqlDataReader r = await rs.ExecuteReaderAsync(CancellationToken.None);
                int row = 0;
                while (await r.ReadAsync(CancellationToken.None))
                {
                    row++;
                    string edgeType = r.GetString(0);
                    double srcOnly = r.IsDBNull(1) ? 0 : r.GetDouble(1);
                    double consensus = r.IsDBNull(2) ? 0 : r.GetDouble(2);
                    double delta = r.IsDBNull(3) ? 0 : r.GetDouble(3);
                    bool above = r.IsDBNull(4) ? false : r.GetBoolean(4);
                    Console.WriteLine($"  {edgeType,-32} src_only={srcOnly,12:F0} consensus={consensus,12:F0} Δ={delta,+12:F0} {(above ? "✓" : " ")}");
                }
                if (row == 0)
                {
                    Console.WriteLine("  (no refinement summary rows; cross-source corroboration arena empty)");
                }
            }
        });

        return cmd;
    }

    private static Command BuildAuditWalkCommand()
    {
        Option<string> connOpt = new(ConnAliases, () => DefaultConnectionString(), "Connection string");
        Option<string> outputDirOpt = new("--output-dir", "Path to a recomposed safetensors directory containing model.safetensors (or shards) with __metadata__ audit chain.");
        outputDirOpt.IsRequired = true;

        Command cmd = new("audit-walk",
            "Read a recomposed safetensors file's __metadata__.hartonomous_provenance_chain "
            + "and verify every (tensor, source, arena, μ) tuple via "
            + "substrate.recompose_audit_walk. Reports drift between claimed and actual μ.");
        cmd.AddOption(connOpt);
        cmd.AddOption(outputDirOpt);

        cmd.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            string conn = ctx.ParseResult.GetValueForOption(connOpt)!;
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
                if (n == 0) { break; }
                read += n;
            }
            ulong headerLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(sizeBuf);
            byte[] headerBytes = new byte[headerLen];
            read = 0;
            while (read < (int)headerLen)
            {
                int n = await fs.ReadAsync(headerBytes.AsMemory(read, (int)headerLen - read), CancellationToken.None);
                if (n == 0) { break; }
                read += n;
            }

            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(headerBytes);
            if (!doc.RootElement.TryGetProperty("__metadata__", out var meta))
            {
                Console.Error.WriteLine("No __metadata__ block in safetensors header.");
                ctx.ExitCode = 3;
                return;
            }

            Console.WriteLine($"=== Audit chain for {safetensorsPath} ===");
            foreach (var prop in meta.EnumerateObject())
            {
                Console.WriteLine($"  {prop.Name,-40} {prop.Value.GetString() ?? "(non-string)"}");
            }

            if (!meta.TryGetProperty("hartonomous_provenance_chain", out var chain))
            {
                Console.WriteLine();
                Console.WriteLine("(No hartonomous_provenance_chain key — audit walk skipped.)");
                return;
            }

            await using NpgsqlDataSource ds = NpgsqlDataSource.Create(conn);
            await using NpgsqlConnection c = await ds.OpenConnectionAsync(CancellationToken.None);
            await using NpgsqlCommand walk = new(
                "SELECT chain_index, encode(tensor_hash, 'hex'), claimed_mu, actual_mu, verified, detail "
                + "FROM substrate.recompose_audit_walk($1::jsonb)", c);
            walk.Parameters.Add(new NpgsqlParameter { Value = chain.GetRawText() });
            await using NpgsqlDataReader r = await walk.ExecuteReaderAsync(CancellationToken.None);

            int verifiedCount = 0;
            int totalCount = 0;
            while (await r.ReadAsync(CancellationToken.None))
            {
                totalCount++;
                int idx = r.GetInt32(0);
                string th = r.GetString(1);
                double claimed = r.IsDBNull(2) ? 0 : r.GetDouble(2);
                double actual = r.IsDBNull(3) ? double.NaN : r.GetDouble(3);
                bool verified = r.GetBoolean(4);
                string detail = r.IsDBNull(5) ? "" : r.GetString(5);
                if (verified) { verifiedCount++; }
                Console.WriteLine($"  [{idx,4}] tensor={th[..16]}… claimed={claimed,10:F0} actual={(double.IsNaN(actual) ? "NULL" : actual.ToString("F0", CultureInfo.InvariantCulture)),10} {(verified ? "✓" : "✗")} {detail}");
            }
            Console.WriteLine();
            Console.WriteLine($"Verified {verifiedCount}/{totalCount} chain entries.");
            if (verifiedCount < totalCount) { ctx.ExitCode = 1; }
        });

        return cmd;
    }

    private static Command BuildHealthCommand()
    {
        Option<string> connOpt = new(
            aliases: ConnAliases,
            getDefaultValue: DefaultConnectionString,
            description: "Npgsql connection string.");

        Command health = new("health",
            "One-call substrate state probe via substrate.health_summary(). " +
            "Counts every entity / edge / physicality / significance row by " +
            "type code, mean μ per arena, and database storage size. " +
            "Hash-as-PK aware — works against the post-0015 schema.");
        health.AddOption(connOpt);
        health.SetHandler(async (string conn) =>
        {
            await using Npgsql.NpgsqlDataSource ds = Npgsql.NpgsqlDataSource.Create(conn);
            await using Npgsql.NpgsqlConnection c = await ds.OpenConnectionAsync(CancellationToken.None);
            await using Npgsql.NpgsqlCommand cmd = new("SELECT substrate.health_summary()", c);
            object? result = await cmd.ExecuteScalarAsync(CancellationToken.None);
            if (result is null or DBNull)
            {
                Console.Error.WriteLine("substrate.health_summary() returned NULL.");
                return;
            }

            string json = result.ToString() ?? "{}";
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            System.Text.Json.JsonElement root = doc.RootElement;

            Console.WriteLine("=== Substrate Health ===");
            Console.WriteLine($"  Total entities:      {root.GetProperty("totalEntities").GetInt64():N0}");
            Console.WriteLine($"  Total edges:         {root.GetProperty("totalEdges").GetInt64():N0}");
            Console.WriteLine($"  Total edge members:  {root.GetProperty("totalEdgeMembers").GetInt64():N0}");
            Console.WriteLine($"  Total physicalities: {root.GetProperty("totalPhysicalities").GetInt64():N0}");
            Console.WriteLine($"  Entity significance: {root.GetProperty("totalEntitySig").GetInt64():N0}");
            Console.WriteLine($"  Edge significance:   {root.GetProperty("totalEdgeSig").GetInt64():N0}");
            Console.WriteLine($"  Storage:             {root.GetProperty("storageSizeBytes").GetInt64():N0} bytes");

            PrintObject(root, "entitiesByType", "Entities by type");
            PrintObject(root, "edgesByType", "Edges by type");
            PrintObject(root, "entityMeanMuByArena", "Entity mean μ by arena");
            PrintObject(root, "edgeMeanMuByArena", "Edge mean μ by arena");
        }, connOpt);

        return health;
    }

    private static void PrintObject(System.Text.Json.JsonElement root, string property, string title)
    {
        if (!root.TryGetProperty(property, out System.Text.Json.JsonElement obj))
        {
            return;
        }
        if (obj.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return;
        }
        Console.WriteLine();
        Console.WriteLine($"=== {title} ===");
        bool any = false;
        foreach (System.Text.Json.JsonProperty p in obj.EnumerateObject())
        {
            string val = p.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                ? p.Value.ToString()
                : p.Value.ToString();
            Console.WriteLine($"  {p.Name,-30} {val}");
            any = true;
        }
        if (!any)
        {
            Console.WriteLine("  (none)");
        }
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
        Option<string> archHashOpt = new("--arch-hash", "model_architecture entity BLAKE3 hash (64 hex chars)");
        archHashOpt.IsRequired = true;
        Option<string> outputOpt = new("--output", "Output safetensors path (file for single-shard, directory when --shard is set)");
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
        Option<string?> recipeOpt = new("--recipe",
            "Path to a recipe JSON (Mode/RefinementPolicy/QuantizationPolicy/etc.). Overrides individual flags when present.");
        Option<double> noiseFloorOpt = new("--noise-floor",
            getDefaultValue: () => 0.0,
            description: "Per-element |x| below this is zeroed at recompose (Law #11 sparsity enforcement).");
        Option<bool> shardOpt = new("--shard",
            getDefaultValue: () => false,
            description: "Multi-shard output via ShardSplitter; --output is treated as a directory; emits model.safetensors.index.json.");
        Option<long> maxShardBytesOpt = new("--max-shard-bytes",
            getDefaultValue: () => 5_000_000_000L,
            description: "Maximum bytes per shard when --shard is set (default 5 GB).");
        Option<bool> includeProvenanceOpt = new("--include-provenance",
            getDefaultValue: () => true,
            description: "Emit __metadata__ audit chain (recipe id, recomposer version, mode, policies).");

        Command exportModel = new("export-model",
            "Recompose a substrate model_architecture into a safetensors file. "
            + "With filter options the export becomes a distillation query (architecture.md "
            + "\"Distillation = WHERE clause\"). With --recipe or --shard the full V1 recipe DSL "
            + "applies (mode, refinement policy, quantization policy, sharding, audit metadata).");
        exportModel.AddOption(connOpt);
        exportModel.AddOption(archHashOpt);
        exportModel.AddOption(outputOpt);
        exportModel.AddOption(sourceIdsOpt);
        exportModel.AddOption(minMuOpt);
        exportModel.AddOption(contextOpt);
        exportModel.AddOption(limitOpt);
        exportModel.AddOption(recipeOpt);
        exportModel.AddOption(noiseFloorOpt);
        exportModel.AddOption(shardOpt);
        exportModel.AddOption(maxShardBytesOpt);
        exportModel.AddOption(includeProvenanceOpt);

        exportModel.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            string conn = ctx.ParseResult.GetValueForOption(connOpt)!;
            string archHashHex = ctx.ParseResult.GetValueForOption(archHashOpt)!;
            byte[] archHashBytes = Convert.FromHexString(archHashHex);
            Hartonomous.Core.Ingestion.EntityHandle archHandle = new(archHashBytes, "model_architecture");
            string output = ctx.ParseResult.GetValueForOption(outputOpt)!;
            long[] sourceIds = ctx.ParseResult.GetValueForOption(sourceIdsOpt) ?? [];
            double? minMu = ctx.ParseResult.GetValueForOption(minMuOpt);
            string? context = ctx.ParseResult.GetValueForOption(contextOpt);
            int? limit = ctx.ParseResult.GetValueForOption(limitOpt);
            string? recipePath = ctx.ParseResult.GetValueForOption(recipeOpt);
            double noiseFloor = ctx.ParseResult.GetValueForOption(noiseFloorOpt);
            bool shard = ctx.ParseResult.GetValueForOption(shardOpt);
            long maxShardBytes = ctx.ParseResult.GetValueForOption(maxShardBytesOpt);
            bool includeProvenance = ctx.ParseResult.GetValueForOption(includeProvenanceOpt);

            await using NpgsqlDataSource ds = NpgsqlDataSource.Create(conn);
            Hartonomous.Engine.Data.NpgsqlEntityReader entityReader = new(ds);
            Hartonomous.Engine.Data.NpgsqlPhysicalityReader physReader = new(ds);
            Hartonomous.Engine.Query.NpgsqlSubstrateQuery query = new(ds);

            Hartonomous.Recomposers.SafetensorsRecomposer recomposer = new(
                entityReader, entityReader, physReader, query);

            bool filtered = sourceIds.Length > 0 || minMu.HasValue || !string.IsNullOrEmpty(context) || limit.HasValue;

            // Build options. --recipe path takes precedence; individual flags
            // override otherwise.
            Hartonomous.Core.Recomposition.RecompositionOptions opts;
            if (!string.IsNullOrEmpty(recipePath))
            {
                opts = LoadRecipeOptions(recipePath, noiseFloor, maxShardBytes, includeProvenance);
                Console.WriteLine($"=== Recipe: {recipePath} ===");
            }
            else
            {
                opts = new()
                {
                    MaxDepth = 20,
                    NoiseFloor = noiseFloor,
                    MaxShardBytes = maxShardBytes,
                    IncludeProvenance = includeProvenance,
                };
            }

            Console.WriteLine($"=== {(filtered ? "Distilling" : "Exporting")} model_architecture {archHandle} → {output} ===");
            if (filtered)
            {
                if (sourceIds.Length > 0) { Console.WriteLine($"  --source-id={string.Join(',', sourceIds)}"); }
                if (minMu.HasValue) { Console.WriteLine($"  --min-significance={minMu.Value}"); }
                if (!string.IsNullOrEmpty(context)) { Console.WriteLine($"  --context={context}"); }
                if (limit.HasValue) { Console.WriteLine($"  --limit={limit.Value}"); }
            }
            if (noiseFloor > 0)            { Console.WriteLine($"  --noise-floor={noiseFloor}"); }
            if (opts.SignificanceThreshold > 0) { Console.WriteLine($"  significance_threshold={opts.SignificanceThreshold}"); }
            if (shard)                     { Console.WriteLine($"  --shard --max-shard-bytes={maxShardBytes:N0}"); }
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

            if (shard)
            {
                Directory.CreateDirectory(output);
                await recomposer.RecomposeToShardsAsync(archHandle, opts, output, CancellationToken.None);
                sw.Stop();
                long total = 0;
                int shardCount = 0;
                foreach (string shardFile in Directory.EnumerateFiles(output, "*.safetensors"))
                {
                    total += new FileInfo(shardFile).Length;
                    shardCount++;
                }
                Console.WriteLine($"Shards written: {shardCount}");
                Console.WriteLine($"Total bytes: {total:N0}");
                Console.WriteLine($"Index: {Path.Combine(output, "model.safetensors.index.json")}");
                Console.WriteLine($"Elapsed: {sw.Elapsed.TotalSeconds:F1}s");
                return;
            }

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
                file = await recomposer.RecomposeFilteredAsync(archHandle, filter, opts, CancellationToken.None);
            }
            else
            {
                file = await recomposer.RecomposeAsync(archHandle, opts, CancellationToken.None);
            }

            // Build audit metadata when --include-provenance is set, mirroring
            // RecomposeToStreamAsync's path.
            IReadOnlyDictionary<string, string>? auditMetadata = null;
            if (includeProvenance)
            {
                auditMetadata = new Dictionary<string, string>
                {
                    ["hartonomous_recomposer_version"] = "v1",
                    ["hartonomous_mode"] = opts.Mode.ToString(),
                    ["hartonomous_refinement_policy"] = opts.RefinementPolicy.ToString(),
                    ["hartonomous_quantization_policy"] = opts.QuantizationPolicy.ToString(),
                    ["hartonomous_lora_policy"] = opts.LoraPolicy.ToString(),
                    ["hartonomous_noise_floor"] = noiseFloor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                if (!string.IsNullOrEmpty(opts.RecipeId))
                {
                    ((Dictionary<string, string>)auditMetadata)["hartonomous_recipe_id"] = opts.RecipeId;
                }
            }

            await using (FileStream fs = File.Create(output))
            {
                await Hartonomous.Recomposers.SafetensorsWriter.WriteAsync(file, fs, auditMetadata, CancellationToken.None);
            }

            sw.Stop();
            FileInfo fi = new(output);
            Console.WriteLine($"Tensors written: {file.Tensors.Count}");
            Console.WriteLine($"File size: {fi.Length:N0} bytes");
            Console.WriteLine($"Elapsed: {sw.Elapsed.TotalSeconds:F1}s");
        });

        return exportModel;
    }

    private static Hartonomous.Core.Recomposition.RecompositionOptions LoadRecipeOptions(
        string recipePath, double cliNoiseFloor, long cliMaxShardBytes, bool cliIncludeProvenance)
    {
        if (!File.Exists(recipePath))
        {
            throw new FileNotFoundException($"Recipe file not found: {recipePath}");
        }
        string json = File.ReadAllText(recipePath);
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        System.Text.Json.JsonElement root = doc.RootElement;

        Hartonomous.Core.Recomposition.RecompositionMode mode =
            root.TryGetProperty("mode", out var m) && Enum.TryParse(m.GetString(),
                out Hartonomous.Core.Recomposition.RecompositionMode parsedMode)
                ? parsedMode
                : Hartonomous.Core.Recomposition.RecompositionMode.Refinement;

        Hartonomous.Core.Recomposition.RefinementPolicy policy =
            root.TryGetProperty("refinement_policy", out var rp) && Enum.TryParse(rp.GetString(),
                out Hartonomous.Core.Recomposition.RefinementPolicy parsedPolicy)
                ? parsedPolicy
                : Hartonomous.Core.Recomposition.RefinementPolicy.SourceOnly;

        Hartonomous.Core.Recomposition.QuantizationPolicy qp =
            root.TryGetProperty("quantization_policy", out var qpe) && Enum.TryParse(qpe.GetString(),
                out Hartonomous.Core.Recomposition.QuantizationPolicy parsedQp)
                ? parsedQp
                : Hartonomous.Core.Recomposition.QuantizationPolicy.Preserve;

        Hartonomous.Core.Recomposition.LoraPolicy lp =
            root.TryGetProperty("lora_policy", out var lpe) && Enum.TryParse(lpe.GetString(),
                out Hartonomous.Core.Recomposition.LoraPolicy parsedLp)
                ? parsedLp
                : Hartonomous.Core.Recomposition.LoraPolicy.None;

        double noiseFloor = root.TryGetProperty("noise_floor", out var nf) && nf.ValueKind == System.Text.Json.JsonValueKind.Number
            ? nf.GetDouble()
            : cliNoiseFloor;

        double sigThreshold = root.TryGetProperty("significance_threshold", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.Number
            ? st.GetDouble()
            : 0.0;

        long maxShardBytes = root.TryGetProperty("max_shard_bytes", out var msb) && msb.ValueKind == System.Text.Json.JsonValueKind.Number
            ? msb.GetInt64()
            : cliMaxShardBytes;

        string? requantizeTarget = root.TryGetProperty("requantize_target", out var rt) ? rt.GetString() : null;
        string? provenanceFilter = root.TryGetProperty("provenance_filter", out var pf) ? pf.GetString() : null;

        List<string>? arenaCodes = null;
        if (root.TryGetProperty("arena_codes", out var ac) && ac.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            arenaCodes = [];
            foreach (var ace in ac.EnumerateArray())
            {
                string? s = ace.GetString();
                if (!string.IsNullOrEmpty(s)) { arenaCodes.Add(s); }
            }
        }

        string? targetArchSpecJson = root.TryGetProperty("target_arch_spec", out var tas)
            ? tas.GetRawText()
            : null;

        // Compute recipe content hash for replayable identity.
        Hartonomous.Core.Compute.IComputeFacade compute = Hartonomous.Core.Compute.ComputeFacade.Instance;
        Hartonomous.Core.Recomposition.RecompositionOptions opts = new()
        {
            MaxDepth = 20,
            Mode = mode,
            RefinementPolicy = policy,
            QuantizationPolicy = qp,
            LoraPolicy = lp,
            RequantizeTarget = requantizeTarget,
            ProvenanceFilter = provenanceFilter,
            ArenaCodes = arenaCodes,
            SignificanceThreshold = sigThreshold,
            NoiseFloor = noiseFloor,
            MaxShardBytes = maxShardBytes,
            IncludeProvenance = cliIncludeProvenance,
            TargetArchSpecJson = targetArchSpecJson,
        };
        string recipeId = Hartonomous.Recomposers.RecipeContentHash.Compute(opts, compute);
        return opts with { RecipeId = recipeId };
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
        Option<bool> allowDriftOpt = new(
            "--allow-drift",
            getDefaultValue: () => false,
            description: "Proceed past checksum drift on previously-applied migrations. Drift is logged loudly but not fatal. Use when SOURCE files were modified after apply but DB-side function/table definitions remain correct (e.g. CREATE OR REPLACE'd in-flight). Verify substrate.health_summary() afterwards.");
        up.AddOption(allowDriftOpt);
        up.SetHandler(async (string conn, string dir, bool allowDrift) =>
        {
            await using NpgsqlDataSource ds = NpgsqlDataSource.Create(conn);
            MigrationRunner runner = new(new NpgsqlMigrationStore(ds), dir);
            await runner.UpAsync(CancellationToken.None, allowDrift);
        }, connOpt, dirOpt, allowDriftOpt);

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
        // Log level resolves from HARTONOMOUS_LOG_LEVEL env var (Trace, Debug,
        // Information, Warning, Error). Defaults to Information for normal
        // runs; set to Trace to see the per-batch sub-step lines from
        // NpgsqlIngestionPipeline (which step preceded any PG crash).
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

        // Load enterprise-grade per-decomposer configuration. The CLI's
        // legacy `--source` argument now overrides DataRoot at runtime if
        // provided; per-decomposer paths come from appsettings.json so each
        // decomposer reads from its actual data location, not a hardcoded
        // subdirectory pattern combined with one shared --source root.
        Microsoft.Extensions.Configuration.IConfigurationRoot cfgRoot =
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                .AddEnvironmentVariables(prefix: "HARTONOMOUS__")
                .Build();
        Hartonomous.Cli.Configuration.HartonomousOptions opts =
            cfgRoot.GetSection("Hartonomous").Get<Hartonomous.Cli.Configuration.HartonomousOptions>()
            ?? new Hartonomous.Cli.Configuration.HartonomousOptions();

        // CLI `--source` (when supplied non-empty) overrides DataRoot.
        // Connection string passed in already wins over config.
        if (!string.IsNullOrWhiteSpace(sourceRoot))
        {
            opts.DataRoot = sourceRoot;
        }
        string dataRoot = opts.DataRoot;

        // Local helper: absolute path = used as-is; relative = resolved
        // against DataRoot. Keeps the config flexible (devs can pin one
        // decomposer to /opt/special_data without affecting siblings).
        string Resolve(string p) =>
            string.IsNullOrEmpty(p) ? dataRoot
            : Path.IsPathRooted(p) ? p
            : Path.Combine(dataRoot, p);

        DecomposerConfig ucdConfig = new()
        {
            SourceDirectory = Resolve(opts.Decomposers.Ucd.SourcePath),
            ConnectionString = conn,
        };
        DecomposerConfig iso639Config = new()
        {
            SourceDirectory = Resolve(opts.Decomposers.Iso639.SourcePath),
            ConnectionString = conn,
        };
        DecomposerConfig wordnetConfig = new()
        {
            SourceDirectory = Resolve(opts.Decomposers.WordNet.SourcePath),
            ConnectionString = conn,
        };
        DecomposerConfig omwConfig = new()
        {
            SourceDirectory = Resolve(opts.Decomposers.Omw.SourcePath),
            ConnectionString = conn,
            LanguageFilter = opts.Decomposers.Omw.LanguageFilter,
        };
        DecomposerConfig udConfig = new()
        {
            SourceDirectory = Resolve(opts.Decomposers.Ud.SourcePath),
            ConnectionString = conn,
            LanguageFilter = opts.Decomposers.Ud.LanguageFilter,
        };
        DecomposerConfig modelConfig = new()
        {
            SourceDirectory = Resolve(opts.Decomposers.Safetensors.HubPath),
            ConnectionString = conn,
            ModelFilter = opts.Decomposers.Safetensors.ModelFilter is { Length: > 0 }
                ? opts.Decomposers.Safetensors.ModelFilter
                : null,
        };
        DecomposerConfig wiktionaryConfig = new()
        {
            SourceDirectory = Resolve(opts.Decomposers.Wiktionary.SourcePath),
            ConnectionString = conn,
            LanguageFilter = opts.Decomposers.Wiktionary.LanguageFilter,
        };
        DecomposerConfig tatoebaConfig = new()
        {
            SourceDirectory = Resolve(opts.Decomposers.Tatoeba.SourcePath),
            ConnectionString = conn,
            LanguageFilter = opts.Decomposers.Tatoeba.LanguageFilter,
        };
        string textSourceDir = Resolve(opts.Decomposers.Text.SourcePath);

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
            [Phase.Iso639] = [new Iso639Decomposer(iso639Config, logFactory.CreateLogger<Iso639Decomposer>(), cpProps, refDataReader, junctionWriter, refDataWriter)],
            [Phase.WordNetOmw] =
            [
                new WordNetDecomposer(wordnetConfig, logFactory.CreateLogger<WordNetDecomposer>(), cpProps, refDataReader, junctionWriter, refDataWriter),
                new OmwDecomposer(omwConfig, logFactory.CreateLogger<OmwDecomposer>(), cpProps, refDataReader, junctionWriter, refDataWriter),
            ],
            [Phase.UniversalDeps] =
            [
                new UdDecomposer(udConfig, logFactory.CreateLogger<UdDecomposer>(), cpProps, refDataReader, junctionWriter, refDataWriter),
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
        // Streaming pipeline: continuous record flow into substrate.staging_*
        // tables, drained by background flush worker. Replaces the per-batch
        // staging/flush dance that crashed PG with stack canary failures.
        // Implements IIngestionPipeline as a compatibility shim, so existing
        // decomposers that build IngestionBatch keep working — the shim
        // unfolds each batch into per-record emits across the channels.
        await using StreamingIngestionPipeline pipeline = new(conn, refDataReader, logFactory.CreateLogger<StreamingIngestionPipeline>());

        // Background flush worker: drains substrate.staging_* → substrate.*
        // continuously on its own connection. Decoupled from producer
        // transactions; never inside a per-batch transaction.
        await using NpgsqlDataSource flushDs = NpgsqlDataSource.Create(conn);
        await using StagingFlushWorker flushWorker = new(flushDs, logFactory.CreateLogger<StagingFlushWorker>());
        await flushWorker.StartAsync();

        // Hard barrier: drain any pre-existing staging residue (from a prior
        // CLI run that died before its catch-up drain completed) BEFORE
        // producers are allowed to emit new content. Throws on unrecoverable
        // PG failure — proceeding under that condition would compound the
        // loss. Staging is persistent across CLI restarts (migration 0019),
        // so this is the recovery path for any prior shutdown drain that
        // didn't finish cleanly.
        await flushWorker.DrainPreExistingResidueAsync(ct);

        // Background significance primer: drains substrate.edge → substrate.edge_significance
        // per arena. The synchronous prime call inside producer batches that
        // crashed PG is GONE — priming is now a separate background loop.
        await using BackgroundSignificancePrimer primer = new(flushDs, logFactory.CreateLogger<BackgroundSignificancePrimer>());
        await primer.StartAsync();
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
                            // Exit non-zero so wrapper scripts (scripts/seed/*.ps1)
                            // see the dependency failure. Plain `return` exits 0
                            // and silently masks the error — bug observed when
                            // every seed phase reported "OK" while UcdUca dep
                            // was duplicate-keying on substrate.codepoint_property.
                            Environment.ExitCode = 1;
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
            if (result.Status == PhaseStatus.Failed)
            {
                Environment.ExitCode = 1;
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

        // Flush all in-flight channel contents to staging before we tear down.
        // Background drain worker continues after that until staging is empty.
        await pipeline.FlushAsync(ct);

        // Stop background workers so the final drain pass lands in substrate
        // and the primer catches up before the process exits.
        await primer.StopAsync();
        await flushWorker.StopAsync();

        StreamingPipelineStats sStats = pipeline.Stats;
        StagingFlushStats fStats = flushWorker.Stats;
        SignificancePrimerStats pStats = primer.Stats;
        Console.WriteLine($"\nStreaming pipeline emitted: {sStats.EntitiesEmitted:N0} entities, {sStats.EdgesEmitted:N0} edges, {sStats.EdgeMembersEmitted:N0} edge_members, {sStats.JunctionsEmitted:N0} junctions, {sStats.PhysicalitiesEmitted:N0} physicalities, {sStats.SequencesEmitted:N0} sequences ({sStats.CopyCommits:N0} COPY commits, {sStats.CopyErrors:N0} errors)");
        string fStatsBreakdown = string.Join(", ",
            fStats.RowsDrainedByFunction.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value:N0}"));
        Console.WriteLine($"Background flush drained:    {fStats.TotalRowsDrained:N0} total rows ({fStats.IdleCycles:N0} idle cycles) — {fStatsBreakdown}");
        Console.WriteLine($"Significance primer:         {pStats.EdgesPrimed:N0} edges primed across {pStats.ArenaCount} arenas ({pStats.IdleCycles:N0} idle cycles)");

        // If shutdown drain left rows in substrate.staging_*, the substrate is
        // incomplete relative to what producers emitted. monitor.phase_status
        // may report phases as "completed" but downstream consumers will see
        // missing entities / sequences. Surface this as a non-zero exit code
        // so orchestration scripts halt instead of trusting the run. The next
        // CLI invocation will eagerly drain the residue (DrainPreExistingResidueAsync)
        // before producing new content — staging is persistent across runs
        // (migration 0019).
        if (!flushWorker.LastShutdownDrainCompleted)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("ERROR: shutdown drain did not empty substrate.staging_*.");
            Console.Error.WriteLine("       Substrate is incomplete relative to producer emissions.");
            Console.Error.WriteLine("       Re-run the CLI: startup will drain the residue before any new phase work.");
            Environment.ExitCode = 1;
        }
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

            using Microsoft.Extensions.Logging.ILoggerFactory lf =
                Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information));

            // Codepoint property cache — subset to only what the prompt
            // actually contains (per AP-7: don't full-load 303k codepoints
            // for an inference path that needs ~50).
            HashSet<int> promptCodepoints = new();
            foreach (System.Text.Rune rune in text.EnumerateRunes())
            {
                promptCodepoints.Add(rune.Value);
            }
            Hartonomous.Engine.Text.NpgsqlCodepointPropertiesCache codepointCache =
                await Hartonomous.Engine.Text.NpgsqlCodepointPropertiesCache.LoadForCodepointsAsync(
                    conn, promptCodepoints, lf.CreateLogger<Hartonomous.Engine.Text.NpgsqlCodepointPropertiesCache>(),
                    CancellationToken.None);

            // Same pipeline machinery the seed phases use. Prompts go through
            // the SAME path: producer batch → channels → staging → substrate.
            // Pipeline handles arbitrary size (one word, Moby Dick, multi-MB).
            await using Hartonomous.Engine.Ingestion.StreamingIngestionPipeline pipeline = new(
                conn,
                refReader,
                lf.CreateLogger<Hartonomous.Engine.Ingestion.StreamingIngestionPipeline>());
            await using Npgsql.NpgsqlDataSource flushDs = Npgsql.NpgsqlDataSource.Create(conn);
            await using Hartonomous.Engine.Ingestion.StagingFlushWorker flushWorker =
                new(flushDs, lf.CreateLogger<Hartonomous.Engine.Ingestion.StagingFlushWorker>());
            await flushWorker.StartAsync();
            // Drain any pre-existing residue before we emit the prompt — same
            // hard barrier the seed phases use.
            await flushWorker.DrainPreExistingResidueAsync(CancellationToken.None);

            Hartonomous.Engine.Inference.SubstrateInferenceEngine engine = new(
                ds, pipeline, codepointCache, refReader,
                lf.CreateLogger<Hartonomous.Engine.Inference.SubstrateInferenceEngine>());

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
            Console.WriteLine($"  prompt seeds:     {result.Seeds.Count} (text_composition root, A* expands word_form children)");
            Console.WriteLine($"  distinct targets: {result.NodesVisited}");
            Console.WriteLine($"  elapsed:          {sw.Elapsed.TotalMilliseconds:F1} ms");

            // Stop the flush worker cleanly so any remaining staging drains.
            await flushWorker.StopAsync();
        });

        return query;
    }

    private static Command BuildRecallCommand()
    {
        Option<string> connOpt = new("--connection", description: "Postgres connection string. Falls back to HARTONOMOUS_DB.")
        {
            IsRequired = false,
        };
        connOpt.SetDefaultValueFactory(() => DefaultConnectionString());

        Argument<string[]> textArg = new("text",
            "Prompt for direct recall. The brain's primary operation: substrate decomposition → seed activation → cross-arena A* → recompose best target. No goal decomposition, no Reflexion retry, no synthesis. Most prompts resolve here.");
        textArg.Arity = ArgumentArity.OneOrMore;

        Command recall = new("recall",
            "Direct substrate recall: prompt → best substrate-grounded answer in one shot. The brain's most common access pattern. Use 'godel' instead for compound prompts that need goal decomposition.");
        recall.AddOption(connOpt);
        recall.AddArgument(textArg);

        recall.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            string conn = ctx.ParseResult.GetValueForOption(connOpt)!;
            string[] textParts = ctx.ParseResult.GetValueForArgument(textArg);
            string text = string.Join(' ', textParts);

            await using Npgsql.NpgsqlDataSource ds = Npgsql.NpgsqlDataSource.Create(conn);
            Hartonomous.Engine.Data.NpgsqlReferenceDataReader refReader = new(ds);
            using Microsoft.Extensions.Logging.ILoggerFactory lf =
                Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information));

            // Subset codepoint cache to prompt — per AP-7.
            HashSet<int> promptCodepoints = new();
            foreach (System.Text.Rune rune in text.EnumerateRunes())
            {
                promptCodepoints.Add(rune.Value);
            }
            Hartonomous.Engine.Text.NpgsqlCodepointPropertiesCache codepointCache =
                await Hartonomous.Engine.Text.NpgsqlCodepointPropertiesCache.LoadForCodepointsAsync(
                    conn, promptCodepoints, lf.CreateLogger<Hartonomous.Engine.Text.NpgsqlCodepointPropertiesCache>(),
                    CancellationToken.None);

            await using Hartonomous.Engine.Ingestion.StreamingIngestionPipeline pipeline = new(
                conn, refReader,
                lf.CreateLogger<Hartonomous.Engine.Ingestion.StreamingIngestionPipeline>());
            await using Npgsql.NpgsqlDataSource flushDs = Npgsql.NpgsqlDataSource.Create(conn);
            await using Hartonomous.Engine.Ingestion.StagingFlushWorker flushWorker =
                new(flushDs, lf.CreateLogger<Hartonomous.Engine.Ingestion.StagingFlushWorker>());
            await flushWorker.StartAsync();
            await flushWorker.DrainPreExistingResidueAsync(CancellationToken.None);

            // Step 0: ingest prompt as substrate content.
            Hartonomous.Core.Ingestion.IIngestionBatch batch = pipeline.CreateBatch();
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text);
            Hartonomous.Core.Text.TextDecomposeResult ingest =
                Hartonomous.Core.Text.CanonicalTextDecomposer.Emit(
                    batch, utf8, codepointCache,
                    new Hartonomous.Core.Text.TextDecomposeOptions(
                        ProvenanceCode: "user_session",
                        TopEntityType: "text_composition",
                        TrustMu: 1000.0));
            await pipeline.SubmitBatchAsync(batch, CancellationToken.None);

            // Step 0b: drain barrier.
            System.Diagnostics.Stopwatch barrierSw = System.Diagnostics.Stopwatch.StartNew();
            byte[] promptHash = ingest.RootHash;
            const int MaxAttempts = 6000;
            bool drained = false;
            for (int i = 0; i < MaxAttempts; i++)
            {
                await using Npgsql.NpgsqlConnection conn0 = await ds.OpenConnectionAsync(CancellationToken.None);
                await using Npgsql.NpgsqlCommand cmd0 = new(
                    @"WITH e AS (SELECT 1 FROM substrate.entity WHERE hash = $1 LIMIT 1),
                           s AS (SELECT 1 FROM substrate.sequence WHERE parent_hash = $1 LIMIT 1)
                      SELECT (SELECT count(*) FROM e), (SELECT count(*) FROM s)", conn0);
                cmd0.Parameters.AddWithValue(promptHash);
                await using Npgsql.NpgsqlDataReader r0 = await cmd0.ExecuteReaderAsync(CancellationToken.None);
                if (await r0.ReadAsync(CancellationToken.None) && r0.GetInt64(0) > 0 && r0.GetInt64(1) > 0)
                {
                    drained = true;
                    break;
                }
                await Task.Delay(50, CancellationToken.None);
            }
            barrierSw.Stop();

            Console.WriteLine("=== substrate.recall ===");
            Console.WriteLine($"  prompt: {text}");
            Console.WriteLine($"  hash:   {Convert.ToHexString(promptHash)[..16]}…");
            Console.WriteLine($"  drain:  {(drained ? $"{barrierSw.ElapsedMilliseconds} ms" : "TIMEOUT")}");
            Console.WriteLine();

            if (!drained)
            {
                Console.Error.WriteLine("ERROR: prompt did not drain to substrate. StagingFlushWorker may be unhealthy.");
                await flushWorker.StopAsync();
                return;
            }

            // Step 1-4: hub-intersection recall via substrate.recall.
            await using (Npgsql.NpgsqlConnection conn1 = await ds.OpenConnectionAsync(CancellationToken.None))
            await using (Npgsql.NpgsqlCommand cmd1 = new(
                "SELECT answer, target_hash, confidence, seed_count, target_count, elapsed_ms " +
                "FROM substrate.recall($1, $2, $3, $4)", conn1))
            {
                cmd1.Parameters.AddWithValue(promptHash);
                cmd1.Parameters.AddWithValue(3);
                cmd1.Parameters.AddWithValue(25);
                cmd1.Parameters.AddWithValue(0.25);
                cmd1.CommandTimeout = 300;
                await using Npgsql.NpgsqlDataReader r1 = await cmd1.ExecuteReaderAsync(CancellationToken.None);
                if (await r1.ReadAsync(CancellationToken.None))
                {
                    string? answer = r1.IsDBNull(0) ? null : r1.GetString(0);
                    byte[]? targetHash = r1.IsDBNull(1) ? null : (byte[])r1.GetValue(1);
                    double confidence = r1.IsDBNull(2) ? 0.0 : r1.GetDouble(2);
                    int seedCount = r1.IsDBNull(3) ? 0 : r1.GetInt32(3);
                    long targetCount = r1.IsDBNull(4) ? 0 : r1.GetInt64(4);
                    int elapsedMs = r1.IsDBNull(5) ? 0 : r1.GetInt32(5);

                    Console.WriteLine("=== Answer ===");
                    Console.WriteLine(string.IsNullOrEmpty(answer) ? "(honest abstention — no substrate path)" : answer);
                    Console.WriteLine();
                    Console.WriteLine($"=== Trace ===");
                    Console.WriteLine($"  seeds activated:  {seedCount}");
                    Console.WriteLine($"  targets reached:  {targetCount}");
                    Console.WriteLine($"  best target:      {(targetHash is null ? "(none)" : Convert.ToHexString(targetHash)[..16] + "…")}");
                    Console.WriteLine($"  confidence (mu):  {confidence:F1}");
                    Console.WriteLine($"  forward-pass:     {elapsedMs} ms");
                }
            }

            await flushWorker.StopAsync();
        });

        return recall;
    }

    private static Command BuildGodelCommand()
    {
        Option<string> connOpt = new("--connection", description: "Postgres connection string. Falls back to HARTONOMOUS_DB.")
        {
            IsRequired = false,
        };
        connOpt.SetDefaultValueFactory(() => DefaultConnectionString());

        Argument<string[]> textArg = new("text", "Prompt. Decomposed into sub-questions, each is its own forward pass; the engine synthesizes a final answer with confidence and a reasoning trace.");
        textArg.Arity = ArgumentArity.OneOrMore;

        Option<string?> outcomeOpt = new("--outcome",
            description: "Optional: 'accept' or 'reject' the primary answer once produced. Triggers Glicko-2 comparison events on the substrate edges that supported each candidate (Step 6 of inference.md).");
        outcomeOpt.SetDefaultValue(null);

        Command godel = new("godel",
            "Run a Gödel Engine inference. Three-phase OODA over the substrate: " +
            "Observe (sub-question decomposition + intent classification), " +
            "Orient (arena weighting), " +
            "Decide+Act (cross-arena top-K traversal + Reflexion retry on low confidence + Self-Consistency voting + multi-clause synthesis).");
        godel.AddOption(connOpt);
        godel.AddOption(outcomeOpt);
        godel.AddArgument(textArg);

        godel.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            string conn = ctx.ParseResult.GetValueForOption(connOpt)!;
            string[] textParts = ctx.ParseResult.GetValueForArgument(textArg);
            string text = string.Join(' ', textParts);
            string? outcome = ctx.ParseResult.GetValueForOption(outcomeOpt);

            await using Npgsql.NpgsqlDataSource ds = Npgsql.NpgsqlDataSource.Create(conn);
            Hartonomous.Engine.Data.NpgsqlReferenceDataReader refReader = new(ds);
            using Microsoft.Extensions.Logging.ILoggerFactory lf =
                Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information));

            // Subset codepoint cache to prompt — per AP-7, never full-load.
            HashSet<int> promptCodepoints = new();
            foreach (System.Text.Rune rune in text.EnumerateRunes())
            {
                promptCodepoints.Add(rune.Value);
            }
            Hartonomous.Engine.Text.NpgsqlCodepointPropertiesCache codepointCache =
                await Hartonomous.Engine.Text.NpgsqlCodepointPropertiesCache.LoadForCodepointsAsync(
                    conn, promptCodepoints, lf.CreateLogger<Hartonomous.Engine.Text.NpgsqlCodepointPropertiesCache>(),
                    CancellationToken.None);

            await using Hartonomous.Engine.Ingestion.StreamingIngestionPipeline pipeline = new(
                conn, refReader,
                lf.CreateLogger<Hartonomous.Engine.Ingestion.StreamingIngestionPipeline>());
            await using Npgsql.NpgsqlDataSource flushDs = Npgsql.NpgsqlDataSource.Create(conn);
            await using Hartonomous.Engine.Ingestion.StagingFlushWorker flushWorker =
                new(flushDs, lf.CreateLogger<Hartonomous.Engine.Ingestion.StagingFlushWorker>());
            await flushWorker.StartAsync();
            await flushWorker.DrainPreExistingResidueAsync(CancellationToken.None);

            Hartonomous.Engine.Godel.GodelEngine engine = new(
                ds, pipeline, codepointCache,
                lf.CreateLogger<Hartonomous.Engine.Godel.GodelEngine>());

            Console.WriteLine("=== Gödel Engine ===");
            Console.WriteLine($"  prompt: {text}");
            Console.WriteLine();

            Hartonomous.Engine.Godel.GodelResponse response =
                await engine.RunAsync(text, CancellationToken.None);

            Console.WriteLine("=== Answer ===");
            Console.WriteLine(string.IsNullOrWhiteSpace(response.PrimaryAnswer)
                ? (response.Abstained ? "(honest abstention — no candidate cleared the confidence floor)" : "(empty)")
                : response.PrimaryAnswer);
            Console.WriteLine();

            Console.WriteLine("=== Reasoning trace ===");
            Console.WriteLine(response.ReasoningTrace);
            Console.WriteLine();

            Console.WriteLine("=== Sub-question candidates ===");
            for (int i = 0; i < response.SubQuestionResults.Count; i++)
            {
                Hartonomous.Engine.Godel.SubQuestionResult sq = response.SubQuestionResults[i];
                Console.WriteLine($"  [{i}] '{sq.SubQuestion.Text}' intent={sq.Intent} seeds={sq.SeedCount} targets={sq.DistinctTargets} retries={sq.RetryCount} confidence={sq.Confidence:F1} ({sq.ElapsedMs} ms)");
                for (int k = 0; k < sq.Candidates.Count; k++)
                {
                    Hartonomous.Engine.Godel.GodelCandidate c = sq.Candidates[k];
                    string preview = c.RecomposedText.Length <= 200
                        ? c.RecomposedText
                        : c.RecomposedText[..200] + "…";
                    Console.WriteLine($"      rank {c.Rank}: mu={c.TotalMu:F1} paths={c.PathCount} → {preview}");
                }
            }
            Console.WriteLine();
            Console.WriteLine($"=== Total elapsed: {response.TotalElapsed.TotalMilliseconds:F1} ms ===");

            // Optional outcome feedback (Step 6 of inference.md).
            if (outcome is "accept" or "reject")
            {
                Hartonomous.Engine.Godel.OutcomeRecorder recorder = new(
                    ds, lf.CreateLogger<Hartonomous.Engine.Godel.OutcomeRecorder>());
                if (outcome == "accept")
                {
                    await recorder.RecordAcceptAsync(response, CancellationToken.None);
                    Console.WriteLine("Outcome accepted — Glicko-2 updates emitted.");
                }
                else
                {
                    await recorder.RecordRejectAsync(response, CancellationToken.None);
                    Console.WriteLine("Outcome rejected — Glicko-2 updates emitted (inverted).");
                }
            }

            await flushWorker.StopAsync();
        });

        return godel;
    }

    private static async Task<string> TryRecomposeAsync(
        Hartonomous.Engine.Data.NpgsqlEntityReader reader,
        Hartonomous.Core.Ingestion.EntityHandle? entity)
    {
        if (entity is null)
        {
            return "<no target>";
        }
        try
        {
            string? s = await reader.RecomposeTextAsync(entity.Value, int.MaxValue, CancellationToken.None);
            if (!string.IsNullOrEmpty(s))
            {
                return s.Length > 120 ? s[..117] + "..." : s;
            }
            return $"<{entity}>";
        }
        catch (Exception ex) // BOUNDARY: CLI display surface — recomposition errors on a single entity must not abort listing the other paths in the result.
        {
            return $"<{entity} (recompose error: {ex.Message[..Math.Min(60, ex.Message.Length)]})>";
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
