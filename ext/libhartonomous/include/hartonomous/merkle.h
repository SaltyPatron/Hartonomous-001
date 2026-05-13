/* libhartonomous — merkle.h
 *
 * Tier > 0 identity = BLAKE3 over the ordered concatenation of 32-byte
 * child hashes. Matches Hartonomous.Core.Compute.Common.Blake3
 * .ComputeMerkleHash exactly, byte for byte.
 */

#ifndef HARTONOMOUS_MERKLE_H
#define HARTONOMOUS_MERKLE_H

#include <stddef.h>
#include <stdint.h>

#include "hartonomous/version.h"
#include "hartonomous/hash.h"

#ifdef __cplusplus
extern "C" {
#endif

HARTONOMOUS_API int hartonomous_blake3_merkle(
    const uint8_t* child_hashes,
    size_t child_count,
    uint8_t output[HARTONOMOUS_HASH_LEN]
);

#ifdef __cplusplus
}
#endif

#endif /* HARTONOMOUS_MERKLE_H */
