using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Query;
using Hartonomous.Core.Recomposition;
using Hartonomous.Engine.Data;
using Hartonomous.Engine.Query;
using Hartonomous.Recomposers;
using Npgsql;

namespace Hartonomous.Cli.Commands;

/// <summary>
/// Recomposes a substrate model_architecture into a safetensors file.
/// With filter options the export becomes a distillation query.
/// </summary>
internal sealed class ExportModelCommand(NpgsqlDataSource dataSource)
{
    public Command Build()
    {
        Option<string> archHashOpt = new("--arch-hash",
            "model_architecture entity BLAKE3 hash (64 hex chars)");
        archHashOpt.IsRequired = true;
        Option<string> outputOpt = new("--output",
            "Output safetensors path (file for single-shard, directory when --shard is set)");
        outputOpt.IsRequired = true;
        Option<long[]> sourceIdsOpt = new("--source-id",
            "model_source_id to filter on (repeat for multiple). Omit for unfiltered export.");
        sourceIdsOpt.AllowMultipleArgumentsPerToken = true;
        Option<double?> minMuOpt = new("--min-significance",
            "Minimum significance mu (inclusive).");
        Option<string?> contextOpt = new("--context",
            "Significance arena context code (e.g. 'model_trust', 'semantic_relevance').");
        Option<int?> limitOpt = new("--limit",
            "Maximum number of tensors to include in the distilled export.");
        Option<string?> recipeOpt = new("--recipe",
            "Path to a recipe JSON (Mode/RefinementPolicy/QuantizationPolicy/etc.).");
        Option<double> noiseFloorOpt = new("--noise-floor",
            getDefaultValue: () => 0.0,
            description: "Per-element |x| below this is zeroed at recompose.");
        Option<bool> shardOpt = new("--shard",
            getDefaultValue: () => false,
            description: "Multi-shard output; --output is treated as a directory.");
        Option<long> maxShardBytesOpt = new("--max-shard-bytes",
            getDefaultValue: () => 5_000_000_000L,
            description: "Maximum bytes per shard when --shard is set (default 5 GB).");
        Option<bool> includeProvenanceOpt = new("--include-provenance",
            getDefaultValue: () => true,
            description: "Emit __metadata__ audit chain.");

        Command exportModel = new("export-model",
            "Recompose a substrate model_architecture into a safetensors file. "
            + "With filter options the export becomes a distillation query.");
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

        exportModel.SetHandler(async (InvocationContext ctx) =>
        {
            string archHashHex = ctx.ParseResult.GetValueForOption(archHashOpt)!;
            byte[] archHashBytes = Convert.FromHexString(archHashHex);
            EntityHandle archHandle = new(archHashBytes, "model_architecture");
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

            NpgsqlEntityReader entityReader = new(dataSource);
            NpgsqlPhysicalityReader physReader = new(dataSource);
            NpgsqlSubstrateQuery query = new(dataSource);
            SafetensorsRecomposer recomposer = new(entityReader, entityReader, physReader, query);

            bool filtered = sourceIds.Length > 0 || minMu.HasValue || !string.IsNullOrEmpty(context) || limit.HasValue;

            RecompositionOptions opts;
            if (!string.IsNullOrEmpty(recipePath))
            {
                opts = LoadRecipeOptions(recipePath, noiseFloor, maxShardBytes, includeProvenance);
                Console.WriteLine($"=== Recipe: {recipePath} ===");
            }
            else
            {
                opts = new() { MaxDepth = 20, NoiseFloor = noiseFloor, MaxShardBytes = maxShardBytes, IncludeProvenance = includeProvenance };
            }

            Console.WriteLine($"=== {(filtered ? "Distilling" : "Exporting")} model_architecture {archHandle} → {output} ===");
            if (filtered)
            {
                if (sourceIds.Length > 0)
                {
                    Console.WriteLine($"  --source-id={string.Join(',', sourceIds)}");
                }

                if (minMu.HasValue)
                {
                    Console.WriteLine($"  --min-significance={minMu.Value}");
                }

                if (!string.IsNullOrEmpty(context))
                {
                    Console.WriteLine($"  --context={context}");
                }

                if (limit.HasValue)
                {
                    Console.WriteLine($"  --limit={limit.Value}");
                }
            }
            if (noiseFloor > 0)
            {
                Console.WriteLine($"  --noise-floor={noiseFloor}");
            }

            if (opts.SignificanceThreshold > 0)
            {
                Console.WriteLine($"  significance_threshold={opts.SignificanceThreshold}");
            }

            if (shard)
            {
                Console.WriteLine($"  --shard --max-shard-bytes={maxShardBytes:N0}");
            }

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

            SafetensorsFile file;
            if (filtered)
            {
                SubstrateQueryFilter filter = new()
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

            IReadOnlyDictionary<string, string>? auditMetadata = null;
            if (includeProvenance)
            {
                Dictionary<string, string> meta = new()
                {
                    ["hartonomous_recomposer_version"] = "v1",
                    ["hartonomous_mode"] = opts.Mode.ToString(),
                    ["hartonomous_refinement_policy"] = opts.RefinementPolicy.ToString(),
                    ["hartonomous_quantization_policy"] = opts.QuantizationPolicy.ToString(),
                    ["hartonomous_lora_policy"] = opts.LoraPolicy.ToString(),
                    ["hartonomous_noise_floor"] = noiseFloor.ToString(CultureInfo.InvariantCulture),
                };
                if (!string.IsNullOrEmpty(opts.RecipeId))
                {
                    meta["hartonomous_recipe_id"] = opts.RecipeId;
                }

                auditMetadata = meta;
            }

            await using (FileStream fileStream = File.Create(output))
            {
                await SafetensorsWriter.WriteAsync(file, fileStream, auditMetadata, CancellationToken.None);
            }

            sw.Stop();
            FileInfo fi = new(output);
            Console.WriteLine($"Tensors written: {file.Tensors.Count}");
            Console.WriteLine($"File size: {fi.Length:N0} bytes");
            Console.WriteLine($"Elapsed: {sw.Elapsed.TotalSeconds:F1}s");
        });

        return exportModel;
    }

    private static RecompositionOptions LoadRecipeOptions(
        string recipePath, double cliNoiseFloor, long cliMaxShardBytes, bool cliIncludeProvenance)
    {
        if (!File.Exists(recipePath))
        {
            throw new FileNotFoundException($"Recipe file not found: {recipePath}");
        }

        string json = File.ReadAllText(recipePath);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        RecompositionMode mode =
            root.TryGetProperty("mode", out JsonElement m) && Enum.TryParse(m.GetString(), out RecompositionMode parsedMode)
                ? parsedMode : RecompositionMode.Refinement;

        RefinementPolicy policy =
            root.TryGetProperty("refinement_policy", out JsonElement rp) && Enum.TryParse(rp.GetString(), out RefinementPolicy parsedPolicy)
                ? parsedPolicy : RefinementPolicy.SourceOnly;

        QuantizationPolicy qp =
            root.TryGetProperty("quantization_policy", out JsonElement qpe) && Enum.TryParse(qpe.GetString(), out QuantizationPolicy parsedQp)
                ? parsedQp : QuantizationPolicy.Preserve;

        LoraPolicy lp =
            root.TryGetProperty("lora_policy", out JsonElement lpe) && Enum.TryParse(lpe.GetString(), out LoraPolicy parsedLp)
                ? parsedLp : LoraPolicy.None;

        double noiseFloor = root.TryGetProperty("noise_floor", out JsonElement nf) && nf.ValueKind == JsonValueKind.Number
            ? nf.GetDouble() : cliNoiseFloor;

        double sigThreshold = root.TryGetProperty("significance_threshold", out JsonElement st) && st.ValueKind == JsonValueKind.Number
            ? st.GetDouble() : 0.0;

        long maxShardBytes = root.TryGetProperty("max_shard_bytes", out JsonElement msb) && msb.ValueKind == JsonValueKind.Number
            ? msb.GetInt64() : cliMaxShardBytes;

        string? requantizeTarget = root.TryGetProperty("requantize_target", out JsonElement rt) ? rt.GetString() : null;
        string? provenanceFilter = root.TryGetProperty("provenance_filter", out JsonElement pf) ? pf.GetString() : null;

        List<string>? arenaCodes = null;
        if (root.TryGetProperty("arena_codes", out JsonElement ac) && ac.ValueKind == JsonValueKind.Array)
        {
            arenaCodes = [];
            foreach (JsonElement ace in ac.EnumerateArray())
            {
                string? s = ace.GetString();
                if (!string.IsNullOrEmpty(s))
                {
                    arenaCodes.Add(s);
                }
            }
        }

        string? targetArchSpecJson = root.TryGetProperty("target_arch_spec", out JsonElement tas)
            ? tas.GetRawText() : null;

        Hartonomous.Core.Compute.IComputeFacade compute = Hartonomous.Core.Compute.ComputeFacade.Instance;
        RecompositionOptions opts = new()
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
        string recipeId = RecipeContentHash.Compute(opts, compute);
        return opts with { RecipeId = recipeId };
    }
}
