/*
 * pg_casts.c — type bridge functions.
 *
 *   point4d        ↔ double precision[4]
 *   point4d        →  PostGIS geometry (POINTZM) — for compatibility with
 *                     2D/3D physicality rows that already use ST_X/Y/Z/M
 *                     coordinate space. NOT exposed via implicit cast
 *                     (semantic loss is real); only via explicit function.
 */
#include "postgres.h"
#include "fmgr.h"
#include "varatt.h"
#include "catalog/pg_type.h"
#include "utils/array.h"
#include "utils/builtins.h"

#include "hartonomous.h"
#include "hartonomous_pg.h"

PG_FUNCTION_INFO_V1(pg_point4d_to_array);
PG_FUNCTION_INFO_V1(pg_array_to_point4d);

Datum
pg_point4d_to_array(PG_FUNCTION_ARGS)
{
    Point4D *p = PG_GETARG_POINT4D_P(0);
    Datum    d[4];
    ArrayType *out;

    d[0] = Float8GetDatum(p->x[0]);
    d[1] = Float8GetDatum(p->x[1]);
    d[2] = Float8GetDatum(p->x[2]);
    d[3] = Float8GetDatum(p->x[3]);
    out = construct_array(d, 4, FLOAT8OID, sizeof(double),
                          FLOAT8PASSBYVAL, 'd');
    PG_RETURN_ARRAYTYPE_P(out);
}

Datum
pg_array_to_point4d(PG_FUNCTION_ARGS)
{
    ArrayType *arr = PG_GETARG_ARRAYTYPE_P(0);
    Datum     *elems;
    bool      *nulls;
    int        n;
    Point4D   *out;
    int        i;

    if (ARR_NDIM(arr) != 1)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("array→point4d: input must be 1-D")));
    if (ARR_ELEMTYPE(arr) != FLOAT8OID)
        ereport(ERROR,
                (errcode(ERRCODE_DATATYPE_MISMATCH),
                 errmsg("array→point4d: input must be float8[]")));

    deconstruct_array(arr, FLOAT8OID, sizeof(double), FLOAT8PASSBYVAL, 'd',
                      &elems, &nulls, &n);
    if (n != 4)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("array→point4d: array length must be 4 (got %d)", n)));

    out = point4d_alloc();
    for (i = 0; i < 4; i++)
    {
        if (nulls[i])
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("array→point4d: element %d is NULL", i)));
        out->x[i] = DatumGetFloat8(elems[i]);
    }
    PG_RETURN_POINT4D_P(out);
}
