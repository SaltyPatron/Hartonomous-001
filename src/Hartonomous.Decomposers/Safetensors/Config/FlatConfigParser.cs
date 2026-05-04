using System.Text.Json;
using Hartonomous.Decomposers.Safetensors.Packages;

namespace Hartonomous.Decomposers.Safetensors.Config;

public sealed class FlatConfigParser : IConfigParser
{
    public bool CanParse(string packageRoot)
    {
        string configPath = Path.Combine(packageRoot, "config.json");
        if (!File.Exists(configPath))
        {
            return false;
        }

        foreach (string subdir in Directory.EnumerateDirectories(packageRoot))
        {
            if (File.Exists(Path.Combine(subdir, "config.json")))
            {
                return false;
            }
        }

        return true;
    }

    public async Task<IConfigSnapshot> ParseAsync(string packageRoot, CancellationToken ct)
    {
        string configPath = Path.Combine(packageRoot, "config.json");
        await using FileStream fs = new(
            configPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        JsonDocument doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct).ConfigureAwait(false);
        return new FlatConfigSnapshot(doc.RootElement.Clone(), configPath, ownsDocument: true, doc);
    }

    private sealed class FlatConfigSnapshot : IConfigSnapshot, IDisposable
    {
        private readonly JsonElement _root;
        private readonly string _sourcePath;
        private readonly bool _ownsDocument;
        private readonly JsonDocument? _document;

        public FlatConfigSnapshot(JsonElement root, string sourcePath, bool ownsDocument, JsonDocument? document)
        {
            _root = root;
            _sourcePath = sourcePath;
            _ownsDocument = ownsDocument;
            _document = document;
        }

        public string SourcePath => _sourcePath;

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
            if (!TryResolve(path, out JsonElement element))
            {
                return null;
            }
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int v))
            {
                return v;
            }
            return null;
        }

        public long? GetInt64(string path)
        {
            if (!TryResolve(path, out JsonElement element))
            {
                return null;
            }
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long v))
            {
                return v;
            }
            return null;
        }

        public double? GetDouble(string path)
        {
            if (!TryResolve(path, out JsonElement element))
            {
                return null;
            }
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double v))
            {
                return v;
            }
            return null;
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
            if (!TryResolve(path, out JsonElement element))
            {
                return null;
            }
            if (element.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            List<string> result = new(element.GetArrayLength());
            foreach (JsonElement item in element.EnumerateArray())
            {
                result.Add(item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString() ?? string.Empty,
                    JsonValueKind.Null => string.Empty,
                    _ => item.GetRawText(),
                });
            }
            return result;
        }

        public IConfigSnapshot? GetSubConfig(string path)
        {
            if (!TryResolve(path, out JsonElement element))
            {
                return null;
            }
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return new FlatConfigSnapshot(element.Clone(), _sourcePath + ":" + path, ownsDocument: false, document: null);
        }

        public IReadOnlyDictionary<string, object?> RawValues
        {
            get
            {
                Dictionary<string, object?> map = new(StringComparer.Ordinal);
                if (_root.ValueKind != JsonValueKind.Object)
                {
                    return map;
                }
                foreach (JsonProperty prop in _root.EnumerateObject())
                {
                    map[prop.Name] = MaterializePrimitive(prop.Value);
                }
                return map;
            }
        }

        private bool TryResolve(string path, out JsonElement element)
        {
            element = default;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            JsonElement current = _root;
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

        public void Dispose()
        {
            if (_ownsDocument)
            {
                _document?.Dispose();
            }
        }
    }
}
