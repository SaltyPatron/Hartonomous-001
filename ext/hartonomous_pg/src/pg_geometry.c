#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/lsyscache.h"
#include "catalog/pg_type.h"

#include "hartonomous.h"

PG_FUNCTION_INFO_V1(pg_s3_distance);
PG_FUNCTION_INFO_V1(pg_s3_centroid);
PG_FUNCTION_INFO_V1(pg_super_fibonacci_project);
PG_FUNCTION_INFO_V1(pg_hilbert_index);

static int
extract_float8_array(ArrayType *arr, double *out, int max_elems)
{
    Datum *elems;
    bool *nulls;
    int nelems;
    int i;

    deconstruct_array(arr, FLOAT8OID, 8, FLOAT8PASSBYVAL, 'd',
                      &elems, &nulls, &nelems);

    if (nelems > max_elems)
        nelems = max_elems;

    for (i = 0; i < nelems; i++)
    {
        if (nulls[i])
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("array element %d must not be NULL", i + 1)));
        out[i] = DatumGetFloat8(elems[i]);
    }

    return nelems;
}

Datum
pg_s3_distance(PG_FUNCTION_ARGS)
{
    ArrayType *arr1 = PG_GETARG_ARRAYTYPE_P(0);
    ArrayType *arr2 = PG_GETARG_ARRAYTYPE_P(1);
    double p1[4], p2[4];
    int n1, n2;
    double dist;

    n1 = extract_float8_array(arr1, p1, 4);
    n2 = extract_float8_array(arr2, p2, 4);

    if (n1 != 4 || n2 != 4)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_ELEMENT_ERROR),
                 errmsg("s3_distance requires two 4-element arrays, got %d and %d", n1, n2)));

    dist = hartonomous_s3_distance(p1, p2);
    PG_RETURN_FLOAT8(dist);
}

Datum
pg_s3_centroid(PG_FUNCTION_ARGS)
{
    ArrayType *arr = PG_GETARG_ARRAYTYPE_P(0);
    Datum *elems;
    bool *nulls;
    int nelems;
    int i;
    double *points;
    double out[4];
    int rc;
    Datum result_elems[4];
    ArrayType *result;

    deconstruct_array(arr, FLOAT8ARRAYOID, -1, false, 'i',
                      &elems, &nulls, &nelems);

    if (nelems == 0)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_ELEMENT_ERROR),
                 errmsg("s3_centroid requires at least one point")));

    points = palloc(sizeof(double) * 4 * nelems);

    for (i = 0; i < nelems; i++)
    {
        ArrayType *point_arr;
        double coords[4];
        int ncoords;

        if (nulls[i])
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("point array element %d must not be NULL", i + 1)));

        point_arr = DatumGetArrayTypeP(elems[i]);
        ncoords = extract_float8_array(point_arr, coords, 4);

        if (ncoords != 4)
            ereport(ERROR,
                    (errcode(ERRCODE_ARRAY_ELEMENT_ERROR),
                     errmsg("each point must have 4 coordinates, point %d has %d", i + 1, ncoords)));

        memcpy(&points[i * 4], coords, sizeof(double) * 4);
    }

    rc = hartonomous_s3_centroid(points, nelems, out);
    pfree(points);

    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("s3_centroid failed: antipodal cancellation or invalid input (code %d)", rc)));

    for (i = 0; i < 4; i++)
        result_elems[i] = Float8GetDatum(out[i]);

    result = construct_array(result_elems, 4, FLOAT8OID, 8, FLOAT8PASSBYVAL, 'd');
    PG_RETURN_ARRAYTYPE_P(result);
}

Datum
pg_super_fibonacci_project(PG_FUNCTION_ARGS)
{
    ArrayType *arr = PG_GETARG_ARRAYTYPE_P(0);
    double params[16];
    double out[4];
    int nparams;
    int rc;
    int i;
    Datum result_elems[4];
    ArrayType *result;

    nparams = extract_float8_array(arr, params, 16);

    if (nparams < 2)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_ELEMENT_ERROR),
                 errmsg("super_fibonacci_project requires at least 2 parameters (index, N), got %d", nparams)));

    rc = hartonomous_super_fibonacci(params, nparams, out);

    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("super_fibonacci_project failed (code %d)", rc)));

    for (i = 0; i < 4; i++)
        result_elems[i] = Float8GetDatum(out[i]);

    result = construct_array(result_elems, 4, FLOAT8OID, 8, FLOAT8PASSBYVAL, 'd');
    PG_RETURN_ARRAYTYPE_P(result);
}

Datum
pg_hilbert_index(PG_FUNCTION_ARGS)
{
    ArrayType *arr = PG_GETARG_ARRAYTYPE_P(0);
    int32 order = PG_GETARG_INT32(1);
    double point[4];
    int ncoords;
    uint64 idx;

    ncoords = extract_float8_array(arr, point, 4);

    if (ncoords != 4)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_ELEMENT_ERROR),
                 errmsg("hilbert_index requires a 4-element array, got %d", ncoords)));

    if (order < 1 || order > 16)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("hilbert order must be 1..16, got %d", order)));

    idx = hartonomous_hilbert_index(point, order);
    PG_RETURN_INT64((int64)idx);
}
