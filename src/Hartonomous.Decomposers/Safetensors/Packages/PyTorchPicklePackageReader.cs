using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Safetensors.Packages;

public sealed partial class PyTorchPicklePackageReader : BaseDonorPackageReader
{
    private const string SingleFileName = "pytorch_model.bin";
    private const string ShardIndexFileName = "pytorch_model.bin.index.json";
    private const string ConfigFileName = "config.json";

    private static readonly string[] FallbackSingleFileCandidates =
    {
        "pytorch_model.bin",
        "consolidated.00.pth",
        "consolidated.00.pt",
    };

    private static readonly string[] CheckpointExtensions = { ".pt", ".pth", ".bin" };

    private readonly object _enumerationGate = new();
    private readonly object _configGate = new();

    private IReadOnlyList<TensorMetadata>? _cachedMetadata;
    private Dictionary<string, TensorLocation>? _locationByName;
    private Dictionary<string, Dictionary<string, string>>? _archiveEntryPathByShard;
    private IConfigSnapshot? _cachedConfig;

    public PyTorchPicklePackageReader(string packageRoot, ILogger<PyTorchPicklePackageReader> logger)
        : base(packageRoot, logger)
    {
    }

    public override string PackageFormat => "pytorch_pickle";

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

        Dictionary<string, string> entryMap = GetOrLoadArchiveEntryMap(location.ShardPath);
        if (!entryMap.TryGetValue(location.StorageKey, out string? entryPath))
        {
            throw new InvalidDataException(
                $"Storage key '{location.StorageKey}' for tensor '{name}' was not found in archive '{location.ShardPath}'.");
        }

        byte[] buffer = new byte[location.ByteLength];

        await using FileStream fs = new(
            location.ShardPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        using ZipArchive archive = new(fs, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry entry = archive.GetEntry(entryPath)
            ?? throw new InvalidDataException(
                $"Archive entry '{entryPath}' disappeared from '{location.ShardPath}'.");

        await using Stream entryStream = entry.Open();
        await SkipBytesAsync(entryStream, location.StorageByteOffset, ct).ConfigureAwait(false);
        await entryStream.ReadExactlyAsync(buffer.AsMemory(0, (int)location.ByteLength), ct).ConfigureAwait(false);

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
            if (File.Exists(configPath))
            {
                using FileStream fs = new(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                JsonDocument doc = JsonDocument.Parse(fs);
                _cachedConfig = new PyTorchConfigSnapshot(doc);
            }
            else
            {
                _cachedConfig = EmptyConfigSnapshot.Instance;
            }
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
            _archiveEntryPathByShard = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

            LogTensorsEnumerated(Logger, PackageRootInternal, metadata.Count, shardCount);
        }
    }

    private (List<TensorMetadata> Metadata, Dictionary<string, TensorLocation> Locations, int ShardCount)
        EnumerateAcrossShards()
    {
        IReadOnlyList<string> shardPaths;
        IReadOnlyDictionary<string, string>? tensorToShard = null;

        string indexPath = Path.Combine(PackageRootInternal, ShardIndexFileName);
        if (File.Exists(indexPath))
        {
            (shardPaths, tensorToShard) = ResolveShardsFromIndex(indexPath);
        }
        else
        {
            shardPaths = ResolveShardsFromDirectoryScan();
        }

        var metadata = new List<TensorMetadata>();
        var locations = new Dictionary<string, TensorLocation>(StringComparer.Ordinal);

        foreach (string shardPath in shardPaths)
        {
            IReadOnlyList<PythonPickleParser.PickleTensorEntry> entries = PythonPickleParser.ParsePackage(shardPath);
            foreach (PythonPickleParser.PickleTensorEntry entry in entries)
            {
                if (tensorToShard is not null
                    && tensorToShard.TryGetValue(entry.Name, out string? expectedShardName))
                {
                    string expectedShardPath = Path.Combine(PackageRootInternal, expectedShardName);
                    if (!string.Equals(
                            Path.GetFullPath(expectedShardPath),
                            Path.GetFullPath(shardPath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        // Tensor belongs to a different shard per the index — skip; it will be picked
                        // up when we walk that shard.
                        continue;
                    }
                }

                int dtypeSize = DtypeByteSize(entry.DtypeCanonical);
                long byteOffset = checked(entry.StorageElementOffset * dtypeSize);

                var meta = new TensorMetadata
                {
                    Name = entry.Name,
                    Dtype = entry.DtypeCanonical,
                    Shape = entry.Shape,
                    ByteOffset = byteOffset,
                    ByteLength = entry.ByteLength,
                    Component = null,
                };

                if (!locations.TryAdd(
                        entry.Name,
                        new TensorLocation(shardPath, entry.StorageKey, byteOffset, entry.ByteLength)))
                {
                    throw new InvalidDataException(
                        $"Tensor name '{entry.Name}' appears in multiple shards under '{PackageRootInternal}'.");
                }
                metadata.Add(meta);
            }
        }

        return (metadata, locations, shardPaths.Count);
    }

    private (IReadOnlyList<string> ShardPaths, IReadOnlyDictionary<string, string> TensorToShard)
        ResolveShardsFromIndex(string indexPath)
    {
        using FileStream fs = new(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using JsonDocument doc = JsonDocument.Parse(fs);

        if (!doc.RootElement.TryGetProperty("weight_map", out JsonElement weightMap)
            || weightMap.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Shard index '{indexPath}' is missing a 'weight_map' object.");
        }

        var tensorToShard = new Dictionary<string, string>(StringComparer.Ordinal);
        var uniqueShards = new SortedSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty entry in weightMap.EnumerateObject())
        {
            string? shardName = entry.Value.GetString();
            if (string.IsNullOrWhiteSpace(shardName))
            {
                throw new InvalidDataException(
                    $"Shard index '{indexPath}' has empty filename for tensor '{entry.Name}'.");
            }
            tensorToShard[entry.Name] = shardName;
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

        return (resolved, tensorToShard);
    }

    private IReadOnlyList<string> ResolveShardsFromDirectoryScan()
    {
        foreach (string candidateName in FallbackSingleFileCandidates)
        {
            string absolute = Path.Combine(PackageRootInternal, candidateName);
            if (File.Exists(absolute))
            {
                return new[] { absolute };
            }
        }

        var matches = new List<string>();
        foreach (string path in Directory.EnumerateFiles(PackageRootInternal, "*", SearchOption.TopDirectoryOnly))
        {
            string ext = Path.GetExtension(path);
            foreach (string allowed in CheckpointExtensions)
            {
                if (string.Equals(ext, allowed, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(path);
                    break;
                }
            }
        }

        if (matches.Count == 0)
        {
            throw new FileNotFoundException(
                $"No PyTorch checkpoint (*.pt, *.pth, *.bin) or '{ShardIndexFileName}' found in '{PackageRootInternal}'.");
        }

        if (matches.Count > 1)
        {
            throw new InvalidDataException(
                $"Multiple PyTorch checkpoint files found in '{PackageRootInternal}' but no '{ShardIndexFileName}' to disambiguate them.");
        }

        return matches;
    }

    private Dictionary<string, string> GetOrLoadArchiveEntryMap(string shardPath)
    {
        if (_archiveEntryPathByShard!.TryGetValue(shardPath, out Dictionary<string, string>? cached))
        {
            return cached;
        }

        lock (_archiveEntryPathByShard)
        {
            if (_archiveEntryPathByShard.TryGetValue(shardPath, out cached))
            {
                return cached;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            using FileStream fs = new(shardPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using ZipArchive archive = new(fs, ZipArchiveMode.Read, leaveOpen: false);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string full = entry.FullName;
                int dataIdx = full.IndexOf("/data/", StringComparison.Ordinal);
                if (dataIdx < 0)
                {
                    continue;
                }
                string storageKey = full[(dataIdx + "/data/".Length)..];
                if (storageKey.Length == 0 || storageKey.Contains('/'))
                {
                    continue;
                }
                map[storageKey] = full;
            }

            _archiveEntryPathByShard[shardPath] = map;
            return map;
        }
    }

    private static async Task SkipBytesAsync(Stream stream, long count, CancellationToken ct)
    {
        if (count <= 0)
        {
            return;
        }

        const int ScratchSize = 8192;
        byte[] scratch = new byte[ScratchSize];
        long remaining = count;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(ScratchSize, remaining);
            int read = await stream.ReadAsync(scratch.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new EndOfStreamException(
                    $"Archive entry ended after skipping {count - remaining} bytes; expected to skip {count}.");
            }
            remaining -= read;
        }
    }

    private static int DtypeByteSize(string dtypeCanonical) => dtypeCanonical switch
    {
        "F32" => 4,
        "F64" => 8,
        "F16" => 2,
        "BF16" => 2,
        "I8" => 1,
        "U8" => 1,
        "I16" => 2,
        "U16" => 2,
        "I32" => 4,
        "U32" => 4,
        "I64" => 8,
        "U64" => 8,
        "BOOL" => 1,
        "F8_E4M3" => 1,
        "F8_E5M2" => 1,
        _ => throw new NotSupportedException($"No byte size known for canonical dtype '{dtypeCanonical}'."),
    };

    [LoggerMessage(Level = LogLevel.Information,
        Message = "PyTorch pickle package enumerated: root={PackageRoot} tensors={TensorCount} shards={ShardCount}")]
    private static partial void LogTensorsEnumerated(ILogger logger, string packageRoot, int tensorCount, int shardCount);

    private readonly record struct TensorLocation(
        string ShardPath,
        string StorageKey,
        long StorageByteOffset,
        long ByteLength);

    private sealed class PyTorchConfigSnapshot : IConfigSnapshot, IDisposable
    {
        private readonly JsonDocument _doc;

        public PyTorchConfigSnapshot(JsonDocument doc)
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
            => TryResolve(path, out JsonElement element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetInt32(out int v) ? v : null;

        public long? GetInt64(string path)
            => TryResolve(path, out JsonElement element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetInt64(out long v) ? v : null;

        public double? GetDouble(string path)
            => TryResolve(path, out JsonElement element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetDouble(out double v) ? v : null;

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
            JsonDocument sub = JsonDocument.Parse(element.GetRawText());
            return new PyTorchConfigSnapshot(sub);
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

    private sealed class EmptyConfigSnapshot : IConfigSnapshot
    {
        public static readonly EmptyConfigSnapshot Instance = new();

        private static readonly IReadOnlyDictionary<string, object?> EmptyMap =
            new Dictionary<string, object?>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, object?> RawValues => EmptyMap;

        public string GetString(string path, string? defaultValue = null) => defaultValue ?? string.Empty;

        public int? GetInt32(string path) => null;

        public long? GetInt64(string path) => null;

        public double? GetDouble(string path) => null;

        public bool? GetBoolean(string path) => null;

        public IReadOnlyList<string>? GetStringArray(string path) => null;

        public IConfigSnapshot? GetSubConfig(string path) => null;
    }
}
