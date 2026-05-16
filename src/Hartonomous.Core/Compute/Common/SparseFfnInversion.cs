using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Common;

/// <summary>
/// Joint exact linear-system solver for the three FFN matrices
/// <c>(W_up, W_gate, W_down)</c> from token-pair attestation constraints.
/// Used by <c>FfnLayerSynthesizer</c>.
///
/// Per docs/specs/recomposers/algorithms/ffn-kv-inversion.md Approach 1
/// (direct KV-memory construction + thin SVD compression — preferred over
/// Approach 2 per-dim Levenberg-Marquardt). Each attestation
/// <c>(input_token, output_token, strength, attestation_type=positive_evidence)</c>
/// becomes a constraint on the composed FFN path
/// <c>output_token^T · W_down · σ(W_gate · input_token) ⊙ (W_up · input_token) = strength</c>.
/// The exact closed-form construction:
///
///   1. Build the K-V memory matrix M ∈ R^(intermediate_dim × hidden_dim·2)
///      whose rows are (W_gate row, W_up row) concatenated.
///   2. For each attestation, compute the implied K (input direction) and
///      V (output direction) row contribution to M.
///   3. Compose M as a sparse weighted sum of (input_dir ⊗ output_dir)
///      outer products (consensus mu as the weight).
///   4. SVD-compress M to the target intermediate_dim via
///      <see cref="LinearSystemSolver"/>; the top singular vectors form
///      W_gate / W_up (split halves of M's rows). W_down is the matched
///      output projection.
///
/// Honest abstention: rows whose attestation density is below
/// <paramref name="coverageMin"/> stay at exact zero. Per-row coverage is
/// returned via <paramref name="coverageOut"/> for synthesizer-side reporting.
///
/// Phase A.0.4 (2026-05-09): native implementation deferred to Phase B.1.
/// </summary>
public static class SparseFfnInversion
{
    /// <summary>
    /// Construct (W_up, W_gate, W_down) from attestation triples.
    ///
    /// Attestation triples encoded as parallel CSR-like arrays:
    ///   inputTokenIdx[i], outputTokenIdx[i], strength[i] for i in [0, nnz).
    ///
    /// Token directions are looked up from <paramref name="tokenEmbeddings"/>
    /// (row-major [vocabSize × hiddenDim]). Each token row is the token's
    /// position in the model's hidden space (typically the consensus
    /// firefly-derived embedding via <see cref="InverseLaplacianEigenmap"/>).
    /// </summary>
    public static void ConstructF64(
        long vocabSize,
        long hiddenDim,
        long intermediateDim,
        ReadOnlySpan<double> tokenEmbeddings,
        long nnz,
        ReadOnlySpan<long> inputTokenIdx,
        ReadOnlySpan<long> outputTokenIdx,
        ReadOnlySpan<double> strength,
        double coverageMin,
        Span<double> wGateOut,
        Span<double> wUpOut,
        Span<double> wDownOut,
        Span<double> coverageOut)
    {
        if (vocabSize <= 0 || hiddenDim <= 0 || intermediateDim <= 0 || nnz < 0)
        {
            throw new ComputeArgumentException(
                $"sparse_ffn_invert_f64: invalid shape vocab={vocabSize} hidden={hiddenDim} intermediate={intermediateDim} nnz={nnz}");
        }
        long embedLen = checked(vocabSize * hiddenDim);
        long ffnLen = checked(intermediateDim * hiddenDim);
        if (tokenEmbeddings.Length < embedLen)
        {
            throw new ComputeArgumentException(
                $"sparse_ffn_invert_f64: tokenEmbeddings buffer too small ({tokenEmbeddings.Length} < {embedLen})");
        }
        if (inputTokenIdx.Length < nnz || outputTokenIdx.Length < nnz || strength.Length < nnz)
        {
            throw new ComputeArgumentException(
                "sparse_ffn_invert_f64: attestation arrays must have length >= nnz");
        }
        if (wGateOut.Length < ffnLen || wUpOut.Length < ffnLen || wDownOut.Length < ffnLen)
        {
            throw new ComputeArgumentException(
                $"sparse_ffn_invert_f64: weight output buffers too small (need {ffnLen} each)");
        }
        if (coverageOut.Length < intermediateDim)
        {
            throw new ComputeArgumentException(
                $"sparse_ffn_invert_f64: coverage buffer too small ({coverageOut.Length} < {intermediateDim})");
        }

        int rc = NativeCompute.SparseFfnInvertF64(
            vocabSize, hiddenDim, intermediateDim,
            tokenEmbeddings,
            nnz, inputTokenIdx, outputTokenIdx, strength,
            coverageMin,
            wGateOut, wUpOut, wDownOut, coverageOut);
        NativeError.ThrowIfError(rc, "sparse_ffn_invert_f64");
    }
}
