/*
 * lh_emit.c — implementation of the rolling generated-source writer.
 */

#include "lh_emit.h"

#include <errno.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/types.h>

static int lh_mkdir_p(const char *path)
{
    char tmp[1024];
    size_t len = strlen(path);
    if (len == 0 || len >= sizeof(tmp)) return -1;
    memcpy(tmp, path, len + 1);
    if (tmp[len - 1] == '/') tmp[--len] = '\0';
    for (char *p = tmp + 1; *p; p++) {
        if (*p == '/') {
            *p = '\0';
            if (mkdir(tmp, 0755) != 0 && errno != EEXIST) return -1;
            *p = '/';
        }
    }
    if (mkdir(tmp, 0755) != 0 && errno != EEXIST) return -1;
    return 0;
}

static int lh_emit_open_part(lh_emit *e)
{
    if (e->part_index == 0) {
        snprintf(e->cur_path, sizeof(e->cur_path),
                 "%s/%s.%s", e->out_dir, e->base_name,
                 e->is_header ? "h" : "c");
    } else {
        snprintf(e->cur_path, sizeof(e->cur_path),
                 "%s/%s_part%d.c", e->out_dir, e->base_name,
                 e->part_index + 1);
    }
    e->fp = fopen(e->cur_path, "wb");
    if (!e->fp) return -1;
    e->bytes_in_part = 0;
    return 0;
}

static int lh_emit_open_common(lh_emit *e, const char *out_dir,
                               const char *base_name, int is_header)
{
    if (!e || !out_dir || !base_name) {
        errno = EINVAL;
        return -1;
    }
    memset(e, 0, sizeof(*e));
    snprintf(e->out_dir, sizeof(e->out_dir), "%s", out_dir);
    snprintf(e->base_name, sizeof(e->base_name), "%s", base_name);
    e->max_part_bytes = LH_EMIT_DEFAULT_MAX_BYTES;
    e->is_header = is_header;
    e->part_index = 0;

    if (lh_mkdir_p(out_dir) != 0) return -1;
    return lh_emit_open_part(e);
}

int lh_emit_open_header(lh_emit *e, const char *out_dir, const char *base_name)
{
    return lh_emit_open_common(e, out_dir, base_name, 1);
}

int lh_emit_open_source(lh_emit *e, const char *out_dir, const char *base_name)
{
    return lh_emit_open_common(e, out_dir, base_name, 0);
}

void lh_emit_set_max_part_bytes(lh_emit *e, size_t max_bytes)
{
    if (e && max_bytes > 0) e->max_part_bytes = max_bytes;
}

static int lh_emit_maybe_roll(lh_emit *e)
{
    if (e->is_header) return 0;
    if (e->bytes_in_part < e->max_part_bytes) return 0;
    if (fclose(e->fp) != 0) return -1;
    e->fp = NULL;
    e->part_index++;
    return lh_emit_open_part(e);
}

int lh_emit_write(lh_emit *e, const void *buf, size_t len)
{
    if (!e || !e->fp || (!buf && len > 0)) {
        errno = EINVAL;
        return -1;
    }
    if (fwrite(buf, 1, len, e->fp) != len) return -1;
    e->bytes_written += len;
    e->bytes_in_part += len;
    return lh_emit_maybe_roll(e);
}

int lh_emit_printf(lh_emit *e, const char *fmt, ...)
{
    if (!e || !e->fp) {
        errno = EINVAL;
        return -1;
    }
    va_list ap;
    va_start(ap, fmt);
    int n = vfprintf(e->fp, fmt, ap);
    va_end(ap);
    if (n < 0) return -1;
    e->bytes_written += (size_t)n;
    e->bytes_in_part += (size_t)n;
    return lh_emit_maybe_roll(e);
}

int lh_emit_close(lh_emit *e)
{
    if (!e) return -1;
    int rc = 0;
    if (e->fp) {
        if (fclose(e->fp) != 0) rc = -1;
        e->fp = NULL;
    }
    return rc;
}

int lh_emit_part_count(const lh_emit *e)
{
    if (!e) return 0;
    return e->part_index + 1;
}
