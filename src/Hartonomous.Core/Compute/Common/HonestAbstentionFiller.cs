using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Common;

/// <summary>
/// Apply honest-abstention masking to a synthesizer's intermediate weight
/// matrix: per-cell coverage below threshold → exact zero. Returns per-row
/// (and aggregate) coverage statistics so the synthesizer can emit them as
/// header metadata on the recomposed safetensors.
///
/// Per spec §VIII (sparse honest recording) and AP-11 (no approximation).
/// Honest abstention is not noise injection; it is the substrate's stated
/// position that an under-attested cell carries no learned signal and is
/// therefore exactly zero. Lottery-Ticket-Hypothesis baseline: 60-90% of
/// cells in a typical transformer carry no load-bearing signal post-prune,
/// so high zero density in the recomposed model is expected and correct.
///
/// Coverage = (sum of attestation weights touching this cell) / (target mu
/// for full-coverage). The threshold is per-tensor-type (configurable on
/// the recomposer's <c>RecompositionOptions</c>); typical defaults around
/// 0.1 for attention/FFN, 0.05 for embedding, 0.0 for layer-norm scale
/// (always-cover). Per-row coverage feeds into the synthesizer's reported
/// per-tensor coverage statistics in safetensors header.
///
/// Phase A.0.4 (2026-05-09): native implementation deferred to Phase B.1.
/// </summary>
public static class HonestAbstentionFiller
{
    /// <summary>
    /// Mask cells whose per-cell coverage is below the threshold to exact
    /// zero. In-place on <paramref name="weights"/>; coverage stats written
    /// to <paramref name="rowCoverageOut"/> and the aggregate returned.
    /// </summary>
    /// <param name="rows">Rows of the weight matrix and the coverage matrix.</param>
    /// <param name="cols">Cols of the weight matrix and the coverage matrix.</param>
    /// <param name="weights">In/out weights, row-major [rows × cols].</param>
    /// <param name="coverage">In coverage per cell, row-major [rows × cols].
    /// Each value is in [0, 1].</param>
    /// <param name="cellThreshold">Per-cell threshold; cells with coverage
    /// below this are zeroed.</param>
    /// <param name="rowCoverageOut">Output mean coverage per row (after
    /// masking), length &gt;= rows.</param>
    /// <returns>Aggregate coverage = mean over all cells (post-masking,
    /// counting zeroed cells as zero contribution). For header metadata.</returns>
    public static double ApplyF64(
        long rows, long cols,
        Span<double> weights,
        ReadOnlySpan<double> coverage,
        double cellThreshold,
        Span<double> rowCoverageOut)
    {
        if (rows <= 0 || cols <= 0)
        {
            throw new ComputeArgumentException(
                $"honest_abstention_f64: invalid shape rows={rows} cols={cols}");
        }
        long len = checked(rows * cols);
        if (weights.Length < len)
        {
            throw new ComputeArgumentException(
                $"honest_abstention_f64: weights buffer too small ({weights.Length} < {len})");
        }
        if (coverage.Length < len)
        {
            throw new ComputeArgumentException(
                $"honest_abstention_f64: coverage buffer too small ({coverage.Length} < {len})");
        }
        if (rowCoverageOut.Length < rows)
        {
            throw new ComputeArgumentException(
                $"honest_abstention_f64: rowCoverage output too small ({rowCoverageOut.Length} < {rows})");
        }
        if (!(cellThreshold >= 0.0 && cellThreshold <= 1.0))
        {
            throw new ComputeArgumentException(
                $"honest_abstention_f64: cellThreshold must be in [0,1], got {cellThreshold}");
        }

        double aggregateCoverage = 0;
        int rc = NativeCompute.HonestAbstentionF64(
            rows, cols, weights, coverage, cellThreshold,
            rowCoverageOut, ref aggregateCoverage);
        NativeError.ThrowIfError(rc, "honest_abstention_f64");
        return aggregateCoverage;
    }
}
