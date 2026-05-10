using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Data;

/// <summary>
/// Substrate-side reads required by Phase C.1 synthesizers. The synthesizers
/// don't construct SQL — they call this interface; implementations live in
/// Hartonomous.Engine.Data and route to named SQL functions under
/// <c>substrate.*</c> (per AP-2).
///
/// The read surface is shaped around the four operations every synthesizer
/// needs:
/// <list type="number">
/// <item>Pull all attestation edges of a given (edge_type, attestation_type)
/// optionally filtered by source-model-set and arena, returning the
/// participants + per-arena consensus mu.</item>
/// <item>Pull per-(model, token) firefly POINTZMs for the embedding
/// synthesizer's reverse-projection.</item>
/// <item>Pull tensor-attached LINESTRINGZM physicality for layer-norm /
/// rope-freq synthesis.</item>
/// <item>Resolve word_form entity hashes for a tokenizer's vocab in the
/// target architecture (so synthesizer output rows map to the right
/// attestation participants).</item>
/// </list>
///
/// Per Phase C.0 / C.1. Phase B.2 audits the SQL function coverage that
/// backs these reads.
/// </summary>
public interface ISubstrateAttestationReader
{
    /// <summary>
    /// Pull token-pair attestation edges of the given (edge_type,
    /// attestation_type), optionally restricted to a source-model subset.
    /// Returns each edge with its participants and per-arena consensus mu
    /// values (one row per edge × arena combination matching the filter).
    /// </summary>
    /// <param name="edgeTypeCode">e.g. "model_attention_pattern",
    /// "model_concept_similarity", "model_ffn_factor".</param>
    /// <param name="attestationTypeCode">e.g. "model_attention_qk_pattern",
    /// "model_input_embedding".</param>
    /// <param name="arenaCodes">Arenas to consult. Per-arena mu is returned
    /// for each. NULL = include every arena present on the edge.</param>
    /// <param name="sourceModelIds">When non-null, restrict to attestations
    /// fired by these model_source_ids only. Mode 1 single-source pass-through.</param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<AttestationRow> ReadAttestationsAsync(
        string edgeTypeCode,
        string attestationTypeCode,
        IReadOnlyList<string>? arenaCodes,
        IReadOnlyList<long>? sourceModelIds,
        CancellationToken ct);

    /// <summary>
    /// Pull per-(model, token) firefly POINTZMs attached to the existing
    /// word_form entity. One row per (model_source_id, token) pair where
    /// the model has emitted an embedding firefly.
    /// </summary>
    IAsyncEnumerable<FireflyRow> ReadFirefliesAsync(
        IReadOnlyList<EntityHandle> tokenEntities,
        IReadOnlyList<long>? sourceModelIds,
        CancellationToken ct);

    /// <summary>
    /// Pull tensor-attached LINESTRINGZM physicality (the canonical
    /// "contour" partition or a custom partition code).
    /// </summary>
    Task<IReadOnlyDictionary<EntityHandle, double[]>> ReadTensorContoursAsync(
        IReadOnlyList<EntityHandle> tensorEntities,
        string physicalityTypeCode,
        CancellationToken ct);

    /// <summary>
    /// Resolve the word_form entity hashes for the target architecture's
    /// vocabulary. The recomposer feeds in the tokenizer's vocab byte
    /// stream; this implementation routes through the canonical text
    /// decomposer to produce content-addressed hashes that match the
    /// substrate's existing word_form entities.
    /// </summary>
    Task<IReadOnlyList<byte[]>> ResolveVocabHashesAsync(
        IReadOnlyList<byte[]> tokenBytes,
        CancellationToken ct);
}

/// <summary>
/// One token-pair attestation edge from substrate, with consensus mu per
/// arena requested.
/// </summary>
/// <param name="EdgeTypeCode">edge_type.code (e.g. "model_attention_pattern").</param>
/// <param name="EdgeHash">edge identity (BLAKE3 of edge_type + ordered participant hashes).</param>
/// <param name="Participants">Role-ordered participant entity handles (typically two word_form tokens).</param>
/// <param name="ArenaMu">Per-arena consensus mu values for this edge under
/// the requested attestation_type. Map keyed by arena code.</param>
/// <param name="GamesAggregate">Sum of Glicko-2 games count across arenas
/// for diagnostic ranking (more games = tighter consensus).</param>
public sealed record AttestationRow(
    string EdgeTypeCode,
    byte[] EdgeHash,
    IReadOnlyList<EntityHandle> Participants,
    IReadOnlyDictionary<string, double> ArenaMu,
    int GamesAggregate);

/// <summary>
/// One per-(model, token) firefly POINTZM physicality.
/// </summary>
/// <param name="TokenEntity">The word_form entity the firefly is attached to.</param>
/// <param name="ModelSourceId">The model that emitted this firefly.</param>
/// <param name="X">4D X coordinate.</param>
/// <param name="Y">4D Y coordinate.</param>
/// <param name="Z">4D Z coordinate.</param>
/// <param name="M">4D M coordinate (typically magnitude).</param>
public sealed record FireflyRow(
    EntityHandle TokenEntity,
    long ModelSourceId,
    double X,
    double Y,
    double Z,
    double M);
