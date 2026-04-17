#include <stddef.h>
#include <stdint.h>

#include "hartonomous.h"
#include "blake3.h"

int hartonomous_blake3_merkle(
    const uint8_t* child_hashes, size_t child_count,
    uint8_t output[HARTONOMOUS_HASH_LEN]
) {
    if (output == NULL) return -1;
    blake3_hasher h;
    blake3_hasher_init(&h);
    if (child_count > 0) {
        if (child_hashes == NULL) return -1;
        blake3_hasher_update(&h, child_hashes, child_count * (size_t)HARTONOMOUS_HASH_LEN);
    }
    blake3_hasher_finalize(&h, output, HARTONOMOUS_HASH_LEN);
    return 0;
}
