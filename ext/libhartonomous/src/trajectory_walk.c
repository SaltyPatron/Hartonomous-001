/*
 * libhartonomous — PostGIS WKB trajectory walker.
 *
 * Parses LINESTRINGZM / MULTILINESTRINGZM PostGIS EWKB byte streams and
 * fires a per-vertex callback with (sub_idx, vertex_idx, x, y, z, m).
 * No SPI, no PG dependency — pure C kernel callable from PG SRFs
 * (via pg_trajectory_walk.c wrappers) and from C# managed code (via
 * P/Invoke).
 *
 * The vertex coordinates are returned raw — the caller chooses whether
 * to interpret them as real metric coordinates (atom centroids; edge
 * trajectories) or as mantissa-packed identity bits (composition
 * ingestion trajectories where X = bb_pack_hash_lo(child.hash_bits_0_51),
 * Y = bb_pack_ordinal_rle, Z = bb_pack_hash_hi(child.hash_bits_52_103),
 * M = bb_pack_metadata). The mantissa unpack is `(int64_t)(d - 2^52)`
 * via simple subtraction — see substrate.bb_unpack_* SQL helpers and
 * Hartonomous.Core.Compute.Common.MantissaPacking in C#.
 *
 * Supported WKB types (PostGIS EWKB):
 *   LINESTRINGZM       (low-12 = 2, Z + M flags) — single sub-linestring
 *   MULTILINESTRINGZM  (low-12 = 5, Z + M flags) — N sub-linestrings
 *
 * Endianness: PostGIS on x86_64 emits little-endian. Both endiannesses
 * are supported in this parser; bytes are decoded per the per-stream
 * endianness byte (PostGIS allows mixed endianness in nested geometries
 * for paranoia, but in practice every nested geometry uses the same
 * endianness as the outer).
 */

#include "hartonomous.h"

#include <stdint.h>
#include <string.h>

#define WKB_FLAG_Z    0x80000000u
#define WKB_FLAG_M    0x40000000u
#define WKB_FLAG_SRID 0x20000000u

#define WKB_TYPE_POINT           1
#define WKB_TYPE_LINESTRING      2
#define WKB_TYPE_MULTILINESTRING 5

#define WKB_ENDIAN_BIG    0
#define WKB_ENDIAN_LITTLE 1

/*
 * Byte-stream reader carrying a cursor + endianness flag. Bounds-checks
 * every read against `len`; on overrun, sets cursor past end and returns
 * a sentinel that the caller treats as parse failure.
 */
typedef struct {
    const uint8_t* p;
    size_t         len;
    size_t         off;
    int            endian; /* WKB_ENDIAN_* */
    int            error;
} lh_wkb_reader;

static inline int lh_wkb_have(const lh_wkb_reader* r, size_t n)
{
    return !r->error && (r->off + n) <= r->len;
}

static inline uint8_t lh_wkb_read_u8(lh_wkb_reader* r)
{
    if (!lh_wkb_have(r, 1)) { r->error = 1; return 0; }
    return r->p[r->off++];
}

static inline uint32_t lh_wkb_read_u32(lh_wkb_reader* r)
{
    if (!lh_wkb_have(r, 4)) { r->error = 1; return 0; }
    uint32_t v;
    if (r->endian == WKB_ENDIAN_LITTLE) {
        v = (uint32_t)r->p[r->off]
          | ((uint32_t)r->p[r->off + 1] << 8)
          | ((uint32_t)r->p[r->off + 2] << 16)
          | ((uint32_t)r->p[r->off + 3] << 24);
    } else {
        v = ((uint32_t)r->p[r->off] << 24)
          | ((uint32_t)r->p[r->off + 1] << 16)
          | ((uint32_t)r->p[r->off + 2] << 8)
          | (uint32_t)r->p[r->off + 3];
    }
    r->off += 4;
    return v;
}

static inline double lh_wkb_read_f64(lh_wkb_reader* r)
{
    if (!lh_wkb_have(r, 8)) { r->error = 1; return 0.0; }
    uint8_t buf[8];
    if (r->endian == WKB_ENDIAN_LITTLE) {
        memcpy(buf, r->p + r->off, 8);
    } else {
        for (int i = 0; i < 8; i++) {
            buf[i] = r->p[r->off + 7 - i];
        }
    }
    r->off += 8;
    double v;
    memcpy(&v, buf, 8);
    return v;
}

/*
 * Parse one LINESTRINGZM body (the header — endianness + type — has
 * already been consumed by the caller). For each vertex, fire the
 * callback. Returns 0 on success, callback's non-zero return on
 * abort, or -1 on parse error.
 */
static int lh_parse_linestring_zm(
    lh_wkb_reader* r,
    int sub_idx,
    lh_traj_vertex_cb cb,
    void* ctx)
{
    uint32_t n_points = lh_wkb_read_u32(r);
    if (r->error) return -1;

    for (uint32_t i = 0; i < n_points; i++) {
        double x = lh_wkb_read_f64(r);
        double y = lh_wkb_read_f64(r);
        double z = lh_wkb_read_f64(r);
        double m = lh_wkb_read_f64(r);
        if (r->error) return -1;

        int rc = cb(ctx, sub_idx, (int)i, x, y, z, m);
        if (rc != 0) return rc;
    }
    return 0;
}

/*
 * Walk a sub-geometry — reads its header (endianness + type), validates
 * it carries Z + M dimensions, and dispatches to the LINESTRINGZM body
 * parser. MULTILINESTRINGZM members reach this function via recursion.
 */
static int lh_parse_sub_geometry(
    lh_wkb_reader* outer,
    int sub_idx,
    lh_traj_vertex_cb cb,
    void* ctx)
{
    /* Each nested geometry carries its own endianness byte per WKB spec. */
    uint8_t endian = lh_wkb_read_u8(outer);
    if (outer->error || endian > 1) return -1;
    outer->endian = endian;

    uint32_t type = lh_wkb_read_u32(outer);
    if (outer->error) return -1;

    /* Strip SRID if present. */
    if (type & WKB_FLAG_SRID) {
        (void)lh_wkb_read_u32(outer); /* discard SRID */
        if (outer->error) return -1;
    }

    uint32_t base = type & 0x000000FFu;
    int has_z = (type & WKB_FLAG_Z) ? 1 : 0;
    int has_m = (type & WKB_FLAG_M) ? 1 : 0;

    /* ISO type encoding (no high flags) — 1001..3007 ranges with implicit
     * dimensionality. PostGIS EWKB typically uses the high-flag form, but
     * accept either. */
    if (!has_z && !has_m && type >= 3000) {
        base   = type - 3000;
        has_z  = 1;
        has_m  = 1;
    } else if (!has_z && !has_m && type >= 2000) {
        base   = type - 2000;
        has_m  = 1;
    } else if (!has_z && !has_m && type >= 1000) {
        base   = type - 1000;
        has_z  = 1;
    }

    if (!has_z || !has_m) {
        /* Substrate guarantees 4D for every trajectory geom. Non-4D is
         * a parse-time error so callers fail loudly. */
        return -1;
    }

    if (base != WKB_TYPE_LINESTRING) {
        /* Sub-geometry of a MULTILINESTRING must be a LINESTRING. */
        return -1;
    }

    return lh_parse_linestring_zm(outer, sub_idx, cb, ctx);
}

HARTONOMOUS_API int hartonomous_trajectory_unpack(
    const uint8_t* wkb,
    size_t wkb_len,
    lh_traj_vertex_cb cb,
    void* ctx)
{
    if (wkb == NULL || cb == NULL || wkb_len < 5) {
        return -1;
    }

    lh_wkb_reader r = { wkb, wkb_len, 0, WKB_ENDIAN_LITTLE, 0 };

    /* Outer header. */
    uint8_t endian = lh_wkb_read_u8(&r);
    if (r.error || endian > 1) return -1;
    r.endian = endian;

    uint32_t type = lh_wkb_read_u32(&r);
    if (r.error) return -1;

    /* Strip SRID if present. */
    if (type & WKB_FLAG_SRID) {
        (void)lh_wkb_read_u32(&r);
        if (r.error) return -1;
    }

    uint32_t base = type & 0x000000FFu;
    int has_z = (type & WKB_FLAG_Z) ? 1 : 0;
    int has_m = (type & WKB_FLAG_M) ? 1 : 0;

    if (!has_z && !has_m && type >= 3000) {
        base  = type - 3000;
        has_z = 1;
        has_m = 1;
    }

    if (!has_z || !has_m) {
        return -1;
    }

    if (base == WKB_TYPE_LINESTRING) {
        return lh_parse_linestring_zm(&r, 0, cb, ctx);
    }

    if (base == WKB_TYPE_MULTILINESTRING) {
        uint32_t n_lines = lh_wkb_read_u32(&r);
        if (r.error) return -1;
        for (uint32_t i = 0; i < n_lines; i++) {
            int rc = lh_parse_sub_geometry(&r, (int)i, cb, ctx);
            if (rc != 0) return rc;
        }
        return 0;
    }

    /* Unsupported geometry type for trajectory unpack. */
    return -1;
}
