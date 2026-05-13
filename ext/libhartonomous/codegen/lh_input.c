/*
 * lh_input.c — POSIX mmap-backed reader. No Windows path needed: codegen
 * only ever runs on the developer's Linux box (the generated tables are
 * checked into the repo and consumed on every platform).
 */

#include "lh_input.h"

#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

int lh_input_open(lh_input *in, const char *path)
{
    if (!in || !path) {
        errno = EINVAL;
        return -1;
    }
    memset(in, 0, sizeof(*in));
    in->path = path;
    in->fd = open(path, O_RDONLY | O_CLOEXEC);
    if (in->fd < 0) return -1;

    struct stat st;
    if (fstat(in->fd, &st) != 0) {
        int e = errno;
        close(in->fd);
        in->fd = -1;
        errno = e;
        return -1;
    }
    if (st.st_size == 0) {
        in->bytes = (const uint8_t *)"";
        in->len = 0;
        return 0;
    }

    void *m = mmap(NULL, (size_t)st.st_size, PROT_READ, MAP_PRIVATE, in->fd, 0);
    if (m == MAP_FAILED) {
        int e = errno;
        close(in->fd);
        in->fd = -1;
        errno = e;
        return -1;
    }
    in->bytes = (const uint8_t *)m;
    in->len = (size_t)st.st_size;
    return 0;
}

void lh_input_close(lh_input *in)
{
    if (!in) return;
    if (in->bytes && in->len > 0) {
        munmap((void *)in->bytes, in->len);
    }
    if (in->fd >= 0) {
        close(in->fd);
    }
    memset(in, 0, sizeof(*in));
    in->fd = -1;
}
