/*
 * pg_scalar.c — Hilbert and Super-Fibonacci wrappers returning point4d.
 *
 * Replaces the float8[] / geometry-bridged versions in the prior
 * pg_geometry.c. Inputs are scalar bigint indices (and order); outputs are
 * the first-class point4d type.
 */
#include "postgres.h"
#include "fmgr.h"

#include "hartonomous.h"
#include "hartonomous_pg.h"

PG_FUNCTION_INFO_V1(pg_super_fibonacci_4d);
PG_FUNCTION_INFO_V1(pg_hilbert_4d);
PG_FUNCTION_INFO_V1(pg_hilbert_4d_inverse);

Datum
pg_super_fibonacci_4d(PG_FUNCTION_ARGS)
{
    int64    i = PG_GETARG_INT64(0);
    int64    n = PG_GETARG_INT64(1);
    double   params[2];
    Point4D *out;
    int      rc;

    if (n <= 0)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("super_fibonacci_4d: n must be positive, got %lld",
                        (long long) n)));
    if (i < 0 || i >= n)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("super_fibonacci_4d: i must be in [0, n), got i=%lld n=%lld",
                        (long long) i, (long long) n)));

    params[0] = (double) i;
    params[1] = (double) n;
    out = point4d_alloc();
    rc = hartonomous_super_fibonacci(params, 2, out->x);
    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("super_fibonacci_4d failed (code %d)", rc)));
    PG_RETURN_POINT4D_P(out);
}

Datum
pg_hilbert_4d(PG_FUNCTION_ARGS)
{
    Point4D *p = PG_GETARG_POINT4D_P(0);
    int32    order = PG_GETARG_INT32(1);
    uint64   idx;

    if (order < 1 || order > 16)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("hilbert_4d order must be 1..16, got %d", order)));

    idx = hartonomous_hilbert_index(p->x, order);
    PG_RETURN_INT64((int64) idx);
}

Datum
pg_hilbert_4d_inverse(PG_FUNCTION_ARGS)
{
    int64    idx = PG_GETARG_INT64(0);
    int32    order = PG_GETARG_INT32(1);
    Point4D *out;
    int      rc;

    if (order < 1 || order > 16)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("hilbert_4d_inverse order must be 1..16, got %d", order)));

    out = point4d_alloc();
    rc = hartonomous_hilbert_inverse((uint64) idx, order, out->x);
    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("hilbert_4d_inverse failed (code %d)", rc)));
    PG_RETURN_POINT4D_P(out);
}
