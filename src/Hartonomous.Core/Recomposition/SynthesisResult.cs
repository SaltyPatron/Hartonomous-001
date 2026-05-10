namespace Hartonomous.Core.Recomposition;

/// <summary>
/// Output of one <see cref="ILayerTypeSynthesizer"/> call: the packed wire
/// bytes for the target tensor + per-tensor coverage statistics for the
/// recomposer's safetensors header.
/// </summary>
/// <param name="Bytes">Packed tensor data in the target wire dtype, ready
/// to be written to the safetensors output. Length must match
/// <c>shape.product * BytesPerElement(dtype)</c>; under-attested cells are
/// already masked to exact zero per honest-abstention discipline.</param>
/// <param name="AggregateCoverage">Mean coverage across all cells in [0, 1].
/// 0 = nothing recovered (entire tensor is honest-abstention zero); 1 = every
/// cell received attestation density at or above the threshold.</param>
/// <param name="AttestationCount">Number of substrate attestation rows
/// consulted for this tensor. Reported in safetensors header for audit.</param>
/// <param name="ContributingSourceCount">Number of distinct model_source_ids
/// that contributed at least one attestation. 1 in Mode 1 single-source
/// round-trip; N in Mode 2 cross-model consensus.</param>
public sealed record SynthesisResult(
    byte[] Bytes,
    double AggregateCoverage,
    long AttestationCount,
    int ContributingSourceCount);
