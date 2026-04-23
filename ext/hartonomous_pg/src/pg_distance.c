/*
 * pg_distance.c — distance and S³ helper SQL wrappers.
 *
 * Each function wraps a libhartonomous primitive. No PostGIS bridging.
 * Operators <-> (Euclidean) and <=> (S³ geodesic) bind to distance_4d
 * and distance_s3 respectively (see hartonomous--1.0.sql).
 */
#include "postgres.h"
#include "fmgr.h"

#include "hartonomous.h"
#include "hartonomous_pg.h"

PG_FUNCTION_INFO_V1(pg_distance_4d);
PG_FUNCTION_INFO_V1(pg_distance_s3);
PG_FUNCTION_INFO_V1(pg_dot_4d);
PG_FUNCTION_INFO_V1(pg_norm_4d);
PG_FUNCTION_INFO_V1(pg_normalize_4d);
PG_FUNCTION_INFO_V1(pg_slerp);
PG_FUNCTION_INFO_V1(pg_antipode);

Datum
pg_distance_4d(PG_FUNCTION_ARGS)
{
    Point4D *a = PG_GETARG_POINT4D_P(0);
    Point4D *b = PG_GETARG_POINT4D_P(1);
    PG_RETURN_FLOAT8(hartonomous_distance_4d(a->x, b->x));
}

Datum
pg_distance_s3(PG_FUNCTION_ARGS)
{
    Point4D *a = PG_GETARG_POINT4D_P(0);
    Point4D *b = PG_GETARG_POINT4D_P(1);
    PG_RETURN_FLOAT8(hartonomous_s3_distance(a->x, b->x));
}

Datum
pg_dot_4d(PG_FUNCTION_ARGS)
{
    Point4D *a = PG_GETARG_POINT4D_P(0);
    Point4D *b = PG_GETARG_POINT4D_P(1);
    PG_RETURN_FLOAT8(hartonomous_dot_4d(a->x, b->x));
}

Datum
pg_norm_4d(PG_FUNCTION_ARGS)
{
    Point4D *p = PG_GETARG_POINT4D_P(0);
    PG_RETURN_FLOAT8(hartonomous_norm_4d(p->x));
}

Datum
pg_normalize_4d(PG_FUNCTION_ARGS)
{
    Point4D *p = PG_GETARG_POINT4D_P(0);
    Point4D *out = point4d_alloc();
    int rc = hartonomous_normalize_4d(p->x, out->x);
    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("normalize_4d failed: zero-norm input")));
    PG_RETURN_POINT4D_P(out);
}

Datum
pg_slerp(PG_FUNCTION_ARGS)
{
    Point4D *a = PG_GETARG_POINT4D_P(0);
    Point4D *b = PG_GETARG_POINT4D_P(1);
    double   t = PG_GETARG_FLOAT8(2);
    Point4D *out = point4d_alloc();
    int rc = hartonomous_slerp(a->x, b->x, t, out->x);
    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("slerp failed (code %d): inputs must be unit-length on S^3", rc)));
    PG_RETURN_POINT4D_P(out);
}

Datum
pg_antipode(PG_FUNCTION_ARGS)
{
    Point4D *p = PG_GETARG_POINT4D_P(0);
    Point4D *out = point4d_alloc();
    (void) hartonomous_antipode(p->x, out->x);
    PG_RETURN_POINT4D_P(out);
}
