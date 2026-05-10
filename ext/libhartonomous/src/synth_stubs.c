/* ── Phase A.0.4 synthesis primitive stubs (2026-05-09) ────────────────
 *
 * These four entrypoints back the recomposer's exact synthesis surface
 * declared in include/hartonomous.h. Native implementations are scheduled
 * for Phase B.1; until then, callers receive HARTONOMOUS_ERR_NOT_IMPLEMENTED
 * (-99) and the C# layer translates that to a ComputeException with the
 * entrypoint name.
 *
 * Spec references:
 *   docs/specs/recomposers/algorithms/embedding-synthesis-from-fireflies.md
 *   docs/specs/recomposers/algorithms/ffn-kv-inversion.md
 *   docs/specs/recomposers/algorithms/lottery-ticket-foundations.md
 *
 * Implementation guidance for B.1:
 *   - linear_system_solve_f64: build on existing svd.c (MKL dgesdd) — compute
 *     SVD of A, threshold singular values at tolerance·σ_max, form A⁺ then
 *     X = A⁺·B. Rank = count of singular values above threshold.
 *   - sparse_ffn_invert_f64: KV-memory construction per Approach 1 of the
 *     ffn-kv-inversion spec. Composes input/output token directions into the
 *     constraint set; then SVD-compress to target intermediate_dim.
 *   - inverse_eigenmap_f64: reverse-project XYZM centroids using stored
 *     eigenvectors. The fireflies-are-the-embedding reframe means the
 *     consensus centroid IS the target hidden-space position; this primitive
 *     returns the model-specific hidden_dim vector that the consensus
 *     centroid implies under the stored eigenvector projection.
 *   - honest_abstention_f64: per-cell coverage threshold; cells below get
 *     zeroed; per-row coverage stats written; aggregate coverage returned.
 */

#include "hartonomous.h"
#include <stdint.h>

#ifndef HARTONOMOUS_ERR_NOT_IMPLEMENTED
#define HARTONOMOUS_ERR_NOT_IMPLEMENTED -99
#endif

HARTONOMOUS_API int hartonomous_linear_system_solve_f64(
    int64_t m, int64_t n, int64_t p,
    const double* a,
    const double* b,
    double* x,
    double tolerance,
    int64_t* rank_out)
{
    (void)m; (void)n; (void)p; (void)a; (void)b; (void)x; (void)tolerance; (void)rank_out;
    return HARTONOMOUS_ERR_NOT_IMPLEMENTED;
}

HARTONOMOUS_API int hartonomous_sparse_ffn_invert_f64(
    int64_t vocab_size, int64_t hidden_dim, int64_t intermediate_dim,
    const double* token_embeddings,
    int64_t nnz,
    const int64_t* input_token_idx,
    const int64_t* output_token_idx,
    const double* strength,
    double coverage_min,
    double* w_gate_out,
    double* w_up_out,
    double* w_down_out,
    double* coverage_out)
{
    (void)vocab_size; (void)hidden_dim; (void)intermediate_dim;
    (void)token_embeddings;
    (void)nnz; (void)input_token_idx; (void)output_token_idx; (void)strength;
    (void)coverage_min;
    (void)w_gate_out; (void)w_up_out; (void)w_down_out; (void)coverage_out;
    return HARTONOMOUS_ERR_NOT_IMPLEMENTED;
}

HARTONOMOUS_API int hartonomous_inverse_eigenmap_f64(
    int64_t vocab_size, int64_t hidden_dim,
    const double* eigenvectors,
    const double* embeddings,
    int64_t centroid_count,
    const double* centroids_xyzm,
    double* hidden_out)
{
    (void)vocab_size; (void)hidden_dim;
    (void)eigenvectors; (void)embeddings;
    (void)centroid_count; (void)centroids_xyzm; (void)hidden_out;
    return HARTONOMOUS_ERR_NOT_IMPLEMENTED;
}

HARTONOMOUS_API int hartonomous_honest_abstention_f64(
    int64_t rows, int64_t cols,
    double* weights,
    const double* coverage,
    double cell_threshold,
    double* row_coverage_out,
    double* aggregate_coverage_out)
{
    (void)rows; (void)cols;
    (void)weights; (void)coverage; (void)cell_threshold;
    (void)row_coverage_out; (void)aggregate_coverage_out;
    return HARTONOMOUS_ERR_NOT_IMPLEMENTED;
}
