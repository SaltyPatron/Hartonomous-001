using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Hartonomous.Engine.Ingestion;
using Xunit;

namespace Hartonomous.Engine.Tests.Ingestion;

public sealed class DrainSqlOrderingTests
{
    public static IEnumerable<object[]> DrainOrderingRequirements()
    {
        // entity.drain.sql uses ON CONFLICT DO UPDATE with COALESCE for
        // centroid back-fill (first non-NULL wins by Merkle invariant), not
        // DO NOTHING — a producer that didn't have the centroid in hand can
        // be followed by one that does, and the entity row picks it up.
        // Content identity is still hash-only; the update is content-
        // preserving denormalization.
        yield return Requirement("entity", "order by hash", "on conflict (hash) do update");
        yield return Requirement("entity_classification", "order by entity_hash, entity_type_id, provenance_id", "on conflict (entity_hash, entity_type_id, provenance_id) do nothing");
        yield return Requirement("edge", "order by edge_type_id, hash, (geometry_payload is null), provenance_id, geometry_payload", "on conflict (edge_type_id, hash) do nothing");
        yield return Requirement("edge_member", "order by edge_type_id, edge_hash, entity_hash, edge_role_id, role_position", "on conflict (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position) do nothing");
        yield return Requirement("junction", "order by entity_hash, pos_id, attestation_type_id, mu desc", "on conflict (entity_hash, pos_id, attestation_type_id) do nothing");
        yield return Requirement("junction", "order by entity_hash, lexname_id", "on conflict (entity_hash, lexname_id) do nothing");
        yield return Requirement("junction", "order by entity_hash, language_id", "on conflict (entity_hash, language_id) do nothing");
        yield return Requirement("junction", "order by entity_hash, morph_feature_id", "on conflict (entity_hash, morph_feature_id) do nothing");
        yield return Requirement("junction", "order by entity_hash, architecture_class_id", "on conflict (entity_hash, architecture_class_id) do nothing");
        yield return Requirement("junction", "order by entity_hash, tensor_role_id", "on conflict (entity_hash, tensor_role_id) do nothing");
        yield return Requirement("junction", "order by entity_hash, deprel_id, attestation_type_id, mu desc", "on conflict (entity_hash, deprel_id, attestation_type_id) do nothing");
        yield return Requirement("physicality", "order by physicality_type_id, entity_hash, content_hash, geometry_payload", "on conflict (physicality_type_id, entity_hash, content_hash) do nothing");
        yield return Requirement("entity_significance", "order by context_type_id, entity_hash, attestation_type_id, mu desc", "on conflict (context_type_id, entity_hash, attestation_type_id) do nothing");
        yield return Requirement("edge_significance", "order by context_type_id, edge_type_id, edge_hash, attestation_type_id, mu desc", "on conflict (context_type_id, edge_type_id, edge_hash, attestation_type_id) do nothing");
        yield return Requirement("entity_model_source", "order by entity_hash, model_source_id", "on conflict (entity_hash, model_source_id) do nothing");
    }

    [Theory]
    [MemberData(nameof(DrainOrderingRequirements))]
    public void DrainSql_OrdersByConflictKeyBeforeUpsert(string stem, string orderBy, string conflictTarget)
    {
        string sql = Normalize(ReadDrainSql(stem));
        int orderIndex = sql.IndexOf(orderBy, StringComparison.Ordinal);
        int conflictIndex = sql.IndexOf(conflictTarget, StringComparison.Ordinal);

        Assert.True(orderIndex >= 0, $"{stem}.drain.sql missing '{orderBy}'.");
        Assert.True(conflictIndex >= 0, $"{stem}.drain.sql missing '{conflictTarget}'.");
        Assert.True(orderIndex < conflictIndex, $"{stem}.drain.sql must sort by the conflict key before the upsert conflict path.");
    }

    [Fact]
    public void DrainSql_UsesExplicitConflictTargets()
    {
        foreach (string stem in DrainStems)
        {
            string sql = Normalize(ReadDrainSql(stem));

            Assert.DoesNotContain("on conflict do nothing", sql, StringComparison.Ordinal);
        }
    }

    private static readonly string[] DrainStems =
    [
        "entity",
        "entity_classification",
        "edge",
        "edge_member",
        "junction",
        "physicality",
        "entity_significance",
        "edge_significance",
        "entity_model_source",
    ];

    private static object[] Requirement(string stem, string orderBy, string conflictTarget)
        => [stem, orderBy, conflictTarget];

    private static string ReadDrainSql(string stem)
    {
        Assembly engine = typeof(StreamingIngestionPipeline).Assembly;
        string suffix = $".Ingestion.Sql.{stem}.drain.sql";
        string? resourceName = Array.Find(engine.GetManifestResourceNames(), name => name.EndsWith(suffix, StringComparison.Ordinal));

        Assert.NotNull(resourceName);
        using Stream stream = engine.GetManifestResourceStream(resourceName)!;
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static string Normalize(string sql)
        => Regex.Replace(sql, @"\s+", " ").Trim().ToLowerInvariant();
}
