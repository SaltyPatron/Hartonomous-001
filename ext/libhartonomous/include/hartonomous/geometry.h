/* libhartonomous — geometry.h
 *
 * 4D primitives. The substrate is genuinely 4D — PostGIS POINTZM treats
 * M as out-of-band; these functions operate on raw double[4]. Coordinate
 * semantics (S^3 unit-quaternion vs Euclidean 4-space) live on the row's
 * physicality_type, not the function.
 */

#ifndef HARTONOMOUS_GEOMETRY_H
#define HARTONOMOUS_GEOMETRY_H

#include <stddef.h>
#include <stdint.h>

#include "hartonomous/version.h"

#ifdef __cplusplus
extern "C" {
#endif

/* ── S^3 ──────────────────────────────────────────────────── */

HARTONOMOUS_API double hartonomous_s3_distance(
    const double p1[4], const double p2[4]);

HARTONOMOUS_API int hartonomous_s3_centroid(
    const double* points, size_t point_count, double out[4]);

/* Karcher (Fréchet) mean on S^3. max_iter <= 0 → 64; tol <= 0 → 1e-12. */
HARTONOMOUS_API int hartonomous_karcher_mean_s3(
    const double* points, size_t point_count,
    int max_iter, double tol, double out[4]);

/* ── Super-Fibonacci ──────────────────────────────────────── */

HARTONOMOUS_API int hartonomous_super_fibonacci(
    const double* params, size_t ndims, double out[4]);

HARTONOMOUS_API int hartonomous_super_fibonacci_many(
    const double* indices, int64_t n, double total, double* out);

/* ── Hilbert (4D) ─────────────────────────────────────────── */

HARTONOMOUS_API uint64_t hartonomous_hilbert_index(
    const double point[4], int order);

HARTONOMOUS_API int hartonomous_hilbert_inverse(
    uint64_t index, int order, double out[4]);

/* ── point4d primitives ───────────────────────────────────── */

HARTONOMOUS_API double hartonomous_distance_4d(
    const double a[4], const double b[4]);

HARTONOMOUS_API int hartonomous_distance_4d_pairs(
    const double* a, const double* b, int64_t n, double* out);

HARTONOMOUS_API double hartonomous_dot_4d(
    const double a[4], const double b[4]);

HARTONOMOUS_API double hartonomous_norm_4d(const double x[4]);

HARTONOMOUS_API int hartonomous_normalize_4d(
    const double x[4], double out[4]);

HARTONOMOUS_API int hartonomous_slerp(
    const double a[4], const double b[4], double t, double out[4]);

HARTONOMOUS_API int hartonomous_antipode(
    const double p[4], double out[4]);

HARTONOMOUS_API int hartonomous_centroid_4d(
    const double* points, size_t point_count, double out[4]);

HARTONOMOUS_API int hartonomous_centroid_4d_grouped(
    const double* points, const int64_t* group_ids,
    int64_t n, int64_t group_count, double* centroids);

/* ── box4d (axis-aligned bounding box, 8 doubles min[0..3] max[0..3]) ── */

HARTONOMOUS_API void hartonomous_bbox_init_point(
    const double p[4], double box[8]);

HARTONOMOUS_API void hartonomous_bbox_expand_point(
    double box[8], const double p[4]);

HARTONOMOUS_API void hartonomous_bbox_union(
    const double a[8], const double b[8], double out[8]);

HARTONOMOUS_API int hartonomous_bbox_overlaps(
    const double a[8], const double b[8]);

HARTONOMOUS_API int hartonomous_bbox_contains_point(
    const double box[8], const double p[4]);

HARTONOMOUS_API int hartonomous_bbox_contains_box(
    const double outer[8], const double inner[8]);

HARTONOMOUS_API int hartonomous_bbox_equals(
    const double a[8], const double b[8]);

HARTONOMOUS_API double hartonomous_bbox_volume(const double box[8]);

HARTONOMOUS_API double hartonomous_bbox_min_distance_4d(
    const double box[8], const double p[4]);

/* ── linestring4d (packed 4-double-per-vertex) ────────────── */

HARTONOMOUS_API double hartonomous_frechet_4d(
    const double* a, size_t na,
    const double* b, size_t nb,
    double* workspace);

HARTONOMOUS_API int hartonomous_frechet_4d_pairs(
    const double* const* a_polylines, const size_t* na,
    const double* const* b_polylines, const size_t* nb,
    int64_t n, double* out_distances);

HARTONOMOUS_API double hartonomous_hausdorff_4d(
    const double* a, size_t na,
    const double* b, size_t nb);

#ifdef __cplusplus
}
#endif

#endif /* HARTONOMOUS_GEOMETRY_H */
