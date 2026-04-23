/*
 * pg_point4d.c — SQL type wrapper for the 4D point.
 *
 * I/O contract:
 *   text  : "(x1, x2, x3, x4)" — four float8s, comma-separated, parens required.
 *   binary: 4 × network-byte-order float8.
 *
 * On-disk layout matches `Point4D` in hartonomous_pg.h: 32 bytes, alignment
 * double, plain storage. Pass-by-reference at the SQL boundary.
 *
 * Equality is exact (bit-pattern). Callers that want approximate equality
 * use `distance_4d(a, b) < eps` directly.
 */
#include "postgres.h"
#include "fmgr.h"
#include "libpq/pqformat.h"
#include "utils/builtins.h"
#include "common/hashfn.h"

#include "hartonomous_pg.h"

PG_FUNCTION_INFO_V1(pg_point4d_in);
PG_FUNCTION_INFO_V1(pg_point4d_out);
PG_FUNCTION_INFO_V1(pg_point4d_recv);
PG_FUNCTION_INFO_V1(pg_point4d_send);
PG_FUNCTION_INFO_V1(pg_point4d_eq);
PG_FUNCTION_INFO_V1(pg_point4d_ne);
PG_FUNCTION_INFO_V1(pg_point4d_hash);
PG_FUNCTION_INFO_V1(pg_point4d_constructor);

Datum
pg_point4d_in(PG_FUNCTION_ARGS)
{
    char   *str = PG_GETARG_CSTRING(0);
    char   *cur = str;
    Point4D *p = point4d_alloc();

    /* Skip leading whitespace, then require '('. */
    while (*cur == ' ' || *cur == '\t') cur++;
    if (*cur != '(')
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("invalid input syntax for type point4d: \"%s\"", str),
                 errhint("Expected leading '('.")));
    cur++;

    for (int i = 0; i < 4; i++)
    {
        char   *endptr;
        double  v;

        while (*cur == ' ' || *cur == '\t') cur++;
        v = strtod(cur, &endptr);
        if (endptr == cur)
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                     errmsg("invalid input syntax for type point4d: \"%s\"", str),
                     errhint("Could not parse coordinate %d.", i + 1)));
        p->x[i] = v;
        cur = endptr;
        while (*cur == ' ' || *cur == '\t') cur++;

        if (i < 3)
        {
            if (*cur != ',')
                ereport(ERROR,
                        (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                         errmsg("invalid input syntax for type point4d: \"%s\"", str),
                         errhint("Expected ',' after coordinate %d.", i + 1)));
            cur++;
        }
    }

    if (*cur != ')')
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                 errmsg("invalid input syntax for type point4d: \"%s\"", str),
                 errhint("Expected closing ')'.")));

    PG_RETURN_POINT4D_P(p);
}

Datum
pg_point4d_out(PG_FUNCTION_ARGS)
{
    Point4D *p = PG_GETARG_POINT4D_P(0);
    char    buf[256];

    snprintf(buf, sizeof(buf), "(%.17g, %.17g, %.17g, %.17g)",
             p->x[0], p->x[1], p->x[2], p->x[3]);
    PG_RETURN_CSTRING(pstrdup(buf));
}

Datum
pg_point4d_recv(PG_FUNCTION_ARGS)
{
    StringInfo  buf = (StringInfo) PG_GETARG_POINTER(0);
    Point4D    *p = point4d_alloc();

    for (int i = 0; i < 4; i++)
        p->x[i] = pq_getmsgfloat8(buf);
    PG_RETURN_POINT4D_P(p);
}

Datum
pg_point4d_send(PG_FUNCTION_ARGS)
{
    Point4D    *p = PG_GETARG_POINT4D_P(0);
    StringInfoData buf;

    pq_begintypsend(&buf);
    for (int i = 0; i < 4; i++)
        pq_sendfloat8(&buf, p->x[i]);
    PG_RETURN_BYTEA_P(pq_endtypsend(&buf));
}

Datum
pg_point4d_eq(PG_FUNCTION_ARGS)
{
    Point4D *a = PG_GETARG_POINT4D_P(0);
    Point4D *b = PG_GETARG_POINT4D_P(1);
    PG_RETURN_BOOL(a->x[0] == b->x[0] && a->x[1] == b->x[1]
                && a->x[2] == b->x[2] && a->x[3] == b->x[3]);
}

Datum
pg_point4d_ne(PG_FUNCTION_ARGS)
{
    Point4D *a = PG_GETARG_POINT4D_P(0);
    Point4D *b = PG_GETARG_POINT4D_P(1);
    PG_RETURN_BOOL(!(a->x[0] == b->x[0] && a->x[1] == b->x[1]
                  && a->x[2] == b->x[2] && a->x[3] == b->x[3]));
}

/* Hash function for the HASHES property of `=`. We hash the raw 32-byte
 * IEEE-754 representation; this is consistent with bit-exact equality. */
Datum
pg_point4d_hash(PG_FUNCTION_ARGS)
{
    Point4D *p = PG_GETARG_POINT4D_P(0);
    PG_RETURN_UINT32(hash_bytes((const unsigned char *) p->x, sizeof(p->x)));
}

/* Convenience constructor: point4d(x1, x2, x3, x4). */
Datum
pg_point4d_constructor(PG_FUNCTION_ARGS)
{
    Point4D *p = point4d_alloc();
    p->x[0] = PG_GETARG_FLOAT8(0);
    p->x[1] = PG_GETARG_FLOAT8(1);
    p->x[2] = PG_GETARG_FLOAT8(2);
    p->x[3] = PG_GETARG_FLOAT8(3);
    PG_RETURN_POINT4D_P(p);
}
