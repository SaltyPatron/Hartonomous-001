#include <string.h>

#include "hartonomous.h"
#include "blake3.h"

/* Compile-time check: our opaque buffer must be >= blake3_hasher. */
typedef char hartonomous_blake3_state_size_check[
    (sizeof(((hartonomous_blake3_state*)0)->_opaque) >= sizeof(blake3_hasher)) ? 1 : -1
];

void hartonomous_blake3(const uint8_t* data, size_t len, uint8_t out[HARTONOMOUS_HASH_LEN]) {
    blake3_hasher h;
    blake3_hasher_init(&h);
    blake3_hasher_update(&h, data, len);
    blake3_hasher_finalize(&h, out, HARTONOMOUS_HASH_LEN);
}

void hartonomous_blake3_init(hartonomous_blake3_state* state) {
    blake3_hasher_init((blake3_hasher*)state->_opaque);
}

void hartonomous_blake3_update(
    hartonomous_blake3_state* state,
    const uint8_t* data,
    size_t len
) {
    blake3_hasher_update((blake3_hasher*)state->_opaque, data, len);
}

void hartonomous_blake3_finalize(
    const hartonomous_blake3_state* state,
    uint8_t out[HARTONOMOUS_HASH_LEN]
) {
    blake3_hasher_finalize((const blake3_hasher*)state->_opaque, out, HARTONOMOUS_HASH_LEN);
}
