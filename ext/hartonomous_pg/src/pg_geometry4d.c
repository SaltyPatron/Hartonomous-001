/*
 * pg_geometry4d.c — umbrella 4D geometry type with tag-polymorphic payload.
 *
 * A single varlena holds any of the 10 subtype shapes documented in
 * docs/specs/native/4d-type-and-index.md §"geometry4d":
 *   POINT / LINESTRING / POLYGON / MULTIPOINT / MULTILINESTRING /
 *   MULTIPOLYGON / TRIANGLE / TIN / POLYHEDRALSURFACE / GEOMETRYCOLLECTION
 *
 * EWKT examples (SRID is always 0, so the `SRID=` prefix is optional and
 * always 0 when present):
 *   POINT4D (1 2 3 4)
 *   LINESTRING4D (0 0 0 0, 1 0 0 0, 1 1 1 1)
 *   POLYGON4D ((0 0 0 0, 1 0 0 0, 1 1 1 1, 0 0 0 0))
 *   MULTIPOINT4D ((1 2 3 4), (5 6 7 8))
 *   GEOMETRYCOLLECTION4D (POINT4D(1 2 3 4), LINESTRING4D(0 0 0 0, 1 1 1 1))
 *
 * EWKB: [u8 endian=1][u32 tag][u32 srid=0][payload]. Payload encodes counts
 * as u32 and coordinates as 4×f64 little-endian. Recursive for COLLECTION.
 *
 * Column types live in SQL as DOMAINs over geometry4d with CHECK(tag = N),
 * keeping 10 distinct SQL types while reusing one C implementation and
 * inheriting automatic cast-to-umbrella.
 */
#include "postgres.h"
#include "fmgr.h"
#include "libpq/pqformat.h"
#include "utils/builtins.h"
#include "utils/memutils.h"
#include "utils/array.h"
#include "utils/lsyscache.h"
#include "lib/stringinfo.h"

#include <string.h>
#include <ctype.h>
#include <math.h>
#include <float.h>

#include "hartonomous_pg.h"

/* Function registrations. */
PG_FUNCTION_INFO_V1(pg_geometry4d_in);
PG_FUNCTION_INFO_V1(pg_geometry4d_out);
PG_FUNCTION_INFO_V1(pg_geometry4d_recv);
PG_FUNCTION_INFO_V1(pg_geometry4d_send);
PG_FUNCTION_INFO_V1(pg_geometry4d_tag);
PG_FUNCTION_INFO_V1(pg_geometry4d_tag_name);
PG_FUNCTION_INFO_V1(pg_geometry4d_srid);
PG_FUNCTION_INFO_V1(pg_geometry4d_bbox);
PG_FUNCTION_INFO_V1(pg_geometry4d_num_geoms);
PG_FUNCTION_INFO_V1(pg_geometry4d_num_points);
PG_FUNCTION_INFO_V1(pg_geometry4d_eq);
PG_FUNCTION_INFO_V1(pg_geometry4d_ne);
PG_FUNCTION_INFO_V1(pg_geometry4d_from_point4d);
PG_FUNCTION_INFO_V1(pg_geometry4d_to_point4d);
PG_FUNCTION_INFO_V1(pg_geometry4d_from_linestring4d);
PG_FUNCTION_INFO_V1(pg_geometry4d_to_linestring4d);
PG_FUNCTION_INFO_V1(pg_geometry4d_makepoint);
PG_FUNCTION_INFO_V1(pg_geometry4d_makeline);

/* ── Allocation ─────────────────────────────────────────────────── */

Geometry4D *
g4d_new(uint32 tag, size_t payload_bytes)
{
    size_t sz = G4D_HDR_SIZE + payload_bytes;
    Geometry4D *g = (Geometry4D *) palloc0(sz);
    SET_VARSIZE(g, sz);
    g->endian = 1;
    g->tag    = tag;
    g->srid   = 0;
    return g;
}

/* ── Structural walk helpers ────────────────────────────────────── */

/* Skip whitespace. */
static void skip_ws(const char **p) { while (**p == ' ' || **p == '\t' || **p == '\n' || **p == '\r') (*p)++; }

/* Expect literal character (case-sensitive, after whitespace). */
static void expect_char(const char **p, char c, const char *what)
{
    skip_ws(p);
    if (**p != c)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("geometry4d: expected '%c' while parsing %s", c, what)));
    (*p)++;
}

/* Parse a single 4D coordinate: "x y z w". */
static void parse_coord(const char **p, double out[4])
{
    for (int i = 0; i < 4; i++)
    {
        char *endptr;
        double v;
        skip_ws(p);
        v = strtod(*p, &endptr);
        if (endptr == *p)
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                     errmsg("geometry4d: failed to parse coordinate")));
        out[i] = v;
        *p = endptr;
    }
}

/* Parse a comma-separated list of coord tuples inside matching parens.
 * Returns newly-palloc'd array of 4*n doubles and sets *n_out.
 */
static double *parse_point_list(const char **p, int32 *n_out)
{
    size_t cap = 8;
    size_t n = 0;
    double *buf = (double *) palloc(sizeof(double) * 4 * cap);
    expect_char(p, '(', "point list");
    for (;;)
    {
        if (n == cap)
        {
            cap *= 2;
            buf = (double *) repalloc(buf, sizeof(double) * 4 * cap);
        }
        parse_coord(p, buf + n * 4);
        n++;
        skip_ws(p);
        if (**p == ',') { (*p)++; continue; }
        if (**p == ')') { (*p)++; break; }
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("geometry4d: expected ',' or ')' in point list")));
    }
    *n_out = (int32) n;
    return buf;
}

/* Match a prefix word case-insensitively; returns 1 on match and advances. */
static bool match_word(const char **p, const char *word)
{
    skip_ws(p);
    const char *s = *p;
    size_t i = 0;
    while (word[i])
    {
        if (!s[i] || toupper((unsigned char) s[i]) != toupper((unsigned char) word[i]))
            return false;
        i++;
    }
    /* Allow the next char to be anything that can follow an identifier boundary. */
    *p = s + i;
    return true;
}

/* Optional "SRID=0;" prefix — accepted but must be 0. */
static void consume_optional_srid(const char **p)
{
    skip_ws(p);
    const char *s = *p;
    if ((s[0] == 'S' || s[0] == 's') && (s[1] == 'R' || s[1] == 'r') && (s[2] == 'I' || s[2] == 'i') &&
        (s[3] == 'D' || s[3] == 'd') && s[4] == '=')
    {
        char *endptr;
        long v;
        s += 5;
        v = strtol(s, &endptr, 10);
        if (endptr == s || v != 0)
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                     errmsg("geometry4d: only SRID=0 is supported")));
        s = endptr;
        skip_ws(&s);
        if (*s != ';')
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                     errmsg("geometry4d: expected ';' after SRID=0")));
        *p = s + 1;
    }
}

/* Dynamic byte buffer used by the EWKT parser to build a payload. */
typedef struct Buf
{
    char  *data;
    size_t len;
    size_t cap;
} Buf;

static void buf_init(Buf *b) { b->cap = 64; b->len = 0; b->data = palloc(b->cap); }
static void buf_ensure(Buf *b, size_t extra)
{
    if (b->len + extra <= b->cap) return;
    while (b->len + extra > b->cap) b->cap *= 2;
    b->data = repalloc(b->data, b->cap);
}
static void buf_append(Buf *b, const void *src, size_t n)
{
    buf_ensure(b, n);
    memcpy(b->data + b->len, src, n);
    b->len += n;
}
static void buf_u32(Buf *b, uint32 v) { buf_append(b, &v, sizeof(v)); }
/* coord blocks are written via buf_append directly. */

/* Forward decl — GEOMETRYCOLLECTION parses arbitrary tagged payloads. */
static void parse_payload(const char **p, uint32 tag, Buf *out);
static uint32 parse_geom_token_tag(const char **p);
static void parse_tagged_item(const char **p, Buf *out);

static void parse_point_payload(const char **p, Buf *out)
{
    int32 n = 0;
    double *pts = parse_point_list(p, &n);
    if (n != 1)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("geometry4d: POINT4D requires exactly one coordinate")));
    buf_append(out, pts, 4 * sizeof(double));
    pfree(pts);
}

static void parse_linestring_payload(const char **p, Buf *out)
{
    int32 n = 0;
    double *pts = parse_point_list(p, &n);
    if (n < 2)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("geometry4d: LINESTRING4D requires at least 2 points")));
    buf_u32(out, (uint32) n);
    buf_append(out, pts, (size_t) n * 4 * sizeof(double));
    pfree(pts);
}

/* POLYGON : ((ring1), (ring2), ...)  each ring: (p,p,p,p_close) */
static void parse_polygon_payload(const char **p, Buf *out)
{
    expect_char(p, '(', "POLYGON4D rings");
    size_t nrings_pos = out->len;
    buf_u32(out, 0);
    uint32 nrings = 0;
    for (;;)
    {
        int32 n = 0;
        double *pts = parse_point_list(p, &n);
        if (n < 4)
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                     errmsg("geometry4d: POLYGON4D ring needs >= 4 points (closed)")));
        /* closedness: first == last. */
        for (int j = 0; j < 4; j++)
        {
            if (pts[j] != pts[(n - 1) * 4 + j])
                ereport(ERROR,
                        (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                         errmsg("geometry4d: POLYGON4D ring must be closed")));
        }
        buf_u32(out, (uint32) n);
        buf_append(out, pts, (size_t) n * 4 * sizeof(double));
        pfree(pts);
        nrings++;
        skip_ws(p);
        if (**p == ',') { (*p)++; continue; }
        if (**p == ')') { (*p)++; break; }
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("geometry4d: expected ',' or ')' between POLYGON4D rings")));
    }
    memcpy(out->data + nrings_pos, &nrings, sizeof(uint32));
}

static void parse_multipoint_payload(const char **p, Buf *out)
{
    expect_char(p, '(', "MULTIPOINT4D");
    size_t cnt_pos = out->len;
    buf_u32(out, 0);
    uint32 n = 0;
    for (;;)
    {
        int32 np = 0;
        double *pts = parse_point_list(p, &np);
        if (np != 1)
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                     errmsg("geometry4d: MULTIPOINT4D subitem must be a single coord")));
        buf_append(out, pts, 4 * sizeof(double));
        pfree(pts);
        n++;
        skip_ws(p);
        if (**p == ',') { (*p)++; continue; }
        if (**p == ')') { (*p)++; break; }
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("geometry4d: expected ',' or ')' in MULTIPOINT4D")));
    }
    memcpy(out->data + cnt_pos, &n, sizeof(uint32));
}

static void parse_multilinestring_payload(const char **p, Buf *out)
{
    expect_char(p, '(', "MULTILINESTRING4D");
    size_t cnt_pos = out->len;
    buf_u32(out, 0);
    uint32 n = 0;
    for (;;)
    {
        parse_linestring_payload(p, out);
        n++;
        skip_ws(p);
        if (**p == ',') { (*p)++; continue; }
        if (**p == ')') { (*p)++; break; }
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("geometry4d: expected ',' or ')' in MULTILINESTRING4D")));
    }
    memcpy(out->data + cnt_pos, &n, sizeof(uint32));
}

static void parse_multipolygon_payload(const char **p, Buf *out)
{
    expect_char(p, '(', "MULTIPOLYGON4D");
    size_t cnt_pos = out->len;
    buf_u32(out, 0);
    uint32 n = 0;
    for (;;)
    {
        parse_polygon_payload(p, out);
        n++;
        skip_ws(p);
        if (**p == ',') { (*p)++; continue; }
        if (**p == ')') { (*p)++; break; }
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("geometry4d: expected ',' or ')' in MULTIPOLYGON4D")));
    }
    memcpy(out->data + cnt_pos, &n, sizeof(uint32));
}

/* TRIANGLE : single closed ring of exactly 4 points (3 distinct + close). */
static void parse_triangle_payload(const char **p, Buf *out)
{
    expect_char(p, '(', "TRIANGLE4D");
    int32 n = 0;
    double *pts = parse_point_list(p, &n);
    if (n != 4)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("geometry4d: TRIANGLE4D ring must have exactly 4 points")));
    for (int j = 0; j < 4; j++)
        if (pts[j] != pts[3 * 4 + j])
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                     errmsg("geometry4d: TRIANGLE4D ring must be closed")));
    buf_u32(out, 1u);       /* nrings */
    buf_u32(out, 4u);       /* npoints */
    buf_append(out, pts, 4 * 4 * sizeof(double));
    pfree(pts);
    expect_char(p, ')', "TRIANGLE4D");
}

static void parse_tin_payload(const char **p, Buf *out)
{
    expect_char(p, '(', "TIN4D");
    size_t cnt_pos = out->len;
    buf_u32(out, 0);
    uint32 n = 0;
    for (;;)
    {
        parse_triangle_payload(p, out);
        n++;
        skip_ws(p);
        if (**p == ',') { (*p)++; continue; }
        if (**p == ')') { (*p)++; break; }
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("geometry4d: expected ',' or ')' in TIN4D")));
    }
    memcpy(out->data + cnt_pos, &n, sizeof(uint32));
}

static void parse_polyhedralsurface_payload(const char **p, Buf *out)
{
    expect_char(p, '(', "POLYHEDRALSURFACE4D");
    size_t cnt_pos = out->len;
    buf_u32(out, 0);
    uint32 n = 0;
    for (;;)
    {
        parse_polygon_payload(p, out);
        n++;
        skip_ws(p);
        if (**p == ',') { (*p)++; continue; }
        if (**p == ')') { (*p)++; break; }
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("geometry4d: expected ',' or ')' in POLYHEDRALSURFACE4D")));
    }
    memcpy(out->data + cnt_pos, &n, sizeof(uint32));
}

static uint32
parse_geom_token_tag(const char **p)
{
    skip_ws(p);
    if (match_word(p, "POINT4D"))                 return G4D_TAG_POINT;
    if (match_word(p, "LINESTRING4D"))            return G4D_TAG_LINESTRING;
    if (match_word(p, "POLYGON4D"))               return G4D_TAG_POLYGON;
    if (match_word(p, "MULTIPOINT4D"))            return G4D_TAG_MULTIPOINT;
    if (match_word(p, "MULTILINESTRING4D"))       return G4D_TAG_MULTILINESTRING;
    if (match_word(p, "MULTIPOLYGON4D"))          return G4D_TAG_MULTIPOLYGON;
    if (match_word(p, "TRIANGLE4D"))              return G4D_TAG_TRIANGLE;
    if (match_word(p, "TIN4D"))                   return G4D_TAG_TIN;
    if (match_word(p, "POLYHEDRALSURFACE4D"))     return G4D_TAG_POLYHEDRALSURFACE;
    if (match_word(p, "GEOMETRYCOLLECTION4D"))    return G4D_TAG_GEOMETRYCOLLECTION;
    ereport(ERROR,
            (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
             errmsg("geometry4d: unknown geometry type token")));
    return 0;
}

static void
parse_payload(const char **p, uint32 tag, Buf *out)
{
    switch (tag)
    {
        case G4D_TAG_POINT:               parse_point_payload(p, out); break;
        case G4D_TAG_LINESTRING:          parse_linestring_payload(p, out); break;
        case G4D_TAG_POLYGON:             parse_polygon_payload(p, out); break;
        case G4D_TAG_MULTIPOINT:          parse_multipoint_payload(p, out); break;
        case G4D_TAG_MULTILINESTRING:     parse_multilinestring_payload(p, out); break;
        case G4D_TAG_MULTIPOLYGON:        parse_multipolygon_payload(p, out); break;
        case G4D_TAG_TRIANGLE:            parse_triangle_payload(p, out); break;
        case G4D_TAG_TIN:                 parse_tin_payload(p, out); break;
        case G4D_TAG_POLYHEDRALSURFACE:   parse_polyhedralsurface_payload(p, out); break;
        case G4D_TAG_GEOMETRYCOLLECTION:
        {
            expect_char(p, '(', "GEOMETRYCOLLECTION4D");
            size_t cnt_pos = out->len;
            buf_u32(out, 0);
            uint32 n = 0;
            for (;;)
            {
                parse_tagged_item(p, out);
                n++;
                skip_ws(p);
                if (**p == ',') { (*p)++; continue; }
                if (**p == ')') { (*p)++; break; }
                ereport(ERROR,
                        (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                         errmsg("geometry4d: expected ',' or ')' in GEOMETRYCOLLECTION4D")));
            }
            memcpy(out->data + cnt_pos, &n, sizeof(uint32));
            break;
        }
        default:
            ereport(ERROR, (errmsg("geometry4d: invalid tag %u", tag)));
    }
}

static void
parse_tagged_item(const char **p, Buf *out)
{
    uint32 tag = parse_geom_token_tag(p);
    buf_u32(out, tag);
    parse_payload(p, tag, out);
}

/* ── EWKT entry ─────────────────────────────────────────────────── */

Datum
pg_geometry4d_in(PG_FUNCTION_ARGS)
{
    char *str = PG_GETARG_CSTRING(0);
    const char *p = str;
    uint32 tag;
    Buf payload;
    Geometry4D *g;

    consume_optional_srid(&p);
    tag = parse_geom_token_tag(&p);
    buf_init(&payload);
    parse_payload(&p, tag, &payload);
    skip_ws(&p);
    if (*p != '\0')
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("geometry4d: trailing garbage: \"%s\"", p)));

    g = g4d_new(tag, payload.len);
    memcpy(G4D_PAYLOAD(g), payload.data, payload.len);
    pfree(payload.data);
    PG_RETURN_GEOMETRY4D_P(g);
}

/* ── EWKT serializer ────────────────────────────────────────────── */

static void emit_coord(StringInfo s, const double *c)
{
    appendStringInfo(s, "%.17g %.17g %.17g %.17g", c[0], c[1], c[2], c[3]);
}

static void emit_point_list(StringInfo s, const char **cur, uint32 npoints)
{
    appendStringInfoChar(s, '(');
    for (uint32 i = 0; i < npoints; i++)
    {
        if (i) appendStringInfoString(s, ", ");
        emit_coord(s, (const double *) *cur);
        *cur += 4 * sizeof(double);
    }
    appendStringInfoChar(s, ')');
}

static void emit_polygon(StringInfo s, const char **cur)
{
    uint32 nrings; memcpy(&nrings, *cur, sizeof(uint32)); *cur += sizeof(uint32);
    appendStringInfoChar(s, '(');
    for (uint32 r = 0; r < nrings; r++)
    {
        uint32 np; memcpy(&np, *cur, sizeof(uint32)); *cur += sizeof(uint32);
        if (r) appendStringInfoString(s, ", ");
        emit_point_list(s, cur, np);
    }
    appendStringInfoChar(s, ')');
}

static void emit_payload(StringInfo s, uint32 tag, const char **cur);

static void emit_item(StringInfo s, const char **cur)
{
    uint32 tag; memcpy(&tag, *cur, sizeof(uint32)); *cur += sizeof(uint32);
    switch (tag)
    {
        case G4D_TAG_POINT: appendStringInfoString(s, "POINT4D"); break;
        case G4D_TAG_LINESTRING: appendStringInfoString(s, "LINESTRING4D"); break;
        case G4D_TAG_POLYGON: appendStringInfoString(s, "POLYGON4D"); break;
        case G4D_TAG_MULTIPOINT: appendStringInfoString(s, "MULTIPOINT4D"); break;
        case G4D_TAG_MULTILINESTRING: appendStringInfoString(s, "MULTILINESTRING4D"); break;
        case G4D_TAG_MULTIPOLYGON: appendStringInfoString(s, "MULTIPOLYGON4D"); break;
        case G4D_TAG_TRIANGLE: appendStringInfoString(s, "TRIANGLE4D"); break;
        case G4D_TAG_TIN: appendStringInfoString(s, "TIN4D"); break;
        case G4D_TAG_POLYHEDRALSURFACE: appendStringInfoString(s, "POLYHEDRALSURFACE4D"); break;
        case G4D_TAG_GEOMETRYCOLLECTION: appendStringInfoString(s, "GEOMETRYCOLLECTION4D"); break;
        default: ereport(ERROR, (errmsg("geometry4d: invalid tag %u", tag)));
    }
    emit_payload(s, tag, cur);
}

static void
emit_payload(StringInfo s, uint32 tag, const char **cur)
{
    switch (tag)
    {
        case G4D_TAG_POINT:
        {
            appendStringInfoChar(s, '(');
            emit_coord(s, (const double *) *cur);
            appendStringInfoChar(s, ')');
            *cur += 4 * sizeof(double);
            break;
        }
        case G4D_TAG_LINESTRING:
        {
            uint32 np; memcpy(&np, *cur, sizeof(uint32)); *cur += sizeof(uint32);
            emit_point_list(s, cur, np);
            break;
        }
        case G4D_TAG_POLYGON:
            emit_polygon(s, cur);
            break;
        case G4D_TAG_MULTIPOINT:
        {
            uint32 n; memcpy(&n, *cur, sizeof(uint32)); *cur += sizeof(uint32);
            appendStringInfoChar(s, '(');
            for (uint32 i = 0; i < n; i++)
            {
                if (i) appendStringInfoString(s, ", ");
                appendStringInfoChar(s, '(');
                emit_coord(s, (const double *) *cur);
                appendStringInfoChar(s, ')');
                *cur += 4 * sizeof(double);
            }
            appendStringInfoChar(s, ')');
            break;
        }
        case G4D_TAG_MULTILINESTRING:
        {
            uint32 n; memcpy(&n, *cur, sizeof(uint32)); *cur += sizeof(uint32);
            appendStringInfoChar(s, '(');
            for (uint32 i = 0; i < n; i++)
            {
                uint32 np; memcpy(&np, *cur, sizeof(uint32)); *cur += sizeof(uint32);
                if (i) appendStringInfoString(s, ", ");
                emit_point_list(s, cur, np);
            }
            appendStringInfoChar(s, ')');
            break;
        }
        case G4D_TAG_MULTIPOLYGON:
        case G4D_TAG_POLYHEDRALSURFACE:
        {
            uint32 n; memcpy(&n, *cur, sizeof(uint32)); *cur += sizeof(uint32);
            appendStringInfoChar(s, '(');
            for (uint32 i = 0; i < n; i++)
            {
                if (i) appendStringInfoString(s, ", ");
                emit_polygon(s, cur);
            }
            appendStringInfoChar(s, ')');
            break;
        }
        case G4D_TAG_TRIANGLE:
            emit_polygon(s, cur);
            break;
        case G4D_TAG_TIN:
        {
            uint32 n; memcpy(&n, *cur, sizeof(uint32)); *cur += sizeof(uint32);
            appendStringInfoChar(s, '(');
            for (uint32 i = 0; i < n; i++)
            {
                if (i) appendStringInfoString(s, ", ");
                emit_polygon(s, cur);
            }
            appendStringInfoChar(s, ')');
            break;
        }
        case G4D_TAG_GEOMETRYCOLLECTION:
        {
            uint32 n; memcpy(&n, *cur, sizeof(uint32)); *cur += sizeof(uint32);
            appendStringInfoChar(s, '(');
            for (uint32 i = 0; i < n; i++)
            {
                if (i) appendStringInfoString(s, ", ");
                emit_item(s, cur);
            }
            appendStringInfoChar(s, ')');
            break;
        }
        default:
            ereport(ERROR, (errmsg("geometry4d: invalid tag %u", tag)));
    }
}

Datum
pg_geometry4d_out(PG_FUNCTION_ARGS)
{
    Geometry4D *g = PG_GETARG_GEOMETRY4D_P(0);
    StringInfoData s;
    const char *cur = G4D_PAYLOAD(g);
    initStringInfo(&s);
    /* We never emit the SRID=0; prefix since SRID is always 0 — keeps round-trips short. */
    switch (g->tag)
    {
        case G4D_TAG_POINT: appendStringInfoString(&s, "POINT4D"); break;
        case G4D_TAG_LINESTRING: appendStringInfoString(&s, "LINESTRING4D"); break;
        case G4D_TAG_POLYGON: appendStringInfoString(&s, "POLYGON4D"); break;
        case G4D_TAG_MULTIPOINT: appendStringInfoString(&s, "MULTIPOINT4D"); break;
        case G4D_TAG_MULTILINESTRING: appendStringInfoString(&s, "MULTILINESTRING4D"); break;
        case G4D_TAG_MULTIPOLYGON: appendStringInfoString(&s, "MULTIPOLYGON4D"); break;
        case G4D_TAG_TRIANGLE: appendStringInfoString(&s, "TRIANGLE4D"); break;
        case G4D_TAG_TIN: appendStringInfoString(&s, "TIN4D"); break;
        case G4D_TAG_POLYHEDRALSURFACE: appendStringInfoString(&s, "POLYHEDRALSURFACE4D"); break;
        case G4D_TAG_GEOMETRYCOLLECTION: appendStringInfoString(&s, "GEOMETRYCOLLECTION4D"); break;
        default: ereport(ERROR, (errmsg("geometry4d: invalid tag %u on output", g->tag)));
    }
    emit_payload(&s, g->tag, &cur);
    PG_RETURN_CSTRING(s.data);
}

/* ── Binary (EWKB-compat) recv/send ─────────────────────────────── */

Datum
pg_geometry4d_recv(PG_FUNCTION_ARGS)
{
    StringInfo buf = (StringInfo) PG_GETARG_POINTER(0);
    uint8 endian;
    uint32 tag, srid;
    size_t remaining;
    Geometry4D *g;

    endian = (uint8) pq_getmsgbyte(buf);
    if (endian != 1)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION),
                 errmsg("geometry4d: only little-endian wire format is supported")));
    tag = (uint32) pq_getmsgint(buf, 4);
    srid = (uint32) pq_getmsgint(buf, 4);
    if (srid != 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION),
                 errmsg("geometry4d: only SRID=0 is supported, got %u", srid)));
    remaining = buf->len - buf->cursor;
    g = g4d_new(tag, remaining);
    memcpy(G4D_PAYLOAD(g), buf->data + buf->cursor, remaining);
    buf->cursor += remaining;
    if (!g4d_validate(g))
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION),
                 errmsg("geometry4d: invalid payload for tag %u", tag)));
    PG_RETURN_GEOMETRY4D_P(g);
}

Datum
pg_geometry4d_send(PG_FUNCTION_ARGS)
{
    Geometry4D *g = PG_GETARG_GEOMETRY4D_P(0);
    size_t plen = G4D_PAYLOAD_SIZE(g);
    StringInfoData buf;
    pq_begintypsend(&buf);
    pq_sendbyte(&buf, 1);              /* endian */
    pq_sendint32(&buf, g->tag);
    pq_sendint32(&buf, g->srid);
    pq_sendbytes(&buf, G4D_PAYLOAD(g), (int) plen);
    PG_RETURN_BYTEA_P(pq_endtypsend(&buf));
}

/* ── Validation and bbox walk ───────────────────────────────────── */

/* Walk the payload of one tagged geometry starting at *cur, pointing into
 * a buffer with `end` bytes available. Returns new cursor position, or
 * sets *bad=true and returns the original cursor on failure. Updates
 * running bbox min/max if non-null.
 */
static const char *
walk_payload(uint32 tag, const char *cur, const char *end, bool *bad,
             double *bb_min, double *bb_max)
{
#define NEED(n) do { if ((size_t)(end - cur) < (size_t)(n)) { *bad = true; return cur; } } while (0)
#define COORD_BUMP(c) do { \
    if (bb_min) { \
        for (int _i = 0; _i < 4; _i++) { \
            if ((c)[_i] < bb_min[_i]) bb_min[_i] = (c)[_i]; \
            if ((c)[_i] > bb_max[_i]) bb_max[_i] = (c)[_i]; \
        } \
    } } while (0)

    switch (tag)
    {
        case G4D_TAG_POINT:
        {
            NEED(4 * sizeof(double));
            COORD_BUMP((const double *) cur);
            cur += 4 * sizeof(double);
            return cur;
        }
        case G4D_TAG_LINESTRING:
        {
            uint32 n; NEED(sizeof(uint32)); memcpy(&n, cur, sizeof(uint32)); cur += sizeof(uint32);
            if (n < 2) { *bad = true; return cur; }
            NEED((size_t) n * 4 * sizeof(double));
            for (uint32 i = 0; i < n; i++)
            {
                COORD_BUMP((const double *) cur);
                cur += 4 * sizeof(double);
            }
            return cur;
        }
        case G4D_TAG_POLYGON:
        case G4D_TAG_TRIANGLE:
        {
            uint32 nrings; NEED(sizeof(uint32)); memcpy(&nrings, cur, sizeof(uint32)); cur += sizeof(uint32);
            if (tag == G4D_TAG_TRIANGLE && nrings != 1) { *bad = true; return cur; }
            for (uint32 r = 0; r < nrings; r++)
            {
                uint32 np; NEED(sizeof(uint32)); memcpy(&np, cur, sizeof(uint32)); cur += sizeof(uint32);
                if (np < 4) { *bad = true; return cur; }
                if (tag == G4D_TAG_TRIANGLE && np != 4) { *bad = true; return cur; }
                NEED((size_t) np * 4 * sizeof(double));
                for (uint32 i = 0; i < np; i++)
                {
                    COORD_BUMP((const double *) cur);
                    cur += 4 * sizeof(double);
                }
            }
            return cur;
        }
        case G4D_TAG_MULTIPOINT:
        {
            uint32 n; NEED(sizeof(uint32)); memcpy(&n, cur, sizeof(uint32)); cur += sizeof(uint32);
            NEED((size_t) n * 4 * sizeof(double));
            for (uint32 i = 0; i < n; i++)
            {
                COORD_BUMP((const double *) cur);
                cur += 4 * sizeof(double);
            }
            return cur;
        }
        case G4D_TAG_MULTILINESTRING:
        {
            uint32 n; NEED(sizeof(uint32)); memcpy(&n, cur, sizeof(uint32)); cur += sizeof(uint32);
            for (uint32 i = 0; i < n; i++)
            {
                cur = walk_payload(G4D_TAG_LINESTRING, cur, end, bad, bb_min, bb_max);
                if (*bad) return cur;
            }
            return cur;
        }
        case G4D_TAG_MULTIPOLYGON:
        case G4D_TAG_POLYHEDRALSURFACE:
        {
            uint32 n; NEED(sizeof(uint32)); memcpy(&n, cur, sizeof(uint32)); cur += sizeof(uint32);
            for (uint32 i = 0; i < n; i++)
            {
                cur = walk_payload(G4D_TAG_POLYGON, cur, end, bad, bb_min, bb_max);
                if (*bad) return cur;
            }
            return cur;
        }
        case G4D_TAG_TIN:
        {
            uint32 n; NEED(sizeof(uint32)); memcpy(&n, cur, sizeof(uint32)); cur += sizeof(uint32);
            for (uint32 i = 0; i < n; i++)
            {
                cur = walk_payload(G4D_TAG_TRIANGLE, cur, end, bad, bb_min, bb_max);
                if (*bad) return cur;
            }
            return cur;
        }
        case G4D_TAG_GEOMETRYCOLLECTION:
        {
            uint32 n; NEED(sizeof(uint32)); memcpy(&n, cur, sizeof(uint32)); cur += sizeof(uint32);
            for (uint32 i = 0; i < n; i++)
            {
                uint32 stag; NEED(sizeof(uint32)); memcpy(&stag, cur, sizeof(uint32)); cur += sizeof(uint32);
                if (stag < 1 || stag > 10) { *bad = true; return cur; }
                cur = walk_payload(stag, cur, end, bad, bb_min, bb_max);
                if (*bad) return cur;
            }
            return cur;
        }
        default:
            *bad = true;
            return cur;
    }
#undef NEED
#undef COORD_BUMP
}

bool
g4d_validate(const Geometry4D *g)
{
    if (g->endian != 1) return false;
    if (g->srid != 0) return false;
    if (g->tag < 1 || g->tag > 10) return false;
    bool bad = false;
    const char *p = G4D_PAYLOAD(g);
    const char *end = p + G4D_PAYLOAD_SIZE(g);
    const char *after = walk_payload(g->tag, p, end, &bad, NULL, NULL);
    if (bad) return false;
    if (after != end) return false;
    return true;
}

void
g4d_compute_bbox(const Geometry4D *g, Box4D *out)
{
    out->min[0] = out->min[1] = out->min[2] = out->min[3] =  DBL_MAX;
    out->max[0] = out->max[1] = out->max[2] = out->max[3] = -DBL_MAX;
    bool bad = false;
    const char *p = G4D_PAYLOAD(g);
    const char *end = p + G4D_PAYLOAD_SIZE(g);
    walk_payload(g->tag, p, end, &bad, out->min, out->max);
    if (bad)
        ereport(ERROR, (errmsg("geometry4d: corrupt payload during bbox walk")));
    /* Empty geometry → degenerate box at origin (shouldn't happen given our
     * minimum-count checks, but defend the public invariant). */
    if (out->min[0] == DBL_MAX)
    {
        for (int i = 0; i < 4; i++) { out->min[i] = 0.0; out->max[i] = 0.0; }
    }
}

/* ── Accessors ──────────────────────────────────────────────────── */

Datum
pg_geometry4d_tag(PG_FUNCTION_ARGS)
{
    Geometry4D *g = PG_GETARG_GEOMETRY4D_P(0);
    PG_RETURN_INT32((int32) g->tag);
}

Datum
pg_geometry4d_tag_name(PG_FUNCTION_ARGS)
{
    Geometry4D *g = PG_GETARG_GEOMETRY4D_P(0);
    const char *name;
    switch (g->tag)
    {
        case G4D_TAG_POINT: name = "POINT4D"; break;
        case G4D_TAG_LINESTRING: name = "LINESTRING4D"; break;
        case G4D_TAG_POLYGON: name = "POLYGON4D"; break;
        case G4D_TAG_MULTIPOINT: name = "MULTIPOINT4D"; break;
        case G4D_TAG_MULTILINESTRING: name = "MULTILINESTRING4D"; break;
        case G4D_TAG_MULTIPOLYGON: name = "MULTIPOLYGON4D"; break;
        case G4D_TAG_TRIANGLE: name = "TRIANGLE4D"; break;
        case G4D_TAG_TIN: name = "TIN4D"; break;
        case G4D_TAG_POLYHEDRALSURFACE: name = "POLYHEDRALSURFACE4D"; break;
        case G4D_TAG_GEOMETRYCOLLECTION: name = "GEOMETRYCOLLECTION4D"; break;
        default: name = "UNKNOWN"; break;
    }
    PG_RETURN_TEXT_P(cstring_to_text(name));
}

Datum
pg_geometry4d_srid(PG_FUNCTION_ARGS)
{
    Geometry4D *g = PG_GETARG_GEOMETRY4D_P(0);
    PG_RETURN_INT32((int32) g->srid);
}

Datum
pg_geometry4d_bbox(PG_FUNCTION_ARGS)
{
    Geometry4D *g = PG_GETARG_GEOMETRY4D_P(0);
    Box4D *b = box4d_alloc();
    g4d_compute_bbox(g, b);
    PG_RETURN_BOX4D_P(b);
}

Datum
pg_geometry4d_num_geoms(PG_FUNCTION_ARGS)
{
    Geometry4D *g = PG_GETARG_GEOMETRY4D_P(0);
    const char *p = G4D_PAYLOAD(g);
    uint32 n;
    switch (g->tag)
    {
        case G4D_TAG_POINT:
        case G4D_TAG_LINESTRING:
        case G4D_TAG_POLYGON:
        case G4D_TAG_TRIANGLE:
            PG_RETURN_INT32(1);
        case G4D_TAG_MULTIPOINT:
        case G4D_TAG_MULTILINESTRING:
        case G4D_TAG_MULTIPOLYGON:
        case G4D_TAG_TIN:
        case G4D_TAG_POLYHEDRALSURFACE:
        case G4D_TAG_GEOMETRYCOLLECTION:
            memcpy(&n, p, sizeof(uint32));
            PG_RETURN_INT32((int32) n);
    }
    PG_RETURN_INT32(0);
}

/* Count all points across a tagged payload. Advances *cur to the byte
 * after the item. Recurses into GEOMETRYCOLLECTION children. */
static int64
count_points(uint32 tag, const char **cur, const char *end)
{
#define READ_U32_CP(dst) do { if ((size_t)(end - *cur) < sizeof(uint32)) ereport(ERROR, (errmsg("geometry4d: truncated"))); memcpy(&(dst), *cur, sizeof(uint32)); *cur += sizeof(uint32); } while (0)
#define SKIP_COORDS_CP(n) do { if ((size_t)(end - *cur) < (size_t)(n) * 4 * sizeof(double)) ereport(ERROR, (errmsg("geometry4d: truncated"))); *cur += (size_t)(n) * 4 * sizeof(double); } while (0)
    int64 total = 0;
    switch (tag)
    {
        case G4D_TAG_POINT:
            SKIP_COORDS_CP(1);
            return 1;
        case G4D_TAG_LINESTRING:
        {
            uint32 np; READ_U32_CP(np); SKIP_COORDS_CP(np); return (int64) np;
        }
        case G4D_TAG_POLYGON:
        case G4D_TAG_TRIANGLE:
        {
            uint32 nrings; READ_U32_CP(nrings);
            for (uint32 r = 0; r < nrings; r++)
            { uint32 np; READ_U32_CP(np); SKIP_COORDS_CP(np); total += (int64) np; }
            return total;
        }
        case G4D_TAG_MULTIPOINT:
        {
            uint32 n; READ_U32_CP(n); SKIP_COORDS_CP(n); return (int64) n;
        }
        case G4D_TAG_MULTILINESTRING:
        {
            uint32 n; READ_U32_CP(n);
            for (uint32 i = 0; i < n; i++) total += count_points(G4D_TAG_LINESTRING, cur, end);
            return total;
        }
        case G4D_TAG_MULTIPOLYGON:
        case G4D_TAG_POLYHEDRALSURFACE:
        {
            uint32 n; READ_U32_CP(n);
            for (uint32 i = 0; i < n; i++) total += count_points(G4D_TAG_POLYGON, cur, end);
            return total;
        }
        case G4D_TAG_TIN:
        {
            uint32 n; READ_U32_CP(n);
            for (uint32 i = 0; i < n; i++) total += count_points(G4D_TAG_TRIANGLE, cur, end);
            return total;
        }
        case G4D_TAG_GEOMETRYCOLLECTION:
        {
            uint32 n; READ_U32_CP(n);
            for (uint32 i = 0; i < n; i++)
            {
                uint32 stag; READ_U32_CP(stag);
                if (stag < 1 || stag > 10) ereport(ERROR, (errmsg("geometry4d: invalid child tag %u", stag)));
                total += count_points(stag, cur, end);
            }
            return total;
        }
        default:
            ereport(ERROR, (errmsg("geometry4d: invalid tag %u in count", tag)));
    }
#undef READ_U32_CP
#undef SKIP_COORDS_CP
    return 0;
}

/* Count all distinct points (no dedup). Closed polygon rings count the
 * terminal repeat. */
Datum
pg_geometry4d_num_points(PG_FUNCTION_ARGS)
{
    Geometry4D *g = PG_GETARG_GEOMETRY4D_P(0);
    const char *cur = G4D_PAYLOAD(g);
    const char *end = cur + G4D_PAYLOAD_SIZE(g);
    int64 n = count_points(g->tag, &cur, end);
    PG_RETURN_INT64(n);
}

/* ── Equality (byte-exact) ─────────────────────────────────────── */

Datum
pg_geometry4d_eq(PG_FUNCTION_ARGS)
{
    Geometry4D *a = PG_GETARG_GEOMETRY4D_P(0);
    Geometry4D *b = PG_GETARG_GEOMETRY4D_P(1);
    if (VARSIZE_ANY(a) != VARSIZE_ANY(b)) PG_RETURN_BOOL(false);
    PG_RETURN_BOOL(memcmp(a, b, VARSIZE_ANY(a)) == 0);
}

Datum
pg_geometry4d_ne(PG_FUNCTION_ARGS)
{
    Geometry4D *a = PG_GETARG_GEOMETRY4D_P(0);
    Geometry4D *b = PG_GETARG_GEOMETRY4D_P(1);
    if (VARSIZE_ANY(a) != VARSIZE_ANY(b)) PG_RETURN_BOOL(true);
    PG_RETURN_BOOL(memcmp(a, b, VARSIZE_ANY(a)) != 0);
}

/* ── Casts ───────────────────────────────────────────────────────── */

Datum
pg_geometry4d_from_point4d(PG_FUNCTION_ARGS)
{
    Point4D *p = PG_GETARG_POINT4D_P(0);
    Geometry4D *g = g4d_new(G4D_TAG_POINT, 4 * sizeof(double));
    memcpy(G4D_PAYLOAD(g), p->x, 4 * sizeof(double));
    PG_RETURN_GEOMETRY4D_P(g);
}

Datum
pg_geometry4d_to_point4d(PG_FUNCTION_ARGS)
{
    Geometry4D *g = PG_GETARG_GEOMETRY4D_P(0);
    Point4D *p;
    if (g->tag != G4D_TAG_POINT)
        ereport(ERROR,
                (errcode(ERRCODE_CANNOT_COERCE),
                 errmsg("geometry4d: cannot cast tag=%u to point4d", g->tag)));
    p = point4d_alloc();
    memcpy(p->x, G4D_PAYLOAD(g), 4 * sizeof(double));
    PG_RETURN_POINT4D_P(p);
}

Datum
pg_geometry4d_from_linestring4d(PG_FUNCTION_ARGS)
{
    LineString4D *ls = PG_GETARG_LINESTRING4D_P(0);
    size_t plen = sizeof(uint32) + (size_t) ls->npoints * 4 * sizeof(double);
    Geometry4D *g = g4d_new(G4D_TAG_LINESTRING, plen);
    char *cur = G4D_PAYLOAD(g);
    uint32 n = (uint32) ls->npoints;
    memcpy(cur, &n, sizeof(uint32)); cur += sizeof(uint32);
    for (int i = 0; i < ls->npoints; i++)
    {
        memcpy(cur, ls->points[i].x, 4 * sizeof(double));
        cur += 4 * sizeof(double);
    }
    PG_RETURN_GEOMETRY4D_P(g);
}

Datum
pg_geometry4d_to_linestring4d(PG_FUNCTION_ARGS)
{
    Geometry4D *g = PG_GETARG_GEOMETRY4D_P(0);
    LineString4D *ls;
    uint32 n;
    const char *p;
    size_t sz;
    if (g->tag != G4D_TAG_LINESTRING)
        ereport(ERROR,
                (errcode(ERRCODE_CANNOT_COERCE),
                 errmsg("geometry4d: cannot cast tag=%u to linestring4d", g->tag)));
    p = G4D_PAYLOAD(g);
    memcpy(&n, p, sizeof(uint32)); p += sizeof(uint32);
    sz = LS4D_SIZE(n);
    ls = (LineString4D *) palloc(sz);
    SET_VARSIZE(ls, sz);
    ls->npoints = (int32) n;
    for (uint32 i = 0; i < n; i++)
    {
        memcpy(ls->points[i].x, p, 4 * sizeof(double));
        p += 4 * sizeof(double);
    }
    PG_RETURN_LINESTRING4D_P(ls);
}

/* ── Constructors ────────────────────────────────────────────────── */

/* geometry4d_makepoint(x1,x2,x3,x4) */
Datum
pg_geometry4d_makepoint(PG_FUNCTION_ARGS)
{
    double c[4] = {
        PG_GETARG_FLOAT8(0),
        PG_GETARG_FLOAT8(1),
        PG_GETARG_FLOAT8(2),
        PG_GETARG_FLOAT8(3),
    };
    Geometry4D *g = g4d_new(G4D_TAG_POINT, 4 * sizeof(double));
    memcpy(G4D_PAYLOAD(g), c, 4 * sizeof(double));
    PG_RETURN_GEOMETRY4D_P(g);
}

/* geometry4d_makeline(point4d[]) */
Datum
pg_geometry4d_makeline(PG_FUNCTION_ARGS)
{
    ArrayType *arr = PG_GETARG_ARRAYTYPE_P(0);
    int32 npts;
    Datum *elems;
    bool  *nulls;
    Oid    elemtype;
    int16  typlen;
    bool   typbyval;
    char   typalign;
    Geometry4D *g;
    size_t plen;
    char *cur;

    elemtype = ARR_ELEMTYPE(arr);
    get_typlenbyvalalign(elemtype, &typlen, &typbyval, &typalign);
    deconstruct_array(arr, elemtype, typlen, typbyval, typalign,
                      &elems, &nulls, (int *) &npts);
    if (npts < 2)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("geometry4d_makeline: need >= 2 points")));
    for (int i = 0; i < npts; i++)
        if (nulls[i])
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("geometry4d_makeline: null points not allowed")));
    plen = sizeof(uint32) + (size_t) npts * 4 * sizeof(double);
    g = g4d_new(G4D_TAG_LINESTRING, plen);
    cur = G4D_PAYLOAD(g);
    {
        uint32 n32 = (uint32) npts;
        memcpy(cur, &n32, sizeof(uint32));
        cur += sizeof(uint32);
    }
    for (int i = 0; i < npts; i++)
    {
        Point4D *p = DatumGetPoint4DP(elems[i]);
        memcpy(cur, p->x, 4 * sizeof(double));
        cur += 4 * sizeof(double);
    }
    PG_RETURN_GEOMETRY4D_P(g);
}
