/*
 * pg_box4d.c — SQL type wrapper for axis-aligned 4D bounding box.
 *
 * I/O contract:
 *   text  : "((x1lo, x2lo, x3lo, x4lo), (x1hi, x2hi, x3hi, x4hi))"
 *   binary: 8 × network-byte-order float8 (min[4] then max[4]).
 *
 * Used as the GiST key type for `point4d` columns. Operators &&, @>, <@
 * dispatch to the libhartonomous bbox helpers.
 */
#include "postgres.h"
#include "fmgr.h"
#include "libpq/pqformat.h"
#include "utils/builtins.h"

#include "hartonomous.h"
#include "hartonomous_pg.h"

PG_FUNCTION_INFO_V1(pg_box4d_in);
PG_FUNCTION_INFO_V1(pg_box4d_out);
PG_FUNCTION_INFO_V1(pg_box4d_recv);
PG_FUNCTION_INFO_V1(pg_box4d_send);
PG_FUNCTION_INFO_V1(pg_box4d_overlaps);
PG_FUNCTION_INFO_V1(pg_box4d_contains_point);
PG_FUNCTION_INFO_V1(pg_point_contained_by_box4d);
PG_FUNCTION_INFO_V1(pg_box4d_contains_box);
PG_FUNCTION_INFO_V1(pg_box4d_contained_by_box);
PG_FUNCTION_INFO_V1(pg_box4d_eq);
PG_FUNCTION_INFO_V1(pg_box4d_union);
PG_FUNCTION_INFO_V1(pg_box4d_expand_point);
PG_FUNCTION_INFO_V1(pg_bbox_from_point);

static int
parse_quad(char **cur_io, double out[4])
{
    char *cur = *cur_io;
    while (*cur == ' ' || *cur == '\t') cur++;
    if (*cur != '(') return 0;
    cur++;
    for (int i = 0; i < 4; i++)
    {
        char   *endptr;
        double  v;
        while (*cur == ' ' || *cur == '\t') cur++;
        v = strtod(cur, &endptr);
        if (endptr == cur) return 0;
        out[i] = v;
        cur = endptr;
        while (*cur == ' ' || *cur == '\t') cur++;
        if (i < 3)
        {
            if (*cur != ',') return 0;
            cur++;
        }
    }
    if (*cur != ')') return 0;
    cur++;
    *cur_io = cur;
    return 1;
}

Datum
pg_box4d_in(PG_FUNCTION_ARGS)
{
    char   *str = PG_GETARG_CSTRING(0);
    char   *cur = str;
    Box4D  *box = box4d_alloc();

    while (*cur == ' ' || *cur == '\t') cur++;
    if (*cur != '(')
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("invalid input syntax for type box4d: \"%s\"", str)));
    cur++;

    if (!parse_quad(&cur, box->min))
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("invalid input syntax for type box4d: \"%s\"", str),
                 errhint("Failed to parse min point.")));
    while (*cur == ' ' || *cur == '\t') cur++;
    if (*cur != ',')
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("invalid input syntax for type box4d: \"%s\"", str),
                 errhint("Expected ',' between min and max.")));
    cur++;
    if (!parse_quad(&cur, box->max))
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("invalid input syntax for type box4d: \"%s\"", str),
                 errhint("Failed to parse max point.")));
    while (*cur == ' ' || *cur == '\t') cur++;
    if (*cur != ')')
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("invalid input syntax for type box4d: \"%s\"", str)));

    /* Validate min ≤ max axis-by-axis. */
    for (int i = 0; i < 4; i++)
        if (box->min[i] > box->max[i])
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                     errmsg("box4d min[%d]=%g exceeds max[%d]=%g",
                            i, box->min[i], i, box->max[i])));

    PG_RETURN_BOX4D_P(box);
}

Datum
pg_box4d_out(PG_FUNCTION_ARGS)
{
    Box4D *b = PG_GETARG_BOX4D_P(0);
    char   buf[512];
    snprintf(buf, sizeof(buf),
             "((%.17g, %.17g, %.17g, %.17g), (%.17g, %.17g, %.17g, %.17g))",
             b->min[0], b->min[1], b->min[2], b->min[3],
             b->max[0], b->max[1], b->max[2], b->max[3]);
    PG_RETURN_CSTRING(pstrdup(buf));
}

Datum
pg_box4d_recv(PG_FUNCTION_ARGS)
{
    StringInfo  buf = (StringInfo) PG_GETARG_POINTER(0);
    Box4D      *b = box4d_alloc();
    for (int i = 0; i < 4; i++) b->min[i] = pq_getmsgfloat8(buf);
    for (int i = 0; i < 4; i++) b->max[i] = pq_getmsgfloat8(buf);
    PG_RETURN_BOX4D_P(b);
}

Datum
pg_box4d_send(PG_FUNCTION_ARGS)
{
    Box4D          *b = PG_GETARG_BOX4D_P(0);
    StringInfoData  buf;
    pq_begintypsend(&buf);
    for (int i = 0; i < 4; i++) pq_sendfloat8(&buf, b->min[i]);
    for (int i = 0; i < 4; i++) pq_sendfloat8(&buf, b->max[i]);
    PG_RETURN_BYTEA_P(pq_endtypsend(&buf));
}

Datum
pg_box4d_overlaps(PG_FUNCTION_ARGS)
{
    Box4D *a = PG_GETARG_BOX4D_P(0);
    Box4D *b = PG_GETARG_BOX4D_P(1);
    /* Reuse libhartonomous predicate; a/b layout is contiguous min[4]+max[4]. */
    PG_RETURN_BOOL(hartonomous_bbox_overlaps((const double *) a, (const double *) b) != 0);
}

Datum
pg_box4d_contains_point(PG_FUNCTION_ARGS)
{
    Box4D   *box = PG_GETARG_BOX4D_P(0);
    Point4D *p = PG_GETARG_POINT4D_P(1);
    PG_RETURN_BOOL(hartonomous_bbox_contains_point((const double *) box, p->x) != 0);
}

Datum
pg_point_contained_by_box4d(PG_FUNCTION_ARGS)
{
    Point4D *p = PG_GETARG_POINT4D_P(0);
    Box4D   *box = PG_GETARG_BOX4D_P(1);
    PG_RETURN_BOOL(hartonomous_bbox_contains_point((const double *) box, p->x) != 0);
}

Datum
pg_box4d_contains_box(PG_FUNCTION_ARGS)
{
    Box4D *outer = PG_GETARG_BOX4D_P(0);
    Box4D *inner = PG_GETARG_BOX4D_P(1);
    PG_RETURN_BOOL(hartonomous_bbox_contains_box((const double *) outer,
                                                 (const double *) inner) != 0);
}

Datum
pg_box4d_contained_by_box(PG_FUNCTION_ARGS)
{
    Box4D *inner = PG_GETARG_BOX4D_P(0);
    Box4D *outer = PG_GETARG_BOX4D_P(1);
    PG_RETURN_BOOL(hartonomous_bbox_contains_box((const double *) outer,
                                                 (const double *) inner) != 0);
}

Datum
pg_box4d_eq(PG_FUNCTION_ARGS)
{
    Box4D *a = PG_GETARG_BOX4D_P(0);
    Box4D *b = PG_GETARG_BOX4D_P(1);
    PG_RETURN_BOOL(hartonomous_bbox_equals((const double *) a, (const double *) b) != 0);
}

Datum
pg_box4d_union(PG_FUNCTION_ARGS)
{
    Box4D *a = PG_GETARG_BOX4D_P(0);
    Box4D *b = PG_GETARG_BOX4D_P(1);
    Box4D *out = box4d_alloc();
    hartonomous_bbox_union((const double *) a, (const double *) b, (double *) out);
    PG_RETURN_BOX4D_P(out);
}

Datum
pg_box4d_expand_point(PG_FUNCTION_ARGS)
{
    Box4D   *src = PG_GETARG_BOX4D_P(0);
    Point4D *p = PG_GETARG_POINT4D_P(1);
    Box4D   *out = box4d_alloc();
    memcpy(out, src, sizeof(Box4D));
    hartonomous_bbox_expand_point((double *) out, p->x);
    PG_RETURN_BOX4D_P(out);
}

Datum
pg_bbox_from_point(PG_FUNCTION_ARGS)
{
    Point4D *p = PG_GETARG_POINT4D_P(0);
    PG_RETURN_BOX4D_P(box4d_from_point(p));
}
