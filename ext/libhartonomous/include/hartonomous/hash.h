/* libhartonomous — hash.h
 *
 * BLAKE3 — content-addressed identity for atoms (Tier 0).
 * Same content bytes → same 32-byte hash, in PG and in .NET.
 */

#ifndef HARTONOMOUS_HASH_H
#define HARTONOMOUS_HASH_H

#include <stddef.h>
#include <stdint.h>

#include "hartonomous/version.h"

#ifdef __cplusplus
extern "C" {
#endif

#define HARTONOMOUS_HASH_LEN 32

typedef struct hartonomous_hash32 {
    uint8_t bytes[HARTONOMOUS_HASH_LEN];
} hartonomous_hash32_t;

/* One-shot. */
HARTONOMOUS_API void hartonomous_blake3(
    const uint8_t* data,
    size_t len,
    uint8_t out[HARTONOMOUS_HASH_LEN]
);

/* Incremental. */
typedef struct hartonomous_blake3_state {
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

/* Batched one-shot — N independent BLAKE3 digests in a single FFI call,
 * OpenMP-parallel across inputs. Eliminates per-record P/Invoke cost.
 * Returns 0, -1 null, -2 n < 0. */
HARTONOMOUS_API int hartonomous_blake3_many(
    const uint8_t* const* inputs,
    const size_t* input_lens,
    int64_t n,
    uint8_t* output
);

#ifdef __cplusplus
}
#endif

#endif /* HARTONOMOUS_HASH_H */
