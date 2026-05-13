/*
 * lh_input.h — mmap helper for codegen tools.
 *
 * Read-only mapping of an input file. Unmaps on `lh_input_close`.
 */

#ifndef LH_INPUT_H
#define LH_INPUT_H

#include <stddef.h>
#include <stdint.h>

typedef struct lh_input {
    const char *path;        /* original path, owned by caller */
    const uint8_t *bytes;    /* mmap base, NUL-terminated guard not required */
    size_t len;              /* file length in bytes */
    int fd;                  /* internal */
} lh_input;

/* Returns 0 on success, -1 on failure with errno set. */
int  lh_input_open(lh_input *in, const char *path);
void lh_input_close(lh_input *in);

#endif /* LH_INPUT_H */
