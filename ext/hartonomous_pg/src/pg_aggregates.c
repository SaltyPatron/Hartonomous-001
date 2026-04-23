/*
 * pg_aggregates.c — centroid_4d, centroid_s3, bbox_4d.
 *
 * State for centroid is a palloc'd struct { sum[4], n }. The S³ aggregate
 * shares the SFUNC with centroid_4d; only the FFUNC differs (renormalize
 * vs raw mean). Combine functions exist so PARALLEL SAFE plans work.
 *
 * Memory: state must be allocated in `aggcontext` (the long-lived per-group
 * context), not the short-lived per-tuple context, or it gets freed mid-scan.
 */
#include "postgres.h"
#include "fmgr.h"
#include "varatt.h"
#include "utils/builtins.h"
#include "utils/memutils.h"
#include <math.h>

#include "hartonomous.h"
#include "hartonomous_pg.h"

PG_FUNCTION_INFO_V1(pg_centroid_4d_sfunc);
PG_FUNCTION_INFO_V1(pg_centroid_4d_combine);
PG_FUNCTION_INFO_V1(pg_centroid_4d_ffunc);
PG_FUNCTION_INFO_V1(pg_centroid_s3_ffunc);
PG_FUNCTION_INFO_V1(pg_centroid_4d_serialize);
PG_FUNCTION_INFO_V1(pg_centroid_4d_deserialize);

PG_FUNCTION_INFO_V1(pg_bbox_4d_sfunc);
PG_FUNCTION_INFO_V1(pg_bbox_4d_combine);

typedef struct CentroidState
{
    double sum[4];
    int64  n;
} CentroidState;

static CentroidState *
centroid_state_alloc(MemoryContext aggctx)
{
    MemoryContext old = MemoryContextSwitchTo(aggctx);
    CentroidState *s = (CentroidState *) palloc0(sizeof(CentroidState));
    MemoryContextSwitchTo(old);
    return s;
}

Datum
pg_centroid_4d_sfunc(PG_FUNCTION_ARGS)
{
    CentroidState *state;
    MemoryContext  aggctx;
    Point4D       *p;

    if (!AggCheckCallContext(fcinfo, &aggctx))
        elog(ERROR, "pg_centroid_4d_sfunc: not called in aggregate context");

    if (PG_ARGISNULL(0))
        state = centroid_state_alloc(aggctx);
    else
        state = (CentroidState *) PG_GETARG_POINTER(0);

    if (PG_ARGISNULL(1))
        PG_RETURN_POINTER(state);

    p = PG_GETARG_POINT4D_P(1);
    state->sum[0] += p->x[0];
    state->sum[1] += p->x[1];
    state->sum[2] += p->x[2];
    state->sum[3] += p->x[3];
    state->n += 1;
    PG_RETURN_POINTER(state);
}

Datum
pg_centroid_4d_combine(PG_FUNCTION_ARGS)
{
    CentroidState *a = PG_ARGISNULL(0) ? NULL : (CentroidState *) PG_GETARG_POINTER(0);
    CentroidState *b = PG_ARGISNULL(1) ? NULL : (CentroidState *) PG_GETARG_POINTER(1);
    MemoryContext  aggctx;

    if (!AggCheckCallContext(fcinfo, &aggctx))
        elog(ERROR, "pg_centroid_4d_combine: not called in aggregate context");

    if (a == NULL && b == NULL) PG_RETURN_NULL();
    if (a == NULL)
    {
        CentroidState *out = centroid_state_alloc(aggctx);
        memcpy(out, b, sizeof(CentroidState));
        PG_RETURN_POINTER(out);
    }
    if (b == NULL) PG_RETURN_POINTER(a);

    a->sum[0] += b->sum[0];
    a->sum[1] += b->sum[1];
    a->sum[2] += b->sum[2];
    a->sum[3] += b->sum[3];
    a->n += b->n;
    PG_RETURN_POINTER(a);
}

Datum
pg_centroid_4d_serialize(PG_FUNCTION_ARGS)
{
    CentroidState *s = (CentroidState *) PG_GETARG_POINTER(0);
    /* sizeof(CentroidState) is fixed; emit as bytea. */
    bytea *out = (bytea *) palloc(VARHDRSZ + sizeof(CentroidState));
    SET_VARSIZE(out, VARHDRSZ + sizeof(CentroidState));
    memcpy(VARDATA(out), s, sizeof(CentroidState));
    PG_RETURN_BYTEA_P(out);
}

Datum
pg_centroid_4d_deserialize(PG_FUNCTION_ARGS)
{
    bytea         *in = PG_GETARG_BYTEA_PP(0);
    MemoryContext  aggctx;
    CentroidState *s;

    if (!AggCheckCallContext(fcinfo, &aggctx))
        elog(ERROR, "pg_centroid_4d_deserialize: not called in aggregate context");

    s = centroid_state_alloc(aggctx);
    memcpy(s, VARDATA_ANY(in), sizeof(CentroidState));
    PG_RETURN_POINTER(s);
}

Datum
pg_centroid_4d_ffunc(PG_FUNCTION_ARGS)
{
    CentroidState *s;
    Point4D       *out;
    double         inv;

    if (PG_ARGISNULL(0)) PG_RETURN_NULL();
    s = (CentroidState *) PG_GETARG_POINTER(0);
    if (s->n == 0) PG_RETURN_NULL();

    inv = 1.0 / (double) s->n;
    out = point4d_alloc();
    out->x[0] = s->sum[0] * inv;
    out->x[1] = s->sum[1] * inv;
    out->x[2] = s->sum[2] * inv;
    out->x[3] = s->sum[3] * inv;
    PG_RETURN_POINT4D_P(out);
}

Datum
pg_centroid_s3_ffunc(PG_FUNCTION_ARGS)
{
    CentroidState *s;
    Point4D       *out;
    double         norm;

    if (PG_ARGISNULL(0)) PG_RETURN_NULL();
    s = (CentroidState *) PG_GETARG_POINTER(0);
    if (s->n == 0) PG_RETURN_NULL();

    norm = sqrt(s->sum[0] * s->sum[0] + s->sum[1] * s->sum[1]
              + s->sum[2] * s->sum[2] + s->sum[3] * s->sum[3]);
    if (norm < 1e-12)
        ereport(ERROR,
                (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                 errmsg("centroid_s3: vector sum has zero magnitude (antipodal cancellation)")));

    out = point4d_alloc();
    out->x[0] = s->sum[0] / norm;
    out->x[1] = s->sum[1] / norm;
    out->x[2] = s->sum[2] / norm;
    out->x[3] = s->sum[3] / norm;
    PG_RETURN_POINT4D_P(out);
}

/* ── bbox_4d aggregate ────────────────────────────────────── */

Datum
pg_bbox_4d_sfunc(PG_FUNCTION_ARGS)
{
    Box4D         *box;
    MemoryContext  aggctx;
    Point4D       *p;

    if (!AggCheckCallContext(fcinfo, &aggctx))
        elog(ERROR, "pg_bbox_4d_sfunc: not called in aggregate context");

    if (PG_ARGISNULL(1))
    {
        if (PG_ARGISNULL(0)) PG_RETURN_NULL();
        PG_RETURN_BOX4D_P((Box4D *) PG_GETARG_POINTER(0));
    }

    p = PG_GETARG_POINT4D_P(1);
    if (PG_ARGISNULL(0))
    {
        MemoryContext old = MemoryContextSwitchTo(aggctx);
        box = box4d_from_point(p);
        MemoryContextSwitchTo(old);
        PG_RETURN_BOX4D_P(box);
    }

    box = PG_GETARG_BOX4D_P(0);
    hartonomous_bbox_expand_point((double *) box, p->x);
    PG_RETURN_BOX4D_P(box);
}

Datum
pg_bbox_4d_combine(PG_FUNCTION_ARGS)
{
    Box4D *a = PG_ARGISNULL(0) ? NULL : PG_GETARG_BOX4D_P(0);
    Box4D *b = PG_ARGISNULL(1) ? NULL : PG_GETARG_BOX4D_P(1);
    MemoryContext aggctx;

    if (!AggCheckCallContext(fcinfo, &aggctx))
        elog(ERROR, "pg_bbox_4d_combine: not called in aggregate context");

    if (a == NULL && b == NULL) PG_RETURN_NULL();
    if (a == NULL)
    {
        MemoryContext old = MemoryContextSwitchTo(aggctx);
        Box4D *out = box4d_alloc();
        memcpy(out, b, sizeof(Box4D));
        MemoryContextSwitchTo(old);
        PG_RETURN_BOX4D_P(out);
    }
    if (b == NULL) PG_RETURN_BOX4D_P(a);

    hartonomous_bbox_union((const double *) a, (const double *) b, (double *) a);
    PG_RETURN_BOX4D_P(a);
}
