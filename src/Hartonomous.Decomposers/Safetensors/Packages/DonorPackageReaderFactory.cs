using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Packages;

public sealed partial class DonorPackageReaderFactory
{
    private const string ConfigFileName = "config.json";
    private const string SafetensorsIndexFileName = "model.safetensors.index.json";
    private const string PyTorchIndexFileName = "pytorch_model.bin.index.json";
    private const string PyTorchSingleFileName = "pytorch_model.bin";

    private static readonly Regex PyTorchShardPattern =
        new(@"^pytorch_model-\d+-of-\d+\.bin$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ConsolidatedPattern =
        new(@"^consolidated\..+\.(pt|pth|bin)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IDonorPackageReader Open(string packageRoot, ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            throw new ArgumentException("Package root must not be null or whitespace.", nameof(packageRoot));
        }
        ArgumentNullException.ThrowIfNull(loggerFactory);

        if (!Directory.Exists(packageRoot))
        {
            throw new DirectoryNotFoundException(
                $"Donor package directory does not exist: '{packageRoot}'.");
        }

        RejectQuantizedConfigs(packageRoot);
        RejectGguf(packageRoot);

        ILogger<DonorPackageReaderFactory> factoryLogger =
            loggerFactory.CreateLogger<DonorPackageReaderFactory>();

        if (MultiSubdirPackageReader.CanHandle(packageRoot))
        {
            var reader = new MultiSubdirPackageReader(
                packageRoot,
                loggerFactory.CreateLogger<MultiSubdirPackageReader>(),
                childReaderFactory: (subdirPath, _) => Open(subdirPath, loggerFactory));
            LogPackageOpened(factoryLogger, packageRoot, "multi_subdir");
            return reader;
        }

        if (HasSafetensorsAtRoot(packageRoot))
        {
            var reader = new SafetensorsPackageReader(
                packageRoot,
                loggerFactory.CreateLogger<SafetensorsPackageReader>());
            LogPackageOpened(factoryLogger, packageRoot, "safetensors");
            return reader;
        }

        if (HasPyTorchPickleAtRoot(packageRoot))
        {
            var reader = new PyTorchPicklePackageReader(
                packageRoot,
                loggerFactory.CreateLogger<PyTorchPicklePackageReader>());
            LogPackageOpened(factoryLogger, packageRoot, "pytorch_pickle");
            return reader;
        }

        throw new InvalidDataException(
            $"No supported donor package format detected at '{packageRoot}'. " +
            "Expected one of: model.safetensors, model.safetensors.index.json, pytorch_model.bin, " +
            "pytorch_model.bin.index.json, consolidated.*.pt, or a multi-subdirectory diffusers-style " +
            "layout with model_index.json.");
    }

    private static void RejectQuantizedConfigs(string packageRoot)
    {
        string configPath = Path.Combine(packageRoot, ConfigFileName);
        if (!File.Exists(configPath))
        {
            return;
        }

        using FileStream fs = new(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(fs);
        }
        catch (JsonException) // BOUNDARY: malformed config is handled by the selected donor reader's validation path.
        {
            // A malformed config is not our concern here — the chosen reader will surface it.
            return;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            string? modelName = null;
            if (doc.RootElement.TryGetProperty("_name_or_path", out JsonElement nameEl)
                && nameEl.ValueKind == JsonValueKind.String)
            {
                modelName = nameEl.GetString();
            }

            string subjectLabel = string.IsNullOrWhiteSpace(modelName)
                ? $"package at '{packageRoot}'"
                : modelName!;

            if (doc.RootElement.TryGetProperty("awq_config", out _))
            {
                throw new NotSupportedException(
                    $"AWQ/GPTQ quantized donor packages are out of scope; " +
                    $"use the full-precision safetensors variant of {subjectLabel} instead.");
            }

            if (doc.RootElement.TryGetProperty("quantization_config", out JsonElement quantConfig)
                && quantConfig.ValueKind == JsonValueKind.Object
                && quantConfig.TryGetProperty("quant_method", out JsonElement quantMethod)
                && quantMethod.ValueKind == JsonValueKind.String)
            {
                string? method = quantMethod.GetString();
                if (string.Equals(method, "awq", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(method, "gptq", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException(
                        $"AWQ/GPTQ quantized donor packages are out of scope; " +
                        $"use the full-precision safetensors variant of {subjectLabel} instead.");
                }
            }
        }
    }

    private static void RejectGguf(string packageRoot)
    {
        if (Directory.EnumerateFiles(packageRoot, "*.gguf", SearchOption.TopDirectoryOnly).Any())
        {
            throw new NotSupportedException(
                "GGUF donor packages are out of scope; use the full-precision safetensors variant instead.");
        }
    }

    private static bool HasSafetensorsAtRoot(string packageRoot)
    {
        if (File.Exists(Path.Combine(packageRoot, SafetensorsIndexFileName)))
        {
            return true;
        }
        return Directory.EnumerateFiles(packageRoot, "*.safetensors", SearchOption.TopDirectoryOnly).Any();
    }

    private static bool HasPyTorchPickleAtRoot(string packageRoot)
    {
        if (File.Exists(Path.Combine(packageRoot, PyTorchIndexFileName)))
        {
            return true;
        }
        if (File.Exists(Path.Combine(packageRoot, PyTorchSingleFileName)))
        {
            return true;
        }

        foreach (string path in Directory.EnumerateFiles(packageRoot, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            string ext = Path.GetExtension(path);

            if (string.Equals(ext, ".pt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".pth", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (PyTorchShardPattern.IsMatch(name))
            {
                return true;
            }

            if (ConsolidatedPattern.IsMatch(name))
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Donor package opened: root={PackageRoot} format={PackageFormat}")]
    private static partial void LogPackageOpened(ILogger logger, string packageRoot, string packageFormat);
}
