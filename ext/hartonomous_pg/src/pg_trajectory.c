/*
 * pg_trajectory.c — Fréchet and Hausdorff distance between linestring4d.
 *
 * Backed by libhartonomous primitives `hartonomous_frechet_4d` (which needs
 * an O(na*nb) workspace) and `hartonomous_hausdorff_4d`.
 */
#include "postgres.h"
#include "fmgr.h"
#include <math.h>

#include "hartonomous.h"
#include "hartonomous_pg.h"

PG_FUNCTION_INFO_V1(pg_frechet_4d);
PG_FUNCTION_INFO_V1(pg_hausdorff_4d);

/* Pack a LineString4D into a flat double[] in row-major order, since the
 * native lib expects 4*npoints contiguous doubles. Avoids realloc by reusing
 * the LineString4D->points memory directly via cast (Point4D == double[4]
 * with no padding under alignment=double). */
static const double *
ls4d_as_doubles(const LineString4D *ls)
{
    return (const double *) ls->points;
}

Datum
pg_frechet_4d(PG_FUNCTION_ARGS)
{
    LineString4D *a = PG_GETARG_LINESTRING4D_P(0);
    LineString4D *b = PG_GETARG_LINESTRING4D_P(1);
    size_t na = (size_t) a->npoints;
    size_t nb = (size_t) b->npoints;
    double *ws;
    double  d;

    if (na == 0 || nb == 0)
        PG_RETURN_NULL();
    ws = (double *) palloc(na * nb * sizeof(double));
    d = hartonomous_frechet_4d(ls4d_as_doubles(a), na,
                               ls4d_as_doubles(b), nb, ws);
    pfree(ws);
    if (isnan(d))
        ereport(ERROR,
                (errcode(ERRCODE_INTERNAL_ERROR),
                 errmsg("frechet_4d returned NaN")));
    PG_RETURN_FLOAT8(d);
}

Datum
pg_hausdorff_4d(PG_FUNCTION_ARGS)
{
    LineString4D *a = PG_GETARG_LINESTRING4D_P(0);
    LineString4D *b = PG_GETARG_LINESTRING4D_P(1);
    double d;

    if (a->npoints == 0 || b->npoints == 0)
        PG_RETURN_NULL();
    d = hartonomous_hausdorff_4d(ls4d_as_doubles(a), (size_t) a->npoints,
                                 ls4d_as_doubles(b), (size_t) b->npoints);
    PG_RETURN_FLOAT8(d);
}
