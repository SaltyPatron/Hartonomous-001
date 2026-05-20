using System;
using System.Reflection;
using Hartonomous.Core.Data;

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
    public static string InsertSafetensorObservations { get; } = Read("safetensor_observation.insert.sql");

    // Substrate-native bulk-write surfaces (no pg_temp, no drain SQL):
    //   entity, entity_classification, physicality, edge, edge_member
    // Those go through substrate.write_entities / write_entity_classifications /
    // write_physicalities / write_edges / write_edge_members directly via
    // bulk array params from StreamingIngestionPipeline.SubmitXAsync.
    //
    // Surfaces still on the legacy pg_temp + COPY + INSERT-SELECT path:
    public static DrainSqlSpec Junction { get; } = Drain("junction");
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
        return EmbeddedSqlResource.Read(assembly, fileName, "ingestion");
    }
}
