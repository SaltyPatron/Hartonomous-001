using System;
using System.Collections.Generic;
using Hartonomous.Core.Compute;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Everything an <see cref="IModelAnalysisPass"/> needs about the model under
/// analysis. Origin metadata (<see cref="Source"/>) is on the context, never
/// inside any entity content hash.
///
/// Per docs/specs/decomposers/analysis-passes.md § "ModelPassContext".
/// </summary>
public sealed record ModelPassContext(
    ModelSourceHandle Source,
    ModelArchitectureHandle Architecture,
    IReadOnlyList<TensorHandle> Tensors,
    IComputeFacade Compute,
    IReadOnlyDictionary<string, int> TensorRoleMap,
    string CheckpointKey,
    string ProvenanceCode)
{
    /// <summary>
    /// Per-pass deterministic seed: BLAKE3(<see cref="CheckpointKey"/> || passId).
    /// Same model + same pass → same seed across runs and machines, satisfying
    /// Law #6 on every primitive that accepts a seed.
    /// </summary>
    public ulong DeriveSeed(string passId)
    {
        ICanonicalSignatureBuilder b = new CanonicalSignatureBuilder(Compute.Common, "seed")
            .WriteUtf8(CheckpointKey)
            .WriteUtf8(passId);
        byte[] hash = b.Finalize();
        return BitConverter.ToUInt64(hash, 0);
    }

    public ICanonicalSignatureBuilder NewSignature(string kindTag4)
        => new CanonicalSignatureBuilder(Compute.Common, kindTag4);
}
