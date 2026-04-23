/*
 * pg_linestring4d.c — SQL type wrapper for 4D polylines.
 *
 * Storage: varlena (vl_len_, npoints, points[]).
 *
 * I/O text format: "((x1,y1,z1,w1),(x2,y2,z2,w2),...)"
 * I/O binary:      int32 npoints, then 4*npoints float8 (network byte order).
 *
 * Functions: npoints, point_n, bbox (MBR), append_point, length_4d.
 */
#include "postgres.h"
#include "fmgr.h"
#include "varatt.h"
#include "catalog/pg_type.h"
#include "libpq/pqformat.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include <math.h>

#include "hartonomous.h"
#include "hartonomous_pg.h"

PG_FUNCTION_INFO_V1(pg_linestring4d_in);
PG_FUNCTION_INFO_V1(pg_linestring4d_out);
PG_FUNCTION_INFO_V1(pg_linestring4d_recv);
PG_FUNCTION_INFO_V1(pg_linestring4d_send);
PG_FUNCTION_INFO_V1(pg_linestring4d_npoints);
PG_FUNCTION_INFO_V1(pg_linestring4d_point_n);
PG_FUNCTION_INFO_V1(pg_linestring4d_bbox);
PG_FUNCTION_INFO_V1(pg_linestring4d_append);
PG_FUNCTION_INFO_V1(pg_linestring4d_length);
PG_FUNCTION_INFO_V1(pg_array_to_linestring4d);
PG_FUNCTION_INFO_V1(pg_bytea_to_linestring4d);

/* parse "(d1,d2,d3,d4)" — used by both array and inline. */
static int
parse_quad(char **cur_io, double out[4])
{
    char *cur = *cur_io;
    while (*cur == ' ' || *cur == '\t') cur++;
    if (*cur != '(') return 0;
    cur++;
    for (int i = 0; i < 4; i++)
    {
        char *endptr;
        double v;
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
pg_linestring4d_in(PG_FUNCTION_ARGS)
{
    char  *str = PG_GETARG_CSTRING(0);
    char  *cur = str;
    int    cap = 8;
    int    n   = 0;
    Point4D *buf = palloc(cap * sizeof(Point4D));
    LineString4D *ls;

    while (*cur == ' ' || *cur == '\t') cur++;
    if (*cur != '(')
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("invalid linestring4d: \"%s\"", str)));
    cur++;

    while (*cur != ')' && *cur != '\0')
    {
        Point4D p;
        while (*cur == ' ' || *cur == '\t' || *cur == ',') cur++;
        if (*cur == ')') break;
        if (!parse_quad(&cur, p.x))
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                     errmsg("linestring4d parse failed near offset %td", cur - str)));
        if (n == cap)
        {
            cap *= 2;
            buf = repalloc(buf, cap * sizeof(Point4D));
        }
        buf[n++] = p;
    }
    if (n == 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("linestring4d must have at least one vertex")));

    ls = (LineString4D *) palloc(LS4D_SIZE(n));
    SET_VARSIZE(ls, LS4D_SIZE(n));
    ls->npoints = n;
    memcpy(ls->points, buf, n * sizeof(Point4D));
    pfree(buf);
    PG_RETURN_LINESTRING4D_P(ls);
}

Datum
pg_linestring4d_out(PG_FUNCTION_ARGS)
{
    LineString4D *ls = PG_GETARG_LINESTRING4D_P(0);
    StringInfoData s;
    int i;

    initStringInfo(&s);
    appendStringInfoChar(&s, '(');
    for (i = 0; i < ls->npoints; i++)
    {
        if (i > 0) appendStringInfoChar(&s, ',');
        appendStringInfo(&s, "(%.17g,%.17g,%.17g,%.17g)",
                         ls->points[i].x[0], ls->points[i].x[1],
                         ls->points[i].x[2], ls->points[i].x[3]);
    }
    appendStringInfoChar(&s, ')');
    PG_RETURN_CSTRING(s.data);
}

Datum
pg_linestring4d_recv(PG_FUNCTION_ARGS)
{
    StringInfo buf = (StringInfo) PG_GETARG_POINTER(0);
    int n = pq_getmsgint(buf, 4);
    LineString4D *ls;
    int i;

    if (n <= 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION),
                 errmsg("linestring4d binary: npoints must be positive")));
    ls = (LineString4D *) palloc(LS4D_SIZE(n));
    SET_VARSIZE(ls, LS4D_SIZE(n));
    ls->npoints = n;
    for (i = 0; i < n; i++)
    {
        ls->points[i].x[0] = pq_getmsgfloat8(buf);
        ls->points[i].x[1] = pq_getmsgfloat8(buf);
        ls->points[i].x[2] = pq_getmsgfloat8(buf);
        ls->points[i].x[3] = pq_getmsgfloat8(buf);
    }
    PG_RETURN_LINESTRING4D_P(ls);
}

Datum
pg_linestring4d_send(PG_FUNCTION_ARGS)
{
    LineString4D *ls = PG_GETARG_LINESTRING4D_P(0);
    StringInfoData buf;
    int i;
    pq_begintypsend(&buf);
    pq_sendint32(&buf, ls->npoints);
    for (i = 0; i < ls->npoints; i++)
    {
        pq_sendfloat8(&buf, ls->points[i].x[0]);
        pq_sendfloat8(&buf, ls->points[i].x[1]);
        pq_sendfloat8(&buf, ls->points[i].x[2]);
        pq_sendfloat8(&buf, ls->points[i].x[3]);
    }
    PG_RETURN_BYTEA_P(pq_endtypsend(&buf));
}

Datum
pg_linestring4d_npoints(PG_FUNCTION_ARGS)
{
    LineString4D *ls = PG_GETARG_LINESTRING4D_P(0);
    PG_RETURN_INT32(ls->npoints);
}

Datum
pg_linestring4d_point_n(PG_FUNCTION_ARGS)
{
    LineString4D *ls = PG_GETARG_LINESTRING4D_P(0);
    int idx = PG_GETARG_INT32(1);
    Point4D *out;

    if (idx < 1 || idx > ls->npoints)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("point_n: index %d out of range [1, %d]", idx, ls->npoints)));
    out = point4d_alloc();
    *out = ls->points[idx - 1];
    PG_RETURN_POINT4D_P(out);
}

Datum
pg_linestring4d_bbox(PG_FUNCTION_ARGS)
{
    LineString4D *ls = PG_GETARG_LINESTRING4D_P(0);
    Box4D *out;
    int i;

    out = box4d_alloc();
    hartonomous_bbox_init_point(ls->points[0].x, (double *) out);
    for (i = 1; i < ls->npoints; i++)
        hartonomous_bbox_expand_point((double *) out, ls->points[i].x);
    PG_RETURN_BOX4D_P(out);
}

Datum
pg_linestring4d_append(PG_FUNCTION_ARGS)
{
    LineString4D *ls = PG_GETARG_LINESTRING4D_P(0);
    Point4D *p = PG_GETARG_POINT4D_P(1);
    int n = ls->npoints + 1;
    LineString4D *out;

    out = (LineString4D *) palloc(LS4D_SIZE(n));
    SET_VARSIZE(out, LS4D_SIZE(n));
    out->npoints = n;
    memcpy(out->points, ls->points, ls->npoints * sizeof(Point4D));
    out->points[ls->npoints] = *p;
    PG_RETURN_LINESTRING4D_P(out);
}

Datum
pg_linestring4d_length(PG_FUNCTION_ARGS)
{
    LineString4D *ls = PG_GETARG_LINESTRING4D_P(0);
    double total = 0.0;
    int i;
    for (i = 1; i < ls->npoints; i++)
        total += hartonomous_distance_4d(ls->points[i - 1].x, ls->points[i].x);
    PG_RETURN_FLOAT8(total);
}

/*
 * pg_array_to_linestring4d — bulk constructor.
 *
 * Input:  flat 1-D float8[] of length 4n, n >= 1; each consecutive group
 *         of 4 elements is one (x1, x2, x3, x4) vertex in vertex order.
 * Output: linestring4d with n vertices.
 *
 * This is the canonical batch-insert path used by the C# ingestion pipeline
 * (NpgsqlIngestionPipeline.WritePhysicalitiesAsync). C# allocates one float8[]
 * per linestring, passes them as a bytea[]/float8[][] is impossible because
 * inner-array length varies; passing each row's flat array separately and
 * applying this function row-wise inside one INSERT keeps the write set-based.
 */
Datum
pg_array_to_linestring4d(PG_FUNCTION_ARGS)
{
    ArrayType *arr = PG_GETARG_ARRAYTYPE_P(0);
    Datum     *elems;
    bool      *nulls;
    int        n_elems;
    int        n_pts;
    LineString4D *out;
    int        i;

    if (ARR_NDIM(arr) != 1)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("array→linestring4d: input must be 1-D float8[]")));
    if (ARR_ELEMTYPE(arr) != FLOAT8OID)
        ereport(ERROR,
                (errcode(ERRCODE_DATATYPE_MISMATCH),
                 errmsg("array→linestring4d: input must be float8[]")));

    deconstruct_array(arr, FLOAT8OID, sizeof(double), FLOAT8PASSBYVAL, 'd',
                      &elems, &nulls, &n_elems);

    if (n_elems < 4 || (n_elems % 4) != 0)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("array→linestring4d: length %d is not a positive multiple of 4",
                        n_elems)));

    n_pts = n_elems / 4;
    out = (LineString4D *) palloc(LS4D_SIZE(n_pts));
    SET_VARSIZE(out, LS4D_SIZE(n_pts));
    out->npoints = n_pts;

    for (i = 0; i < n_elems; i++)
    {
        if (nulls[i])
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("array→linestring4d: element %d is NULL", i)));
        out->points[i / 4].x[i % 4] = DatumGetFloat8(elems[i]);
    }
    PG_RETURN_LINESTRING4D_P(out);
}

/*
 * pg_bytea_to_linestring4d — per-row binary constructor.
 *
 * Input:  bytea holding the linestring4d wire format:
 *           int32 npoints (network byte order, > 0)
 *           4 * npoints float8 values (network byte order),
 *           one (x1, x2, x3, x4) tuple per vertex in vertex order.
 * Output: linestring4d.
 *
 * Why this exists. The C# ingestion pipeline writes physicality.ls4d rows in
 * batches via INSERT ... SELECT FROM unnest(parallel_arrays). Postgres can
 * unnest float8[] fine, but it cannot unnest float8[][] row-wise — multidim
 * arrays flatten — and rows have variable vertex counts so a single uniform
 * 2-D float8[][] is impossible.  Passing one bytea per row solves both:
 *   - bytea[] is a 1-D array; unnest yields one bytea per row, no flattening.
 *   - Each bytea is self-describing (npoints prefix), so vertex counts may
 *     differ row-to-row in the same INSERT.
 * The wire format matches pg_linestring4d_recv exactly so the same encoder/
 * decoder code path is exercised by COPY BINARY tests.
 */
Datum
pg_bytea_to_linestring4d(PG_FUNCTION_ARGS)
{
    bytea       *raw = PG_GETARG_BYTEA_PP(0);
    StringInfoData buf;
    int          n;
    LineString4D *out;
    int          i;

    buf.data   = VARDATA_ANY(raw);
    buf.len    = VARSIZE_ANY_EXHDR(raw);
    buf.maxlen = buf.len;
    buf.cursor = 0;

    if (buf.len < 4)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION),
                 errmsg("bytea→linestring4d: payload shorter than npoints header")));

    n = pq_getmsgint(&buf, 4);
    if (n <= 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION),
                 errmsg("bytea→linestring4d: npoints must be positive (got %d)", n)));

    if (buf.len - buf.cursor != (Size) n * 4 * sizeof(double))
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION),
                 errmsg("bytea→linestring4d: payload length %d does not match npoints %d (expected %lld trailing bytes)",
                        buf.len, n, (long long)((Size) n * 4 * sizeof(double)))));

    out = (LineString4D *) palloc(LS4D_SIZE(n));
    SET_VARSIZE(out, LS4D_SIZE(n));
    out->npoints = n;
    for (i = 0; i < n; i++)
    {
        out->points[i].x[0] = pq_getmsgfloat8(&buf);
        out->points[i].x[1] = pq_getmsgfloat8(&buf);
        out->points[i].x[2] = pq_getmsgfloat8(&buf);
        out->points[i].x[3] = pq_getmsgfloat8(&buf);
    }
    PG_RETURN_LINESTRING4D_P(out);
}
