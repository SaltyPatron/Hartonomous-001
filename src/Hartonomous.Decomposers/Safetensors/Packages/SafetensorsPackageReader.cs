using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Packages;

public sealed partial class SafetensorsPackageReader : BaseDonorPackageReader
{
    private const string SingleFileName = "model.safetensors";
    private const string ShardIndexFileName = "model.safetensors.index.json";
    private const string ConfigFileName = "config.json";

    private readonly object _enumerationGate = new();
    private readonly object _configGate = new();

    private IReadOnlyList<TensorMetadata>? _cachedMetadata;
    private Dictionary<string, TensorLocation>? _locationByName;
    private IConfigSnapshot? _cachedConfig;

    public SafetensorsPackageReader(string packageRoot, ILogger<SafetensorsPackageReader> logger)
        : base(packageRoot, logger)
    {
    }

    public override string PackageFormat => "safetensors";

    public override IReadOnlyList<TensorMetadata> EnumerateTensors()
    {
        EnsureEnumerated();
        return _cachedMetadata!;
    }

    public override async Task<ReadOnlyMemory<byte>> ReadTensorAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        EnsureEnumerated();

        if (!_locationByName!.TryGetValue(name, out TensorLocation location))
        {
            throw new KeyNotFoundException($"Tensor '{name}' is not present in package '{PackageRootInternal}'.");
        }

        if (location.ByteLength < 0 || location.ByteLength > int.MaxValue)
        {
            throw new NotSupportedException(
                $"Tensor '{name}' has byte length {location.ByteLength} which exceeds int.MaxValue.");
        }

        // Caller must NOT mutate the returned buffer — it is treated as a frozen
        // view onto the on-disk tensor bytes.
        byte[] buffer = new byte[location.ByteLength];

        await using FileStream fs = new(
            location.ShardPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        fs.Seek(location.AbsoluteByteOffset, SeekOrigin.Begin);
        await fs.ReadExactlyAsync(buffer.AsMemory(0, (int)location.ByteLength), ct).ConfigureAwait(false);

        return buffer;
    }

    public override IConfigSnapshot ReadConfig()
    {
        if (_cachedConfig is not null)
        {
            return _cachedConfig;
        }

        lock (_configGate)
        {
            if (_cachedConfig is not null)
            {
                return _cachedConfig;
            }

            string configPath = Path.Combine(PackageRootInternal, ConfigFileName);
            if (!File.Exists(configPath))
            {
                throw new FileNotFoundException(
                    $"Safetensors package at '{PackageRootInternal}' is missing required '{ConfigFileName}'.",
                    configPath);
            }

            string json = File.ReadAllText(configPath);
            JsonDocument doc = JsonDocument.Parse(json);
            _cachedConfig = new SafetensorsConfigSnapshot(doc);
            return _cachedConfig;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_cachedConfig is IDisposable disposable)
        {
            disposable.Dispose();
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureEnumerated()
    {
        if (_cachedMetadata is not null)
        {
            return;
        }

        lock (_enumerationGate)
        {
            if (_cachedMetadata is not null)
            {
                return;
            }

            (List<TensorMetadata> metadata, Dictionary<string, TensorLocation> locations, int shardCount) =
                EnumerateAcrossShards();

            _cachedMetadata = metadata;
            _locationByName = locations;

            LogTensorsEnumerated(Logger, PackageRootInternal, metadata.Count, shardCount);
        }
    }

    private (List<TensorMetadata> Metadata, Dictionary<string, TensorLocation> Locations, int ShardCount)
        EnumerateAcrossShards()
    {
        string indexPath = Path.Combine(PackageRootInternal, ShardIndexFileName);
        IReadOnlyList<string> shardPaths;

        if (File.Exists(indexPath))
        {
            shardPaths = ResolveShardsFromIndex(indexPath);
        }
        else
        {
            shardPaths = ResolveShardsFromDirectoryScan();
        }

        var metadata = new List<TensorMetadata>();
        var locations = new Dictionary<string, TensorLocation>(StringComparer.Ordinal);

        foreach (string shardPath in shardPaths)
        {
            List<SafetensorsTensorInfo> tensors = SafetensorsReader.ReadHeader(shardPath);
            foreach (SafetensorsTensorInfo info in tensors)
            {
                long byteLength = info.EndByte - info.BeginByte;
                int[] shape = ToInt32Shape(info.Shape, info.Name);

                var entry = new TensorMetadata
                {
                    Name = info.Name,
                    Dtype = MapDtype(info.Dtype),
                    Shape = shape,
                    ByteOffset = info.BeginByte,
                    ByteLength = byteLength,
                    Component = null,
                };

                if (!locations.TryAdd(info.Name, new TensorLocation(shardPath, info.BeginByte, byteLength)))
                {
                    throw new InvalidDataException(
                        $"Tensor name '{info.Name}' appears in multiple shards under '{PackageRootInternal}'.");
                }
                metadata.Add(entry);
            }
        }

        return (metadata, locations, shardPaths.Count);
    }

    private List<string> ResolveShardsFromIndex(string indexPath)
    {
        using FileStream fs = new(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using JsonDocument doc = JsonDocument.Parse(fs);

        if (!doc.RootElement.TryGetProperty("weight_map", out JsonElement weightMap)
            || weightMap.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Shard index '{indexPath}' is missing a 'weight_map' object.");
        }

        var uniqueShards = new SortedSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty entry in weightMap.EnumerateObject())
        {
            string? shardName = entry.Value.GetString();
            if (string.IsNullOrWhiteSpace(shardName))
            {
                throw new InvalidDataException(
                    $"Shard index '{indexPath}' has empty filename for tensor '{entry.Name}'.");
            }
            uniqueShards.Add(shardName);
        }

        var resolved = new List<string>(uniqueShards.Count);
        foreach (string shardName in uniqueShards)
        {
            string absolute = Path.Combine(PackageRootInternal, shardName);
            if (!File.Exists(absolute))
            {
                throw new FileNotFoundException(
                    $"Shard '{shardName}' referenced by '{ShardIndexFileName}' is missing from '{PackageRootInternal}'.",
                    absolute);
            }
            resolved.Add(absolute);
        }

        if (resolved.Count == 0)
        {
            throw new InvalidDataException(
                $"Shard index '{indexPath}' contains no shard references.");
        }

        return resolved;
    }

    private IReadOnlyList<string> ResolveShardsFromDirectoryScan()
    {
        string singleCanonical = Path.Combine(PackageRootInternal, SingleFileName);
        if (File.Exists(singleCanonical))
        {
            return new[] { singleCanonical };
        }

        var candidates = new List<string>();
        foreach (string path in Directory.EnumerateFiles(PackageRootInternal, "*.safetensors", SearchOption.TopDirectoryOnly))
        {
            candidates.Add(path);
        }

        if (candidates.Count == 0)
        {
            throw new FileNotFoundException(
                $"No '*.safetensors' file or '{ShardIndexFileName}' found in '{PackageRootInternal}'.");
        }

        if (candidates.Count > 1)
        {
            throw new InvalidDataException(
                $"Multiple '*.safetensors' files found in '{PackageRootInternal}' but no '{ShardIndexFileName}' to disambiguate them.");
        }

        return candidates;
    }

    private static int[] ToInt32Shape(long[] shape, string tensorName)
    {
        int[] result = new int[shape.Length];
        for (int i = 0; i < shape.Length; i++)
        {
            long dim = shape[i];
            if (dim < 0 || dim > int.MaxValue)
            {
                throw new NotSupportedException(
                    $"Tensor '{tensorName}' shape dimension {i} = {dim} exceeds int.MaxValue.");
            }
            result[i] = (int)dim;
        }
        return result;
    }

    private static string MapDtype(SafetensorsDtype dtype) => dtype switch
    {
        SafetensorsDtype.F32 => "F32",
        SafetensorsDtype.F64 => "F64",
        SafetensorsDtype.F16 => "F16",
        SafetensorsDtype.BF16 => "BF16",
        SafetensorsDtype.I8 => "I8",
        SafetensorsDtype.U8 => "U8",
        SafetensorsDtype.I16 => "I16",
        SafetensorsDtype.U16 => "U16",
        SafetensorsDtype.I32 => "I32",
        SafetensorsDtype.U32 => "U32",
        SafetensorsDtype.I64 => "I64",
        SafetensorsDtype.U64 => "U64",
        SafetensorsDtype.Bool => "BOOL",
        SafetensorsDtype.F8E4M3 => "F8_E4M3",
        SafetensorsDtype.F8E5M2 => "F8_E5M2",
        _ => throw new NotSupportedException($"Unmapped safetensors dtype '{dtype}'."),
    };

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Safetensors package enumerated: root={PackageRoot} tensors={TensorCount} shards={ShardCount}")]
    private static partial void LogTensorsEnumerated(ILogger logger, string packageRoot, int tensorCount, int shardCount);

    private readonly record struct TensorLocation(string ShardPath, long AbsoluteByteOffset, long ByteLength);

    private sealed class SafetensorsConfigSnapshot : IConfigSnapshot, IDisposable
    {
        private readonly JsonDocument _doc;

        public SafetensorsConfigSnapshot(JsonDocument doc)
        {
            _doc = doc;
        }

        public IReadOnlyDictionary<string, object?> RawValues
        {
            get
            {
                Dictionary<string, object?> map = new(StringComparer.Ordinal);
                if (_doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return map;
                }
                foreach (JsonProperty prop in _doc.RootElement.EnumerateObject())
                {
                    map[prop.Name] = MaterializePrimitive(prop.Value);
                }
                return map;
            }
        }

        public string GetString(string path, string? defaultValue = null)
        {
            if (!TryResolve(path, out JsonElement element))
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

        public int? GetInt32(string path)
        {
            if (!TryResolve(path, out JsonElement element) || element.ValueKind != JsonValueKind.Number)
            {
                return null;
            }
            return element.TryGetInt32(out int v) ? v : null;
        }

        public long? GetInt64(string path)
        {
            if (!TryResolve(path, out JsonElement element) || element.ValueKind != JsonValueKind.Number)
            {
                return null;
            }
            return element.TryGetInt64(out long v) ? v : null;
        }

        public double? GetDouble(string path)
        {
            if (!TryResolve(path, out JsonElement element) || element.ValueKind != JsonValueKind.Number)
            {
                return null;
            }
            return element.TryGetDouble(out double v) ? v : null;
        }

        public bool? GetBoolean(string path)
        {
            if (!TryResolve(path, out JsonElement element))
            {
                return null;
            }
            return element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }

        public IReadOnlyList<string>? GetStringArray(string path)
        {
            if (!TryResolve(path, out JsonElement element) || element.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
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

        public IConfigSnapshot? GetSubConfig(string path)
        {
            if (!TryResolve(path, out JsonElement element) || element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            // Reparse the sub-element into its own JsonDocument so the snapshot owns its memory.
            JsonDocument sub = JsonDocument.Parse(element.GetRawText());
            return new SafetensorsConfigSnapshot(sub);
        }

        public void Dispose() => _doc.Dispose();

        private bool TryResolve(string path, out JsonElement element)
        {
            element = default;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            JsonElement current = _doc.RootElement;
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
