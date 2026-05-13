/* libhartonomous — synthesis.h
 *
 * Phase A.0.4 synthesis primitives — recomposer's exact synthesis
 * surface. Native implementation lands in Phase B.1; current returns
 * are placeholder per the original umbrella header contract.
 *
 * Spec docs:
 *   docs/specs/recomposers/algorithms/embedding-synthesis-from-fireflies.md
 *   docs/specs/recomposers/algorithms/ffn-kv-inversion.md
 *   docs/specs/recomposers/algorithms/lottery-ticket-foundations.md
 */

#ifndef HARTONOMOUS_SYNTHESIS_H
#define HARTONOMOUS_SYNTHESIS_H

#include <stddef.h>
#include <stdint.h>

#include "hartonomous/version.h"

#ifdef __cplusplus
extern "C" {
#endif

/* (W_gate, W_up, W_down) from sparse token-pair attestations. */
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
    double* coverage_out);

/* Reverse-project firefly POINTZM centroids back to hidden_dim. */
HARTONOMOUS_API int hartonomous_inverse_eigenmap_f64(
    int64_t vocab_size, int64_t hidden_dim,
    const double* eigenvectors,
    const double* embeddings,
    int64_t centroid_count,
    const double* centroids_xyzm,
    double* hidden_out);

/* Mask cells below coverage threshold to exact zero (in place). */
HARTONOMOUS_API int hartonomous_honest_abstention_f64(
    int64_t rows, int64_t cols,
    double* weights,
    const double* coverage,
    double cell_threshold,
    double* row_coverage_out,
    double* aggregate_coverage_out);

#ifdef __cplusplus
}
#endif

#endif /* HARTONOMOUS_SYNTHESIS_H */
