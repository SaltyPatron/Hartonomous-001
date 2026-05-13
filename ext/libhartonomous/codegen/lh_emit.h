/*
 * lh_emit.h — generated-file writer. Buffered C-source emitter that
 * automatically rolls over once the active output reaches a configurable
 * byte ceiling (default 25 MiB). Used by every gen_* tool so generated
 * tables stay below the per-translation-unit size that keeps icx and gcc
 * compile times sane.
 */

#ifndef LH_EMIT_H
#define LH_EMIT_H

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>

#define LH_EMIT_DEFAULT_MAX_BYTES (25u * 1024u * 1024u)

typedef struct lh_emit {
    char    out_dir[1024];        /* directory for emitted files */
    char    base_name[256];       /* e.g. "lh_ucd_props" */
    FILE   *fp;                   /* current open file */
    char    cur_path[2048];       /* full path of open file */
    size_t  bytes_written;        /* total bytes written across rollover parts */
    size_t  bytes_in_part;        /* bytes in current part */
    size_t  max_part_bytes;       /* roll over once exceeded */
    int     part_index;           /* 0 = base file, 1 = _part2, 2 = _part3, … */
    int     is_header;            /* 1 = .h file (no rollover); 0 = .c file */
} lh_emit;

/*
 * Open a header file at "<out_dir>/<base_name>.h". Header files NEVER roll
 * over (must remain a single TU for #include).
 */
int lh_emit_open_header(lh_emit *e, const char *out_dir, const char *base_name);

/*
 * Open the first part of a .c file at "<out_dir>/<base_name>.c". Successive
 * parts after rollover are "<base_name>_part2.c", "<base_name>_part3.c", …
 */
int lh_emit_open_source(lh_emit *e, const char *out_dir, const char *base_name);

/*
 * Optional: override the rollover ceiling before writing any data.
 * Default is LH_EMIT_DEFAULT_MAX_BYTES.
 */
void lh_emit_set_max_part_bytes(lh_emit *e, size_t max_bytes);

/* printf-style write. Triggers rollover after the current write completes. */
int lh_emit_printf(lh_emit *e, const char *fmt, ...)
    __attribute__((format(printf, 2, 3)));

/* Raw byte write. Triggers rollover after the current write completes. */
int lh_emit_write(lh_emit *e, const void *buf, size_t len);

/* Closes the active file and any rollover parts. Returns -1 on flush error. */
int lh_emit_close(lh_emit *e);

/* Returns the number of part files written (1 = single file, no rollover). */
int lh_emit_part_count(const lh_emit *e);

#endif /* LH_EMIT_H */
