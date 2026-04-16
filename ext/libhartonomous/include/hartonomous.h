#ifndef HARTONOMOUS_H
#define HARTONOMOUS_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) || defined(__CYGWIN__)
  #ifdef HARTONOMOUS_BUILD
    #define HARTONOMOUS_API __declspec(dllexport)
  #else
    #define HARTONOMOUS_API __declspec(dllimport)
  #endif
#else
  #define HARTONOMOUS_API __attribute__((visibility("default")))
#endif

#define HARTONOMOUS_VERSION_MAJOR 0
#define HARTONOMOUS_VERSION_MINOR 1
#define HARTONOMOUS_VERSION_PATCH 0

#define HARTONOMOUS_HASH_LEN 32

HARTONOMOUS_API const char* hartonomous_version(void);

/*
 * BLAKE3 one-shot hash. Computes a 32-byte digest over `len` bytes at `data`.
 * `out` must point to at least HARTONOMOUS_HASH_LEN (32) bytes.
 * Thread-safe. No allocations.
 */
HARTONOMOUS_API void hartonomous_blake3(
    const uint8_t* data,
    size_t len,
    uint8_t out[HARTONOMOUS_HASH_LEN]
);

/*
 * BLAKE3 incremental hashing. Opaque state, owned by the caller.
 * Usage:
 *   hartonomous_blake3_state st;
 *   hartonomous_blake3_init(&st);
 *   hartonomous_blake3_update(&st, chunk_a, a_len);
 *   hartonomous_blake3_update(&st, chunk_b, b_len);
 *   hartonomous_blake3_finalize(&st, out);
 */
typedef struct hartonomous_blake3_state {
    /* Opaque; sized to hold a blake3_hasher (1912 bytes on LP64). */
    uint8_t _opaque[2048];
} hartonomous_blake3_state;

HARTONOMOUS_API void hartonomous_blake3_init(hartonomous_blake3_state* state);
HARTONOMOUS_API void hartonomous_blake3_update(
    hartonomous_blake3_state* state,
    const uint8_t* data,
    size_t len
);
HARTONOMOUS_API void hartonomous_blake3_finalize(
    const hartonomous_blake3_state* state,
    uint8_t out[HARTONOMOUS_HASH_LEN]
);

/* ── S3 Geometry ─────────────────────────────────────────── */

/*
 * Geodesic distance between two unit 4-vectors on S^3.
 * Returns arccos(clamp(dot(p1, p2), -1, 1)) in [0, pi].
 * Inputs are assumed unit-length; no normalization is performed.
 */
HARTONOMOUS_API double hartonomous_s3_distance(
    const double p1[4],
    const double p2[4]
);

/*
 * Chordal centroid of `point_count` points on S^3.
 * Computes the vector mean of the input points and renormalizes onto S^3.
 * Writes the 4D unit vector to `out`.
 * Returns 0 on success, -1 on null argument or zero points,
 * -2 when the vector sum has negligible magnitude (antipodal cancellation).
 */
HARTONOMOUS_API int hartonomous_s3_centroid(
    const double* points,
    size_t point_count,
    double out[4]
);

/* ── Super-Fibonacci ─────────────────────────────────────── */

/*
 * Project a sample index onto S^3 using the Super-Fibonacci lattice
 * (Alexa, CVPR 2022). Produces a deterministic, quasi-uniform unit
 * 4-vector in [-1, 1]^4 on the unit 3-sphere.
 *
 * params[0] = index i in [0, N)
 * params[1] = total sample count N (must be > 0)
 * ndims     = number of valid entries in params; must be >= 2
 *
 * Writes 4D unit vector to `out`. Returns 0 on success, -1 on null
 * argument, -2 when ndims < 2 or N <= 0 or i not in [0, N).
 */
HARTONOMOUS_API int hartonomous_super_fibonacci(
    const double* params,
    size_t ndims,
    double out[4]
);

/* ── Hilbert Curve (4D) ──────────────────────────────────── */

/*
 * Compute the Hilbert curve index for a 4D point in [0, 1]^4.
 * `order` is the number of bits per dimension (1..16). Output fits in uint64_t
 * for order <= 16 (4 * 16 = 64 bits).
 * Returns the Hilbert index. Out-of-range inputs are clamped to [0, 1].
 */
HARTONOMOUS_API uint64_t hartonomous_hilbert_index(
    const double point[4],
    int order
);

/*
 * Inverse of hartonomous_hilbert_index. Writes the 4D point corresponding to
 * `index` at resolution `order` into `out` (each coordinate in [0, 1]).
 * Returns 0 on success, -1 on null argument, -2 on order out of range.
 */
HARTONOMOUS_API int hartonomous_hilbert_inverse(
    uint64_t index,
    int order,
    double out[4]
);

#ifdef __cplusplus
}
#endif

#endif /* HARTONOMOUS_H */
