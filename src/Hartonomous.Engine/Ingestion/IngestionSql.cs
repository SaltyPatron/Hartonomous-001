using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Hartonomous.Engine.Ingestion;

internal static class IngestionSql
{
    public static string DrainSessionSettings { get; } = Read("drain_session_settings.sql");
    public static string PostPassSessionSettings { get; } = Read("post_pass_session_settings.sql");

    public static DrainSqlSpec Entity { get; } = Drain("entity");
    public static DrainSqlSpec EntityClassification { get; } = Drain("entity_classification");
    public static DrainSqlSpec Edge { get; } = Drain("edge");
    public static DrainSqlSpec EdgeMember { get; } = Drain("edge_member");
    public static DrainSqlSpec Junction { get; } = Drain("junction");
    public static DrainSqlSpec Physicality { get; } = Drain("physicality");
    public static DrainSqlSpec Sequence { get; } = Drain("sequence");
    public static DrainSqlSpec EntitySignificance { get; } = Drain("entity_significance");
    public static DrainSqlSpec EdgeSignificance { get; } = Drain("edge_significance");
    public static DrainSqlSpec EntityModelSource { get; } = Drain("entity_model_source");

    private static DrainSqlSpec Drain(string stem)
        => new(
            Read($"{stem}.temp.sql"),
            Read($"{stem}.copy.sql"),
            Read($"{stem}.truncate.sql"),
            Read($"{stem}.drain.sql"));

    private static string Read(string fileName)
    {
        Assembly assembly = typeof(IngestionSql).Assembly;
        string resourceSuffix = "." + fileName;
        string resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Missing embedded ingestion SQL resource: {fileName}");

        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Unable to open embedded ingestion SQL resource: {resourceName}");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd().Trim();
    }
}