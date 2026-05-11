using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Hartonomous.Engine.Ingestion;

internal static class IngestionSql
{
    public static string DrainSessionSettings { get; } = Read("drain_session_settings.sql");
    public static string PostPassSessionSettings { get; } = Read("post_pass_session_settings.sql");

    // Substrate-aware ingestion: bulk existence-check SQL. One round-trip per
    // kind per chunk; the substrate's btree-on-bytea identity model answers
    // million-element ANY-array probes in well under a second.
    public static string GetExistingEntityHashes { get; } = Read("get_existing_entity_hashes.sql");
    public static string GetExistingEntityClassifications { get; } = Read("get_existing_entity_classifications.sql");
    public static string GetExistingEdges { get; } = Read("get_existing_edges.sql");
    public static string GetExistingEdgeMembers { get; } = Read("get_existing_edge_members.sql");
    public static string GetExistingPhysicalities { get; } = Read("get_existing_physicalities.sql");
    public static string GetExistingSequenceRows { get; } = Read("get_existing_sequence_rows.sql");

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
