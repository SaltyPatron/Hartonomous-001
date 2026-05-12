using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Hartonomous.Core.Data;

public static class EmbeddedSqlResource
{
    public static string Read(Assembly assembly, string fileName, string resourceKind)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKind);

        string resourceSuffix = "." + fileName;
        string resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Missing embedded {resourceKind} SQL resource: {fileName}");

        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Unable to open embedded {resourceKind} SQL resource: {resourceName}");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd().Trim();
    }
}
