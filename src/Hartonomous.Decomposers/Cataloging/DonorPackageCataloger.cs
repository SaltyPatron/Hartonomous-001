using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hartonomous.Core.Operations;
using Hartonomous.Decomposers.Safetensors.Adapters;
using Hartonomous.Decomposers.Safetensors.Packages;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Cataloging;

public sealed partial class DonorPackageCataloger
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] ConfigSummaryKeys =
    [
        "hidden_size", "num_attention_heads", "num_hidden_layers",
        "num_key_value_heads", "vocab_size", "intermediate_size",
        "max_position_embeddings", "rope_theta", "model_type",
        "num_experts", "num_local_experts", "n_routed_experts",
        "num_experts_per_tok", "num_labels", "task_type",
        "torch_dtype", "_name_or_path",
    ];

    private readonly ILogger<DonorPackageCataloger> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IArchitectureAdapter[] _adapters;

    public DonorPackageCataloger(
        ILogger<DonorPackageCataloger> logger,
        ILoggerFactory loggerFactory,
        IEnumerable<IArchitectureAdapter> adapters)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _adapters = adapters.ToArray();
    }

    public async Task<CatalogRunSummary> CatalogHubAsync(
        string hubRoot,
        string outputRoot,
        CancellationToken ct)
    {
        if (!Directory.Exists(hubRoot))
        {
            throw new DirectoryNotFoundException(hubRoot);
        }
        Directory.CreateDirectory(outputRoot);

        List<DonorManifest> manifests = [];
        Dictionary<string, TensorPatternEntry> patternMap = new(StringComparer.Ordinal);
        int discovered = 0;
        int ingested = 0;
        int unsupported = 0;
        int rejected = 0;
        int discoveryFailed = 0;
        int totalUnclassified = 0;

        foreach (string packageRoot in EnumerateModelDirectories(hubRoot))
        {
            ct.ThrowIfCancellationRequested();
            discovered++;
            DonorManifest manifest = await CatalogOneAsync(packageRoot, ct).ConfigureAwait(false);
            manifests.Add(manifest);

            switch (manifest.Status)
            {
                case DonorManifestStatuses.Ingested:
                    ingested++;
                    totalUnclassified += manifest.UnclassifiedTensors.Count;
                    AccumulatePatterns(patternMap, manifest);
                    break;
                case DonorManifestStatuses.UnsupportedV1:
                    unsupported++;
                    break;
                case DonorManifestStatuses.Rejected:
                    rejected++;
                    break;
                case DonorManifestStatuses.DiscoveryFailed:
                    discoveryFailed++;
                    break;
            }

            string manifestPath = Path.Combine(
                outputRoot,
                ManifestDirectoryName(manifest.Vendor, manifest.Model),
                "manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            string json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
            await File.WriteAllTextAsync(manifestPath, json, ct).ConfigureAwait(false);
        }

        TensorPatternCatalog catalog = new()
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Entries = patternMap.Values
                .OrderBy(e => e.ArchitectureClass, StringComparer.Ordinal)
                .ThenBy(e => e.TensorName, StringComparer.Ordinal)
                .ToArray(),
        };
        string catalogPath = Path.Combine(outputRoot, "tensor-pattern-catalog.json");
        string catalogJson = JsonSerializer.Serialize(catalog, ManifestJsonOptions);
        await File.WriteAllTextAsync(catalogPath, catalogJson, ct).ConfigureAwait(false);

        string readmePath = Path.Combine(outputRoot, "README.md");
        string readme = BuildReadme(manifests, discovered, ingested, unsupported, rejected, discoveryFailed, totalUnclassified);
        await File.WriteAllTextAsync(readmePath, readme, ct).ConfigureAwait(false);

        return new CatalogRunSummary(
            HubRoot: hubRoot,
            OutputRoot: outputRoot,
            Discovered: discovered,
            Ingested: ingested,
            UnsupportedV1: unsupported,
            Rejected: rejected,
            DiscoveryFailed: discoveryFailed,
            UnclassifiedTensors: totalUnclassified,
            UniquePatterns: catalog.Entries.Count);
    }

    private async Task<DonorManifest> CatalogOneAsync(string packageRoot, CancellationToken ct)
    {
        (string vendor, string model) = ParseVendorModel(packageRoot);
        string effectivePackageRoot = ResolvePackageRoot(packageRoot);

        IDonorPackageReader? reader = null;
        try
        {
            try
            {
                reader = DonorPackageReaderFactory.Open(effectivePackageRoot, _loggerFactory);
            }
            catch (NotSupportedException ex)
            {
                LogPackageRejected(_logger, packageRoot, ex.Message);
                return new DonorManifest
                {
                    Vendor = vendor,
                    Model = model,
                    PackageRoot = effectivePackageRoot,
                    PackageFormat = "rejected",
                    Status = DonorManifestStatuses.Rejected,
                    RejectionReason = ex.Message,
                };
            }
            catch (Exception ex) when (ex is InvalidDataException or DirectoryNotFoundException or FileNotFoundException)
            {
                LogPackageDiscoveryFailed(_logger, packageRoot, ex.Message);
                return new DonorManifest
                {
                    Vendor = vendor,
                    Model = model,
                    PackageRoot = effectivePackageRoot,
                    PackageFormat = "unknown",
                    Status = DonorManifestStatuses.DiscoveryFailed,
                    RejectionReason = ex.Message,
                };
            }

            IConfigSnapshot config;
            try
            {
                config = reader.ReadConfig();
            }
            catch (Exception ex)
            {
                LogConfigReadFailed(_logger, packageRoot, ex.Message);
                config = new EmptyConfigSnapshot();
            }

            IArchitectureAdapter? adapter = SelectAdapter(config);
            IReadOnlyList<TensorMetadata> tensors;
            try
            {
                tensors = reader.EnumerateTensors();
            }
            catch (Exception ex)
            {
                LogEnumerateFailed(_logger, packageRoot, ex.Message);
                return new DonorManifest
                {
                    Vendor = vendor,
                    Model = model,
                    PackageRoot = effectivePackageRoot,
                    PackageFormat = reader.PackageFormat,
                    Status = DonorManifestStatuses.DiscoveryFailed,
                    RejectionReason = ex.Message,
                };
            }

            List<DonorTensor> classified = new(tensors.Count);
            List<string> unclassified = [];
            Dictionary<string, int> modalitySummary = new(StringComparer.Ordinal);

            foreach (TensorMetadata t in tensors)
            {
                ct.ThrowIfCancellationRequested();
                if (adapter is not null && adapter.TryClassify(t.Name, t.Shape, t.Dtype, out ModalityLobe lobe, out string role))
                {
                    string lobeCode = LobeToSnakeCase(lobe);
                    classified.Add(new DonorTensor
                    {
                        Name = t.Name,
                        Dtype = t.Dtype,
                        Shape = t.Shape,
                        ByteLength = t.ByteLength,
                        Component = t.Component,
                        Lobe = lobeCode,
                        Role = role,
                    });
                    modalitySummary[lobeCode] = modalitySummary.GetValueOrDefault(lobeCode) + 1;
                }
                else
                {
                    string fallbackLobe = LobeToSnakeCase(ModalityLobe.UnsupportedV1);
                    classified.Add(new DonorTensor
                    {
                        Name = t.Name,
                        Dtype = t.Dtype,
                        Shape = t.Shape,
                        ByteLength = t.ByteLength,
                        Component = t.Component,
                        Lobe = fallbackLobe,
                        Role = "unclassified",
                    });
                    unclassified.Add(t.Name);
                    modalitySummary[fallbackLobe] = modalitySummary.GetValueOrDefault(fallbackLobe) + 1;
                }
            }

            string status = adapter is null
                ? DonorManifestStatuses.UnsupportedV1
                : DonorManifestStatuses.Ingested;
            string? requiredAdapter = adapter is null ? GuessRequiredAdapter(config) : null;

            return new DonorManifest
            {
                Vendor = vendor,
                Model = model,
                PackageRoot = effectivePackageRoot,
                PackageFormat = reader.PackageFormat,
                Status = status,
                ArchitectureClass = adapter?.ArchitectureClassCode,
                RequiredAdapter = requiredAdapter,
                PackageFiles = SummarizePackageFiles(reader, tensors),
                Architectures = config.GetStringArray("architectures") ?? [],
                ConfigSummary = ExtractConfigSummary(config),
                TensorCount = classified.Count,
                Tensors = classified,
                UnclassifiedTensors = unclassified,
                ModalitySummary = modalitySummary,
                AdditionalArtifacts = reader.AdditionalArtifacts,
            };
        }
        finally
        {
            if (reader is not null)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private IArchitectureAdapter? SelectAdapter(IConfigSnapshot config)
    {
        for (int i = 0; i < _adapters.Length; i++)
        {
            try
            {
                if (_adapters[i].CanHandle(config))
                {
                    return _adapters[i];
                }
            }
            catch
            {
                continue;
            }
        }
        return null;
    }

    private static IEnumerable<string> EnumerateModelDirectories(string hubRoot)
    {
        foreach (string dir in Directory.EnumerateDirectories(hubRoot))
        {
            string name = Path.GetFileName(dir);
            if (name.StartsWith("datasets--", StringComparison.Ordinal))
            {
                continue;
            }
            yield return dir;
        }
    }

    private static (string Vendor, string Model) ParseVendorModel(string packageRoot)
    {
        string name = Path.GetFileName(packageRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string normalized = name;
        while (normalized.Length > 0 && (normalized[0] is 'x' or 'y' or 'z' or 'w'))
        {
            int testIdx = normalized.IndexOf("models--", StringComparison.Ordinal);
            if (testIdx == 1)
            {
                normalized = normalized[1..];
            }
            else
            {
                break;
            }
        }
        if (normalized.StartsWith("models--", StringComparison.Ordinal))
        {
            string body = normalized["models--".Length..];
            int sep = body.IndexOf("--", StringComparison.Ordinal);
            if (sep > 0)
            {
                return (body[..sep], body[(sep + 2)..]);
            }
            return ("unknown", body);
        }
        return ("local", normalized);
    }

    private static string ResolvePackageRoot(string packageRoot)
    {
        string snapshots = Path.Combine(packageRoot, "snapshots");
        if (Directory.Exists(snapshots))
        {
            string? latest = Directory.EnumerateDirectories(snapshots).OrderByDescending(d => d).FirstOrDefault();
            if (latest is not null)
            {
                return latest;
            }
        }
        return packageRoot;
    }

    private static string ManifestDirectoryName(string vendor, string model)
    {
        StringBuilder sb = new();
        AppendSafe(sb, vendor);
        sb.Append("--");
        AppendSafe(sb, model);
        return sb.ToString();
    }

    private static void AppendSafe(StringBuilder sb, string s)
    {
        foreach (char c in s)
        {
            sb.Append(InvalidPathChar().IsMatch(c.ToString()) ? '_' : c);
        }
    }

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1F]")]
    private static partial Regex InvalidPathChar();

    private static Dictionary<string, object?> ExtractConfigSummary(IConfigSnapshot config)
    {
        Dictionary<string, object?> summary = new(StringComparer.Ordinal);
        foreach (string key in ConfigSummaryKeys)
        {
            int? i32 = config.GetInt32(key);
            if (i32 is not null) { summary[key] = i32; continue; }
            long? i64 = config.GetInt64(key);
            if (i64 is not null) { summary[key] = i64; continue; }
            double? d = config.GetDouble(key);
            if (d is not null) { summary[key] = d; continue; }
            bool? b = config.GetBoolean(key);
            if (b is not null) { summary[key] = b; continue; }
            string s = config.GetString(key, string.Empty) ?? string.Empty;
            if (s.Length > 0)
            {
                summary[key] = s;
            }
        }
        return summary;
    }

    private static List<DonorPackageFile> SummarizePackageFiles(
        IDonorPackageReader reader,
        IReadOnlyList<TensorMetadata> tensors)
    {
        Dictionary<string, int> componentCounts = new(StringComparer.Ordinal);
        foreach (TensorMetadata t in tensors)
        {
            string key = t.Component ?? reader.PackageFormat;
            componentCounts[key] = componentCounts.GetValueOrDefault(key) + 1;
        }
        List<DonorPackageFile> files = new(componentCounts.Count);
        foreach (KeyValuePair<string, int> kvp in componentCounts.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            files.Add(new DonorPackageFile
            {
                Path = kvp.Key,
                Format = reader.PackageFormat,
                TensorCount = kvp.Value,
            });
        }
        return files;
    }

    private static string? GuessRequiredAdapter(IConfigSnapshot config)
    {
        IReadOnlyList<string>? archs = config.GetStringArray("architectures");
        if (archs is null || archs.Count == 0)
        {
            return null;
        }
        string a = archs[0];
        if (a.Contains("Detr", StringComparison.OrdinalIgnoreCase) || a.Contains("DINO", StringComparison.OrdinalIgnoreCase))
        {
            return "VisionDetrAdapter";
        }
        if (a.Contains("Florence", StringComparison.OrdinalIgnoreCase))
        {
            return "VisionLanguageAdapter (Florence-2)";
        }
        if (a.Contains("Yolo", StringComparison.OrdinalIgnoreCase))
        {
            return "VisionYoloAdapter";
        }
        if (a.Contains("Speech", StringComparison.OrdinalIgnoreCase) || a.Contains("Conformer", StringComparison.OrdinalIgnoreCase))
        {
            return "AudioConformerAdapter";
        }
        if (a.Contains("Flux", StringComparison.OrdinalIgnoreCase) || a.Contains("Diffusion", StringComparison.OrdinalIgnoreCase))
        {
            return "DiffusionUnetAdapter";
        }
        if (a.Contains("Sam", StringComparison.OrdinalIgnoreCase))
        {
            return "VisionSamAdapter";
        }
        return $"adapter for {a}";
    }

    private static string LobeToSnakeCase(ModalityLobe lobe)
    {
        string name = lobe.ToString();
        StringBuilder sb = new(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c))
            {
                sb.Append('_');
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static void AccumulatePatterns(Dictionary<string, TensorPatternEntry> map, DonorManifest manifest)
    {
        if (manifest.ArchitectureClass is null)
        {
            return;
        }
        string modelKey = $"{manifest.Vendor}/{manifest.Model}";
        foreach (DonorTensor t in manifest.Tensors)
        {
            if (string.Equals(t.Role, "unclassified", StringComparison.Ordinal))
            {
                continue;
            }
            string key = $"{manifest.ArchitectureClass}::{t.Name}";
            if (map.TryGetValue(key, out TensorPatternEntry? existing))
            {
                if (!existing.ObservedInModels.Contains(modelKey, StringComparer.Ordinal))
                {
                    List<string> models = new(existing.ObservedInModels) { modelKey };
                    map[key] = existing with { ObservedInModels = models };
                }
            }
            else
            {
                map[key] = new TensorPatternEntry
                {
                    TensorName = t.Name,
                    Lobe = t.Lobe,
                    Role = t.Role,
                    ArchitectureClass = manifest.ArchitectureClass,
                    ObservedInModels = [modelKey],
                    ExampleShape = t.Shape,
                    ExampleDtype = t.Dtype,
                };
            }
        }
    }

    private static string BuildReadme(
        IReadOnlyList<DonorManifest> manifests,
        int discovered, int ingested, int unsupported, int rejected, int discoveryFailed,
        int unclassified)
    {
        StringBuilder sb = new();
        sb.AppendLine("# Donor Catalog");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Generated: {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine();
        sb.AppendLine("## Run summary");
        sb.AppendLine();
        sb.AppendFormat(CultureInfo.InvariantCulture, "- Discovered: {0}\n", discovered);
        sb.AppendFormat(CultureInfo.InvariantCulture, "- Ingested: {0}\n", ingested);
        sb.AppendFormat(CultureInfo.InvariantCulture, "- Unsupported (V1): {0}\n", unsupported);
        sb.AppendFormat(CultureInfo.InvariantCulture, "- Rejected (AWQ/GGUF): {0}\n", rejected);
        sb.AppendFormat(CultureInfo.InvariantCulture, "- Discovery failed: {0}\n", discoveryFailed);
        sb.AppendFormat(CultureInfo.InvariantCulture, "- Unclassified tensors (within ingested): {0}\n", unclassified);
        sb.AppendLine();
        sb.AppendLine("## Per-model status");
        sb.AppendLine();
        sb.AppendLine("| Vendor | Model | Status | Architecture | Tensors | Required adapter |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (DonorManifest m in manifests.OrderBy(m => m.Vendor, StringComparer.Ordinal).ThenBy(m => m.Model, StringComparer.Ordinal))
        {
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "| {0} | {1} | {2} | {3} | {4} | {5} |\n",
                m.Vendor, m.Model, m.Status,
                m.ArchitectureClass ?? "-",
                m.TensorCount,
                m.RequiredAdapter ?? "-");
        }
        return sb.ToString();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Donor package rejected at {PackageRoot}: {Reason}")]
    private static partial void LogPackageRejected(ILogger logger, string packageRoot, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Donor package discovery failed at {PackageRoot}: {Reason}")]
    private static partial void LogPackageDiscoveryFailed(ILogger logger, string packageRoot, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Config read failed for {PackageRoot}: {Reason}")]
    private static partial void LogConfigReadFailed(ILogger logger, string packageRoot, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tensor enumeration failed for {PackageRoot}: {Reason}")]
    private static partial void LogEnumerateFailed(ILogger logger, string packageRoot, string reason);

    private sealed class EmptyConfigSnapshot : IConfigSnapshot
    {
        public IReadOnlyDictionary<string, object?> RawValues { get; } =
            new Dictionary<string, object?>(StringComparer.Ordinal);

        public string GetString(string path, string? defaultValue = null) => defaultValue ?? string.Empty;

        public int? GetInt32(string path) => null;

        public long? GetInt64(string path) => null;

        public double? GetDouble(string path) => null;

        public bool? GetBoolean(string path) => null;

        public IReadOnlyList<string>? GetStringArray(string path) => null;

        public IConfigSnapshot? GetSubConfig(string path) => null;
    }
}

public sealed record CatalogRunSummary(
    string HubRoot,
    string OutputRoot,
    int Discovered,
    int Ingested,
    int UnsupportedV1,
    int Rejected,
    int DiscoveryFailed,
    int UnclassifiedTensors,
    int UniquePatterns);
