using Hartonomous.Core.Ingestion;

namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// One tensor of a model, already promoted to a substrate <c>tensor</c> entity
/// by the orchestrator's pre-pass setup. Carries the parsed safetensors header
/// info, the role classification, the BLAKE3-of-(dtype,shape,bytes) content
/// hash, and the substrate <see cref="EntityHandle"/> (hash + type code) — no
/// surrogate id, since hash IS the identity.
/// </summary>
public sealed record TensorHandle(
    SafetensorsTensorInfo Info,
    TensorClassification Classification,
    byte[] ContentHash,
    EntityHandle Entity);
