using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Decomposers.Safetensors.Passes.Internal;

/// <summary>
/// One above-noise-floor cell extracted from a Track 2 transformation tensor.
/// Identifies the two content participants the cell binds (typically two
/// <c>word_form</c> token hashes resolved through the model's tokenizer, or
/// one token and a <c>visual_concept</c> / <c>pixel_region</c> for cross-modal
/// models) plus the sign-bearing Glicko event payload.
///
/// <para>
/// <see cref="Score"/> ∈ {0.0, 1.0} encodes sign per Glicko-2 outcome
/// semantics: 1.0 for positive correlation (attention, attraction,
/// alignment), 0.0 for negative correlation (anti-attention, suppression,
/// antipodal). <see cref="Weight"/> = |raw value|; carries the magnitude of
/// the evidence. Combined, the substrate distinguishes silence (no cell) ≠
/// wide-sigma uncertainty ≠ tight-neutral consensus ≠ tight-signed consensus
/// (AP-31).
/// </para>
/// </summary>
public readonly record struct TensorSignalCell(
    Hash32 ParticipantA,
    Hash32 ParticipantB,
    double Score,
    double Weight);
