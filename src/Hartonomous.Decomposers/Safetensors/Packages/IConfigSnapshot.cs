using System.Collections.Generic;

namespace Hartonomous.Decomposers.Safetensors.Packages;

public interface IConfigSnapshot
{
    IReadOnlyDictionary<string, object?> RawValues { get; }

    string GetString(string path, string? defaultValue = null);

    int? GetInt32(string path);

    long? GetInt64(string path);

    double? GetDouble(string path);

    bool? GetBoolean(string path);

    IReadOnlyList<string>? GetStringArray(string path);

    IConfigSnapshot? GetSubConfig(string path);
}
