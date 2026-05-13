using System.Collections.Generic;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Decomposers.Safetensors.Passes.Internal;

/// <summary>
/// Per-extraction parameters. The tensor shape, the tensor's adaptive noise
/// floor (computed by the orchestrator from the tensor's own |x|
/// distribution, not a global constant), and the participant content hashes
/// the extractor will bind cells between.
///
/// <para>
/// <see cref="Shape"/> is row-major; for a <c>[V, D]</c> tensor (vocabulary ×
/// hidden-dim) Shape[0] = V and Shape[1] = D. The extractor interprets shape
/// per-tuple — attention QK is <c>[H, D_head, D_model]</c>, FFN up is
/// <c>[D_intermediate, D_model]</c>, embedding is <c>[V, D_model]</c>, etc.
/// </para>
///
/// <para>
/// <see cref="Participants"/> indexes match the tensor's row/column space:
/// for attention QK, indices 0..V-1 map to token hashes via the tokenizer;
/// for cross-modal attention, indices may map to a mix of token hashes and
/// visual-concept hashes per the architecture.
/// </para>
/// </summary>
public readonly record struct TensorSignalExtractionParameters(
    IReadOnlyList<int> Shape,
    double NoiseFloor,
    IReadOnlyList<Hash32> Participants);
