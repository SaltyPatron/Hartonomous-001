/*
 * pg_glicko_bulk.c — set-at-a-time Glicko-2 update wrapper.
 *
 * SQL signature:
 *   glicko2_bulk_update(
 *       mu        double precision[],
 *       sigma     double precision[],
 *       vol       double precision[],
 *       opp_mu    double precision[],
 *       opp_sigma double precision[],
 *       score     double precision[]
 *   ) RETURNS TABLE (new_mu double precision[],
 *                    new_sigma double precision[],
 *                    new_vol double precision[])
 *
 * All input arrays must have the same length n. Returns three arrays
 * each of length n. Backed by hartonomous_glicko2_bulk_update.
 */
#include "postgres.h"
#include "fmgr.h"
#include "varatt.h"
#include "funcapi.h"
#include "catalog/pg_type.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/lsyscache.h"

#include "hartonomous.h"
#include "hartonomous_pg.h"

PG_FUNCTION_INFO_V1(pg_glicko2_bulk_update);

static void
extract_float8_array(ArrayType *arr, double **out, int *n)
{
    Datum  *elems;
    bool   *nulls;
    int     count;
    int     i;

    if (ARR_NDIM(arr) != 1)
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("glicko2_bulk_update: arrays must be 1-D")));
    if (ARR_ELEMTYPE(arr) != FLOAT8OID)
        ereport(ERROR,
                (errcode(ERRCODE_DATATYPE_MISMATCH),
                 errmsg("glicko2_bulk_update: arrays must be float8[]")));

    deconstruct_array(arr, FLOAT8OID, sizeof(double), FLOAT8PASSBYVAL, 'd',
                      &elems, &nulls, &count);
    *out = (double *) palloc(count * sizeof(double));
    for (i = 0; i < count; i++)
    {
        if (nulls[i])
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("glicko2_bulk_update: array elements must be non-null")));
        (*out)[i] = DatumGetFloat8(elems[i]);
    }
    *n = count;
}

static ArrayType *
double_array_to_array(const double *vals, int n)
{
    Datum *d = palloc(n * sizeof(Datum));
    int    i;
    for (i = 0; i < n; i++)
        d[i] = Float8GetDatum(vals[i]);
    return construct_array(d, n, FLOAT8OID, sizeof(double),
                           FLOAT8PASSBYVAL, 'd');
}

Datum
pg_glicko2_bulk_update(PG_FUNCTION_ARGS)
{
    ArrayType *a_mu     = PG_GETARG_ARRAYTYPE_P(0);
    ArrayType *a_sigma  = PG_GETARG_ARRAYTYPE_P(1);
    ArrayType *a_vol    = PG_GETARG_ARRAYTYPE_P(2);
    ArrayType *a_omu    = PG_GETARG_ARRAYTYPE_P(3);
    ArrayType *a_osigma = PG_GETARG_ARRAYTYPE_P(4);
    ArrayType *a_score  = PG_GETARG_ARRAYTYPE_P(5);

    double *mu, *sigma, *vol, *omu, *osigma, *score;
    int     n_mu, n_sigma, n_vol, n_omu, n_osigma, n_score;
    double *new_mu, *new_sigma, *new_vol;
    int     rc;

    TupleDesc tupdesc;
    Datum     values[3];
    bool      nulls[3] = {false, false, false};
    HeapTuple tuple;

    extract_float8_array(a_mu,     &mu,     &n_mu);
    extract_float8_array(a_sigma,  &sigma,  &n_sigma);
    extract_float8_array(a_vol,    &vol,    &n_vol);
    extract_float8_array(a_omu,    &omu,    &n_omu);
    extract_float8_array(a_osigma, &osigma, &n_osigma);
    extract_float8_array(a_score,  &score,  &n_score);

    if (!(n_mu == n_sigma && n_mu == n_vol && n_mu == n_omu &&
          n_mu == n_osigma && n_mu == n_score))
        ereport(ERROR,
                (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR),
                 errmsg("glicko2_bulk_update: all arrays must have same length"),
                 errdetail("Got mu=%d sigma=%d vol=%d opp_mu=%d opp_sigma=%d score=%d",
                           n_mu, n_sigma, n_vol, n_omu, n_osigma, n_score)));

    new_mu    = (double *) palloc(n_mu * sizeof(double));
    new_sigma = (double *) palloc(n_mu * sizeof(double));
    new_vol   = (double *) palloc(n_mu * sizeof(double));

    rc = hartonomous_glicko2_bulk_update((int64_t) n_mu, mu, sigma, vol,
                                         omu, osigma, score,
                                         new_mu, new_sigma, new_vol);
    if (rc != 0)
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("glicko2_bulk_update native call failed: rc=%d", rc)));

    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE)
        ereport(ERROR,
                (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                 errmsg("glicko2_bulk_update: function returning record called in context that cannot accept type record")));
    BlessTupleDesc(tupdesc);

    values[0] = PointerGetDatum(double_array_to_array(new_mu,    n_mu));
    values[1] = PointerGetDatum(double_array_to_array(new_sigma, n_mu));
    values[2] = PointerGetDatum(double_array_to_array(new_vol,   n_mu));
    tuple = heap_form_tuple(tupdesc, values, nulls);
    PG_RETURN_DATUM(HeapTupleGetDatum(tuple));
}
