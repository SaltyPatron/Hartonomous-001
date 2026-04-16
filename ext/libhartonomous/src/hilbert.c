#include "hartonomous.h"

#include <stddef.h>
#include <stdint.h>

/*
 * N-dimensional Hilbert curve, compacted to N=4.
 * AxesToTranspose / TransposeToAxes algorithm (Skilling, 2004).
 *
 * For `order` bits per dimension, we treat `point[k]` in [0,1] as an integer
 * coordinate in [0, 2^order - 1]. The resulting Hilbert index fits in
 * 4 * order bits.
 */

#define HTNS_HILBERT_DIM 4
#define HTNS_HILBERT_MAX_ORDER 16

static uint64_t clamp_coord(double c, uint64_t max_val)
{
    if (c <= 0.0) return 0;
    if (c >= 1.0) return max_val;
    return (uint64_t)(c * (double)max_val + 0.5);
}

/* Skilling's AxesToTranspose: in-place conversion from N coordinate ints to
 * the "transposed" Hilbert form. After this step the bit-layout is:
 *   bit k of coord d, for d=0..N-1, k=order-1..0
 * which we read out as the index. */
static void axes_to_transpose(uint64_t X[HTNS_HILBERT_DIM], int order)
{
    uint64_t M = (uint64_t)1 << (order - 1);

    /* Inverse undo */
    for (uint64_t Q = M; Q > 1; Q >>= 1) {
        uint64_t P = Q - 1;
        for (int i = 0; i < HTNS_HILBERT_DIM; ++i) {
            if (X[i] & Q) {
                X[0] ^= P;
            } else {
                uint64_t T = (X[0] ^ X[i]) & P;
                X[0] ^= T;
                X[i] ^= T;
            }
        }
    }

    /* Gray encode */
    for (int i = 1; i < HTNS_HILBERT_DIM; ++i) {
        X[i] ^= X[i - 1];
    }

    uint64_t T = 0;
    for (uint64_t Q = M; Q > 1; Q >>= 1) {
        if (X[HTNS_HILBERT_DIM - 1] & Q) {
            T ^= Q - 1;
        }
    }
    for (int i = 0; i < HTNS_HILBERT_DIM; ++i) {
        X[i] ^= T;
    }
}

static void transpose_to_axes(uint64_t X[HTNS_HILBERT_DIM], int order)
{
    uint64_t N = (uint64_t)2 << (order - 1);

    /* Gray decode by H ^ (H/2) */
    uint64_t T = X[HTNS_HILBERT_DIM - 1] >> 1;
    for (int i = HTNS_HILBERT_DIM - 1; i > 0; --i) {
        X[i] ^= X[i - 1];
    }
    X[0] ^= T;

    /* Undo excess work */
    for (uint64_t Q = 2; Q != N; Q <<= 1) {
        uint64_t P = Q - 1;
        for (int i = HTNS_HILBERT_DIM - 1; i >= 0; --i) {
            if (X[i] & Q) {
                X[0] ^= P;
            } else {
                uint64_t Tt = (X[0] ^ X[i]) & P;
                X[0] ^= Tt;
                X[i] ^= Tt;
            }
        }
    }
}

uint64_t hartonomous_hilbert_index(const double point[4], int order)
{
    if (point == NULL || order < 1 || order > HTNS_HILBERT_MAX_ORDER) return 0;

    uint64_t max_val = ((uint64_t)1 << order) - 1;
    uint64_t X[HTNS_HILBERT_DIM];
    for (int i = 0; i < HTNS_HILBERT_DIM; ++i) {
        X[i] = clamp_coord(point[i], max_val);
    }

    axes_to_transpose(X, order);

    /* Interleave transposed form: bit k of coord d → index bit (k * N + d). */
    uint64_t index = 0;
    for (int k = order - 1; k >= 0; --k) {
        for (int d = 0; d < HTNS_HILBERT_DIM; ++d) {
            index = (index << 1) | ((X[d] >> k) & 1);
        }
    }
    return index;
}

int hartonomous_hilbert_inverse(uint64_t index, int order, double out[4])
{
    if (out == NULL) return -1;
    if (order < 1 || order > HTNS_HILBERT_MAX_ORDER) return -2;

    uint64_t X[HTNS_HILBERT_DIM] = {0, 0, 0, 0};
    int total_bits = order * HTNS_HILBERT_DIM;
    for (int bit = total_bits - 1; bit >= 0; --bit) {
        int k = bit / HTNS_HILBERT_DIM;
        int d = (HTNS_HILBERT_DIM - 1) - (bit % HTNS_HILBERT_DIM);
        uint64_t v = (index >> bit) & 1;
        X[d] |= v << k;
    }

    transpose_to_axes(X, order);

    uint64_t max_val = ((uint64_t)1 << order) - 1;
    double denom = (double)max_val;
    if (denom == 0.0) denom = 1.0;
    for (int i = 0; i < HTNS_HILBERT_DIM; ++i) {
        out[i] = (double)X[i] / denom;
    }
    return 0;
}
