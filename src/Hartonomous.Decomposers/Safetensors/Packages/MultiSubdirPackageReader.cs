using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Packages;

public sealed partial class MultiSubdirPackageReader : BaseDonorPackageReader
{
    private const string ModelIndexFileName = "model_index.json";
    private const string ConfigFileName = "config.json";
    private const string SchedulerConfigFileName = "scheduler_config.json";

    private static readonly string[] WeightFilePatterns =
    {
        "*.safetensors",
        "pytorch_model.bin",
    };

    private readonly Func<string, ILogger, IDonorPackageReader> _childReaderFactory;

    private readonly object _initGate = new();

    private Dictionary<string, IDonorPackageReader>? _childrenByComponent;
    private IReadOnlyList<TensorMetadata>? _cachedMetadata;
    private IConfigSnapshot? _cachedConfig;
    private JsonDocument? _modelIndexDoc;
    private bool _disposed;

    public MultiSubdirPackageReader(
        string packageRoot,
        ILogger<MultiSubdirPackageReader> logger,
        Func<string, ILogger, IDonorPackageReader> childReaderFactory)
        : base(packageRoot, logger)
    {
        ArgumentNullException.ThrowIfNull(childReaderFactory);
        _childReaderFactory = childReaderFactory;
    }

    public override string PackageFormat => "multi_subdir";

    public static bool CanHandle(string packageRoot)
    {
        if (string.IsNullOrWhiteSpace(packageRoot) || !Directory.Exists(packageRoot))
        {
            return false;
        }

        if (File.Exists(Path.Combine(packageRoot, ModelIndexFileName)))
        {
            return true;
        }

        foreach (string pattern in WeightFilePatterns)
        {
            if (Directory.EnumerateFiles(packageRoot, pattern, SearchOption.TopDirectoryOnly).Any())
            {
                return false;
            }
        }

        int subdirsWithConfig = 0;
        foreach (string subdir in Directory.EnumerateDirectories(packageRoot))
        {
            if (File.Exists(Path.Combine(subdir, ConfigFileName))
                || File.Exists(Path.Combine(subdir, SchedulerConfigFileName)))
            {
                subdirsWithConfig++;
                if (subdirsWithConfig >= 2)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public override IReadOnlyList<TensorMetadata> EnumerateTensors()
    {
        EnsureInitialized();
        return _cachedMetadata!;
    }

    public override async Task<ReadOnlyMemory<byte>> ReadTensorAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureInitialized();

        int slashIndex = name.IndexOf('/');
        if (slashIndex <= 0 || slashIndex >= name.Length - 1)
        {
            throw new ArgumentException(
                $"Multi-subdir package expects tensor key in the form '<component>/<tensor_name>'. Received '{name}'.",
                nameof(name));
        }

        string component = name.Substring(0, slashIndex);
        string tensorName = name.Substring(slashIndex + 1);

        if (!_childrenByComponent!.TryGetValue(component, out IDonorPackageReader? child))
        {
            throw new KeyNotFoundException(
                $"Component '{component}' is not present in multi-subdir package '{PackageRootInternal}'.");
        }

        return await child.ReadTensorAsync(tensorName, ct).ConfigureAwait(false);
    }

    public override IConfigSnapshot ReadConfig()
    {
        EnsureInitialized();
        return _cachedConfig!;
    }

    public override IReadOnlyList<string> AdditionalArtifacts
    {
        get
        {
            EnsureInitialized();

            var found = new List<string>();
            foreach (KeyValuePair<string, IDonorPackageReader> kvp in _childrenByComponent!.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                string component = kvp.Key;
                IDonorPackageReader child = kvp.Value;
                foreach (string artifact in child.AdditionalArtifacts)
                {
                    found.Add($"{component}/{artifact}");
                }
            }

            string modelIndexPath = Path.Combine(PackageRootInternal, ModelIndexFileName);
            if (File.Exists(modelIndexPath))
            {
                found.Add(ModelIndexFileName);
            }

            // Include scheduler-only subdirectories' config files since their child readers
            // are not constructed and would otherwise be invisible.
            foreach (string subdir in Directory.EnumerateDirectories(PackageRootInternal).OrderBy(p => p, StringComparer.Ordinal))
            {
                string component = Path.GetFileName(subdir);
                if (_childrenByComponent!.ContainsKey(component))
                {
                    continue;
                }
                string schedulerConfig = Path.Combine(subdir, SchedulerConfigFileName);
                if (File.Exists(schedulerConfig))
                {
                    found.Add($"{component}/{SchedulerConfigFileName}");
                }
                string config = Path.Combine(subdir, ConfigFileName);
                if (File.Exists(config))
                {
                    found.Add($"{component}/{ConfigFileName}");
                }
            }

            return found;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_childrenByComponent is not null)
        {
            foreach (IDonorPackageReader child in _childrenByComponent.Values)
            {
                await child.DisposeAsync().ConfigureAwait(false);
            }
            _childrenByComponent.Clear();
        }

        if (_cachedConfig is IDisposable disposableConfig)
        {
            disposableConfig.Dispose();
        }

        _modelIndexDoc?.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureInitialized()
    {
        if (_cachedMetadata is not null)
        {
            return;
        }

        lock (_initGate)
        {
            if (_cachedMetadata is not null)
            {
                return;
            }

            string modelIndexPath = Path.Combine(PackageRootInternal, ModelIndexFileName);
            JsonDocument? modelIndex = null;
            IReadOnlyList<string> componentNames;

            if (File.Exists(modelIndexPath))
            {
                modelIndex = JsonDocument.Parse(File.ReadAllText(modelIndexPath));
                componentNames = ExtractComponentsFromModelIndex(modelIndex);
            }
            else
            {
                componentNames = ScanComponentSubdirectories();
            }

            var children = new Dictionary<string, IDonorPackageReader>(StringComparer.Ordinal);
            var childConfigs = new Dictionary<string, IConfigSnapshot>(StringComparer.Ordinal);

            foreach (string component in componentNames)
            {
                string subdirPath = Path.Combine(PackageRootInternal, component);
                if (!Directory.Exists(subdirPath))
                {
                    continue;
                }

                if (!HasWeightFiles(subdirPath))
                {
                    LogSkippedComponent(Logger, PackageRootInternal, component);
                    continue;
                }

                IDonorPackageReader child = _childReaderFactory(subdirPath, Logger);
                children[component] = child;
                IConfigSnapshot childConfig = child.ReadConfig();
                childConfigs[component] = childConfig;
            }

            var metadata = new List<TensorMetadata>();
            foreach (KeyValuePair<string, IDonorPackageReader> kvp in children.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                string component = kvp.Key;
                IDonorPackageReader child = kvp.Value;
                IReadOnlyList<TensorMetadata> childTensors = child.EnumerateTensors();
                foreach (TensorMetadata tensor in childTensors.OrderBy(t => t.Name, StringComparer.Ordinal))
                {
                    metadata.Add(tensor with { Component = component });
                }
            }

            _modelIndexDoc = modelIndex;
            _childrenByComponent = children;
            _cachedConfig = new CompositeConfigSnapshot(childConfigs, modelIndex);
            _cachedMetadata = metadata;

            LogPackageInitialized(Logger, PackageRootInternal, children.Count, metadata.Count);
        }
    }

    private static List<string> ExtractComponentsFromModelIndex(JsonDocument doc)
    {
        List<string> names = new();
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"'{ModelIndexFileName}' root must be a JSON object.");
        }
        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name.StartsWith('_'))
            {
                continue;
            }
            names.Add(prop.Name);
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private List<string> ScanComponentSubdirectories()
    {
        List<string> names = new();
        foreach (string subdir in Directory.EnumerateDirectories(PackageRootInternal))
        {
            if (File.Exists(Path.Combine(subdir, ConfigFileName))
                || File.Exists(Path.Combine(subdir, SchedulerConfigFileName)))
            {
                names.Add(Path.GetFileName(subdir));
            }
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static bool HasWeightFiles(string subdirPath)
    {
        foreach (string pattern in WeightFilePatterns)
        {
            if (Directory.EnumerateFiles(subdirPath, pattern, SearchOption.TopDirectoryOnly).Any())
            {
                return true;
            }
        }
        return false;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Multi-subdir package initialized: root={PackageRoot} components={ComponentCount} tensors={TensorCount}")]
    private static partial void LogPackageInitialized(ILogger logger, string packageRoot, int componentCount, int tensorCount);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Multi-subdir package skipped weightless component: root={PackageRoot} component={Component}")]
    private static partial void LogSkippedComponent(ILogger logger, string packageRoot, string component);

    private sealed class CompositeConfigSnapshot : IConfigSnapshot, IDisposable
    {
        private readonly IReadOnlyDictionary<string, IConfigSnapshot> _children;
        private readonly JsonDocument? _modelIndex;

        public CompositeConfigSnapshot(IReadOnlyDictionary<string, IConfigSnapshot> children, JsonDocument? modelIndex)
        {
            _children = children;
            _modelIndex = modelIndex;
        }

        public IReadOnlyDictionary<string, object?> RawValues
        {
            get
            {
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (_modelIndex is not null && _modelIndex.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty prop in _modelIndex.RootElement.EnumerateObject())
                    {
                        map[prop.Name] = MaterializePrimitive(prop.Value);
                    }
                }
                foreach (KeyValuePair<string, IConfigSnapshot> kvp in _children)
                {
                    map[kvp.Key] = kvp.Value;
                }
                return map;
            }
        }

        public string GetString(string path, string? defaultValue = null)
        {
            (IConfigSnapshot? child, string? remainder) = ResolveComponentPath(path);
            if (child is not null && remainder is not null)
            {
                return child.GetString(remainder, defaultValue);
            }
            return GetFromModelIndexString(path, defaultValue);
        }

        public int? GetInt32(string path)
        {
            (IConfigSnapshot? child, string? remainder) = ResolveComponentPath(path);
            if (child is not null && remainder is not null)
            {
                return child.GetInt32(remainder);
            }
            if (TryResolveModelIndex(path, out JsonElement element) && element.ValueKind == JsonValueKind.Number)
            {
                return element.TryGetInt32(out int v) ? v : null;
            }
            return null;
        }

        public long? GetInt64(string path)
        {
            (IConfigSnapshot? child, string? remainder) = ResolveComponentPath(path);
            if (child is not null && remainder is not null)
            {
                return child.GetInt64(remainder);
            }
            if (TryResolveModelIndex(path, out JsonElement element) && element.ValueKind == JsonValueKind.Number)
            {
                return element.TryGetInt64(out long v) ? v : null;
            }
            return null;
        }

        public double? GetDouble(string path)
        {
            (IConfigSnapshot? child, string? remainder) = ResolveComponentPath(path);
            if (child is not null && remainder is not null)
            {
                return child.GetDouble(remainder);
            }
            if (TryResolveModelIndex(path, out JsonElement element) && element.ValueKind == JsonValueKind.Number)
            {
                return element.TryGetDouble(out double v) ? v : null;
            }
            return null;
        }

        public bool? GetBoolean(string path)
        {
            (IConfigSnapshot? child, string? remainder) = ResolveComponentPath(path);
            if (child is not null && remainder is not null)
            {
                return child.GetBoolean(remainder);
            }
            if (TryResolveModelIndex(path, out JsonElement element))
            {
                return element.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
            }
            return null;
        }

        public IReadOnlyList<string>? GetStringArray(string path)
        {
            (IConfigSnapshot? child, string? remainder) = ResolveComponentPath(path);
            if (child is not null && remainder is not null)
            {
                return child.GetStringArray(remainder);
            }
            if (TryResolveModelIndex(path, out JsonElement element) && element.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>(element.GetArrayLength());
                foreach (JsonElement item in element.EnumerateArray())
                {
                    list.Add(item.ValueKind switch
                    {
                        JsonValueKind.String => item.GetString() ?? string.Empty,
                        _ => item.GetRawText(),
                    });
                }
                return list;
            }
            return null;
        }

        public IConfigSnapshot? GetSubConfig(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            if (_children.TryGetValue(path, out IConfigSnapshot? whole))
            {
                return whole;
            }

            (IConfigSnapshot? child, string? remainder) = ResolveComponentPath(path);
            if (child is not null && remainder is not null)
            {
                return child.GetSubConfig(remainder);
            }

            return null;
        }

        public void Dispose()
        {
            foreach (IConfigSnapshot child in _children.Values)
            {
                if (child is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            _modelIndex?.Dispose();
        }

        private (IConfigSnapshot? Child, string? Remainder) ResolveComponentPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return (null, null);
            }
            int dotIndex = path.IndexOf('.');
            if (dotIndex <= 0 || dotIndex >= path.Length - 1)
            {
                return (null, null);
            }
            string head = path.Substring(0, dotIndex);
            string remainder = path.Substring(dotIndex + 1);
            return _children.TryGetValue(head, out IConfigSnapshot? child)
                ? (child, remainder)
                : (null, null);
        }

        private string GetFromModelIndexString(string path, string? defaultValue)
        {
            if (!TryResolveModelIndex(path, out JsonElement element))
            {
                return defaultValue ?? string.Empty;
            }
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? defaultValue ?? string.Empty,
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => defaultValue ?? string.Empty,
                _ => element.GetRawText(),
            };
        }

        private bool TryResolveModelIndex(string path, out JsonElement element)
        {
            element = default;
            if (_modelIndex is null || string.IsNullOrEmpty(path))
            {
                return false;
            }

            JsonElement current = _modelIndex.RootElement;
            int start = 0;
            for (int i = 0; i <= path.Length; i++)
            {
                if (i == path.Length || path[i] == '.')
                {
                    if (current.ValueKind != JsonValueKind.Object)
                    {
                        return false;
                    }
                    string segment = path.Substring(start, i - start);
                    if (!current.TryGetProperty(segment, out JsonElement next))
                    {
                        return false;
                    }
                    current = next;
                    start = i + 1;
                }
            }

            element = current;
            return true;
        }

        private static object? MaterializePrimitive(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out long l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        };
    }
}
