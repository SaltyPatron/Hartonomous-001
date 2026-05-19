using System.Text;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Decomposers;

/// <summary>
/// Cross-link attestation helpers (Step I of ancient-launching-papert plan):
/// every text-bearing decomposer fires has_language / has_script attestation
/// events on the unified substrate.edge_significance surface so cross-source
/// language-coverage consensus accumulates on shared content-addressed
/// identities (per AP-8 unified-Glicko-surface correction + the
/// universal-cross-source-attestation framing).
///
/// Decomposers continue to emit their existing junction rows (entity_language,
/// entity_pos, etc.) — those remain as denormalized analytics caches per
/// the AP-8 correction. The edge emit here is the authoritative surface.
///
/// language_name entity identity = BLAKE3 over the ISO 639-3 3-letter code in
/// UTF-8. Stable across all decomposers; cross-source attestation collapses
/// onto the same language_name entity.
/// </summary>
public static class CrossLinkAttestation
{
    /// <summary>
    /// Emit both the entity_language junction row (legacy analytics cache)
    /// and the has_language edge attestation (unified Glicko surface).
    /// </summary>
    public static void EmitLanguageAttestation(
        IIngestionBatch batch,
        EntityHandle entity,
        string isoCode,
        int languageReferenceId,
        string provenanceCode)
    {
        if (string.IsNullOrEmpty(isoCode)) { return; }

        // Legacy junction (denormalized analytics cache per AP-8)
        batch.AddJunction("entity_language", entity, languageReferenceId);

        // Unified Glicko surface — has_language edge
        Hash32 langHash = Blake3.Hash32(Encoding.UTF8.GetBytes(isoCode));
        EntityHandle langHandle = batch.AddEntity(langHash, "language_name");
        EdgeMemberSpec[] members =
        [
            new EdgeMemberSpec(entity, "source", 0),
            new EdgeMemberSpec(langHandle, "target", 1),
        ];
        batch.AddEdge(
            "has_language",
            provenanceCode,
            members,
            ReadOnlySpan<EdgeSignificanceSpec>.Empty,
            EdgeArenaRouter.EventsFor("has_language"));
    }
}
