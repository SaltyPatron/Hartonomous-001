using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Recomposers.Synthesizers;
using Npgsql;

namespace Hartonomous.Cli.Commands;

/// <summary>
/// Substrate-derived Build-a-bear model synthesis. Distinct from
/// <c>export-model</c> which exports an already-ingested model from its
/// stored phantom-scatter entities (deprecated path, AP-28). This command
/// SYNTHESIZES a target architecture from the substrate's accumulated
/// cross-source attestation surface — no AI model ingestion required.
///
/// Output is a directory containing:
///   model.safetensors           — synthesized tensors
///   config.json                 — HF transformers config
///   tokenizer.json              — substrate-derived vocab
///   tokenizer_config.json
///   hartonomous_audit.json      — recipe + provenance chain
///
/// Loadable in HF transformers via AutoModel.from_pretrained(output);
/// convertible to GGUF via llama.cpp's convert-hf-to-gguf.py.
/// </summary>
internal sealed class SynthesizeModelCommand(NpgsqlDataSource dataSource)
{
    public Command Build()
    {
        // Recipe-driven primary path. The recipe IS the bear: a single
        // structured JSON document defining structure / vocab / arena weights /
        // synthesis algorithms / MoE / LoRA / RoPE / dtype. Templates pre-cut
        // the famous architectures (Llama1B/3B, Qwen7B, Mistral7B, BERT,
        // MiniLM); arbitrary custom architectures pass --recipe.
        Option<string?> recipePathOpt = new(
            "--recipe",
            description: "Path to a recipe JSON file (the Build-a-bear spec). "
                       + "Mutually exclusive with --template.");

        Option<string?> templateOpt = new(
            "--template",
            description: "Pre-cut template name: minilm-base | bert-base | "
                       + "llama-small | llama-1b | llama-3b | qwen-7b | mistral-7b. "
                       + "Mutually exclusive with --recipe.");

        Option<string?> archOpt = new(
            "--arch",
            description: "Legacy: alias for --template (pre-recipe CLI surface).");

        Option<int?> vocabSizeOpt = new(
            "--vocab-size",
            description: "Override the recipe's vocab_size at the CLI.");

        Option<string> outputOpt = new(
            "--output",
            description: "Output directory (will be created if absent).");
        outputOpt.IsRequired = true;

        Option<string?> packageOpt = new(
            "--package",
            description: "Override the recipe's package_format: safetensors | gguf.");

        Option<string?> dtypeOpt = new(
            "--dtype",
            description: "Override the recipe's output_dtype: f32 | f16 | bf16.");

        Option<bool?> honestAbstentionOpt = new(
            "--honest-abstention",
            description: "Override the recipe's honest_abstention.");

        Option<string?> writeRecipeOpt = new(
            "--write-recipe",
            description: "Write the resolved recipe (post-template + overrides) to "
                       + "this path before running synthesis. Useful for capturing the "
                       + "exact bear that produced the export.");

        Command cmd = new(
            "synthesize-model",
            "Substrate-derived Build-a-bear model synthesis. Pass --recipe <path> for "
          + "a custom bear, or --template <name> for a pre-cut famous architecture. "
          + "CLI overrides apply on top of either.");
        cmd.AddOption(recipePathOpt);
        cmd.AddOption(templateOpt);
        cmd.AddOption(archOpt);
        cmd.AddOption(vocabSizeOpt);
        cmd.AddOption(outputOpt);
        cmd.AddOption(packageOpt);
        cmd.AddOption(dtypeOpt);
        cmd.AddOption(honestAbstentionOpt);
        cmd.AddOption(writeRecipeOpt);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            string? recipePath = ctx.ParseResult.GetValueForOption(recipePathOpt);
            string? templateName = ctx.ParseResult.GetValueForOption(templateOpt)
                                 ?? ctx.ParseResult.GetValueForOption(archOpt);
            int? vocabOverride = ctx.ParseResult.GetValueForOption(vocabSizeOpt);
            string outputDir = ctx.ParseResult.GetValueForOption(outputOpt)!;
            string? packageName = ctx.ParseResult.GetValueForOption(packageOpt);
            string? dtypeName = ctx.ParseResult.GetValueForOption(dtypeOpt);
            bool? honestAbstention = ctx.ParseResult.GetValueForOption(honestAbstentionOpt);
            string? writeRecipePath = ctx.ParseResult.GetValueForOption(writeRecipeOpt);
            CancellationToken ct = ctx.GetCancellationToken();

            if (recipePath is null && templateName is null)
            {
                templateName = "minilm-base";
            }
            if (recipePath is not null && templateName is not null)
            {
                throw new ArgumentException("--recipe and --template are mutually exclusive.");
            }

            RecipeConfig recipe = recipePath is not null
                ? await RecipeConfig.LoadAsync(recipePath, ct).ConfigureAwait(false)
                : RecipeTemplates.Resolve(templateName!, vocabOverride);

            // CLI overrides on top of the loaded recipe.
            if (vocabOverride is int vo)
            {
                recipe.Architecture.VocabSize = vo;
            }
            if (packageName is not null)
            {
                recipe.PackageFormat = ResolvePackageFormat(packageName);
            }
            if (dtypeName is not null)
            {
                recipe.OutputDtype = ResolveDtype(dtypeName);
            }
            if (honestAbstention is bool ha)
            {
                recipe.HonestAbstention = ha;
            }

            Directory.CreateDirectory(outputDir);
            if (writeRecipePath is not null)
            {
                await recipe.SaveAsync(writeRecipePath, ct).ConfigureAwait(false);
            }

            TargetArchitectureSpec arch = recipe.ToArchitectureSpec();
            RecompositionOptions options = recipe.ToRecompositionOptions();

            System.Console.Out.WriteLine(
                $"Synthesizing {recipe.Name} family={recipe.Architecture.Family} "
              + $"vocab={arch.VocabSize} hidden={arch.HiddenDim} layers={arch.NumHiddenLayers} "
              + $"package={recipe.PackageFormat} dtype={recipe.OutputDtype}");
            System.Console.Out.WriteLine($"Output: {outputDir}");

            if (recipe.PackageFormat == PackageFormat.Gguf)
            {
                throw new NotImplementedException(
                    "GGUF package_format is on the roadmap. For now, export safetensors and "
                  + "convert via llama.cpp's convert-hf-to-gguf.py — produces an identical "
                  + "GGUF to a native writer for any architecture llama.cpp recognizes.");
            }

            long t0 = System.Environment.TickCount64;

            await SubstrateModelExporter.ExportAsync(dataSource, arch, options, outputDir, ct)
                .ConfigureAwait(false);

            long elapsedMs = System.Environment.TickCount64 - t0;
            System.Console.Out.WriteLine($"Synthesis complete in {elapsedMs} ms.");
            System.Console.Out.WriteLine("Files emitted:");
            foreach (string f in Directory.EnumerateFiles(outputDir))
            {
                long sz = new FileInfo(f).Length;
                System.Console.Out.WriteLine($"  {Path.GetFileName(f),-32} {sz,15:N0} bytes");
            }
        });

        return cmd;
    }

    private static PackageFormat ResolvePackageFormat(string name) => name.ToLowerInvariant() switch
    {
        "safetensors" => PackageFormat.Safetensors,
        "safetensors-sharded" or "shards" => PackageFormat.SafetensorsSharded,
        "gguf" => PackageFormat.Gguf,
        _ => throw new ArgumentException(
            $"Unknown --package '{name}'. Supported: safetensors, safetensors-sharded, gguf."),
    };

    private static QuantizationTarget ResolveDtype(string dtypeName) =>
        dtypeName.ToLowerInvariant() switch
        {
            "f32" => QuantizationTarget.F32,
            "f16" => QuantizationTarget.F16,
            "bf16" => QuantizationTarget.BF16,
            "q8" => QuantizationTarget.Q8,
            "awq-q4" or "awqq4" => QuantizationTarget.AwqQ4,
            _ => throw new ArgumentException(
                $"Unknown --dtype '{dtypeName}'. Supported: f32, f16, bf16, q8, awq-q4."),
        };
}
