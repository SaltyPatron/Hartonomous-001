#include <string.h>

#include <omp.h>

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

int hartonomous_blake3_many(
    const uint8_t* const* inputs,
    const size_t* input_lens,
    int64_t n,
    uint8_t* output
) {
    if (inputs == NULL || input_lens == NULL || output == NULL) return -1;
    if (n < 0) return -2;
    if (n == 0) return 0;

    int64_t i;
    #pragma omp parallel for schedule(static) private(i)
    for (i = 0; i < n; ++i) {
        blake3_hasher h;
        blake3_hasher_init(&h);
        if (input_lens[i] > 0 && inputs[i] != NULL) {
            blake3_hasher_update(&h, inputs[i], input_lens[i]);
        }
        blake3_hasher_finalize(&h, output + (size_t)i * HARTONOMOUS_HASH_LEN, HARTONOMOUS_HASH_LEN);
    }
    return 0;
}
