/*
 * pg_gist_point4d.c — GiST opclass support functions for point4d.
 *
 * Storage type: box4d (R-tree-style internal node = MBR of children).
 * Index entries:
 *   - leaf: point4d compressed to a degenerate box4d (min == max == p).
 *   - inner: union box4d of children.
 *
 * Strategy numbers (kept in sync with hartonomous--1.0.sql):
 *   1: <@ (point4d, box4d)         — containment
 *   2: <-> (point4d, point4d)      — Euclidean kNN ORDER BY
 *   3: <=> (point4d, point4d)      — S³ kNN ORDER BY
 *
 * picksplit: Guttman quadratic split generalized to 4 axes. Pick the seed
 * pair maximizing pairwise Euclidean distance; iteratively assign each
 * remaining entry to the subgroup whose union volume grows less.
 */
#include "postgres.h"
#include "fmgr.h"
#include "access/gist.h"
#include "access/skey.h"
#include "access/stratnum.h"
#include "utils/builtins.h"
#define _USE_MATH_DEFINES
#include <math.h>
#include <float.h>
#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

#include "hartonomous.h"
#include "hartonomous_pg.h"

PG_FUNCTION_INFO_V1(gist_point4d_consistent);
PG_FUNCTION_INFO_V1(gist_point4d_union);
PG_FUNCTION_INFO_V1(gist_point4d_compress);
PG_FUNCTION_INFO_V1(gist_point4d_decompress);
PG_FUNCTION_INFO_V1(gist_point4d_penalty);
PG_FUNCTION_INFO_V1(gist_point4d_picksplit);
PG_FUNCTION_INFO_V1(gist_point4d_same);
PG_FUNCTION_INFO_V1(gist_point4d_distance);

/* ── compress: point4d leaf → box4d ───────────────────────────────── */
Datum
gist_point4d_compress(PG_FUNCTION_ARGS)
{
    GISTENTRY *entry = (GISTENTRY *) PG_GETARG_POINTER(0);
    GISTENTRY *retval;

    if (entry->leafkey)
    {
        Point4D *p = DatumGetPoint4DP(entry->key);
        Box4D   *b = box4d_from_point(p);
        retval = palloc(sizeof(GISTENTRY));
        gistentryinit(*retval, PointerGetDatum(b),
                      entry->rel, entry->page, entry->offset, false);
    }
    else
    {
        retval = entry;
    }
    PG_RETURN_POINTER(retval);
}

/* decompress: identity for box4d storage. */
Datum
gist_point4d_decompress(PG_FUNCTION_ARGS)
{
    PG_RETURN_POINTER(PG_GETARG_POINTER(0));
}

/* same: bit-equal box4d comparison. */
Datum
gist_point4d_same(PG_FUNCTION_ARGS)
{
    Box4D *a = PG_GETARG_BOX4D_P(0);
    Box4D *b = PG_GETARG_BOX4D_P(1);
    bool  *result = (bool *) PG_GETARG_POINTER(2);
    *result = (hartonomous_bbox_equals((const double *) a, (const double *) b) != 0);
    PG_RETURN_POINTER(result);
}

/* union: MBR of all entries. */
Datum
gist_point4d_union(PG_FUNCTION_ARGS)
{
    GistEntryVector *ev = (GistEntryVector *) PG_GETARG_POINTER(0);
    int             *sz = (int *) PG_GETARG_POINTER(1);
    Box4D           *out = box4d_alloc();
    Box4D           *first = DatumGetBox4DP(ev->vector[0].key);

    memcpy(out->min, first->min, 4 * sizeof(double));
    memcpy(out->max, first->max, 4 * sizeof(double));
    for (int i = 1; i < ev->n; i++)
    {
        Box4D *b = DatumGetBox4DP(ev->vector[i].key);
        hartonomous_bbox_union((const double *) out, (const double *) b,
                               (double *) out);
    }
    *sz = sizeof(Box4D);
    PG_RETURN_POINTER(out);
}

/* penalty: volume increase when adding the new entry to the original. */
Datum
gist_point4d_penalty(PG_FUNCTION_ARGS)
{
    GISTENTRY *origentry = (GISTENTRY *) PG_GETARG_POINTER(0);
    GISTENTRY *newentry  = (GISTENTRY *) PG_GETARG_POINTER(1);
    float     *penalty   = (float *) PG_GETARG_POINTER(2);
    Box4D     *orig      = DatumGetBox4DP(origentry->key);
    Box4D     *neu       = DatumGetBox4DP(newentry->key);
    Box4D      united;

    hartonomous_bbox_union((const double *) orig, (const double *) neu,
                           (double *) &united);
    {
        double v_orig = hartonomous_bbox_volume((const double *) orig);
        double v_un   = hartonomous_bbox_volume((const double *) &united);
        double diff   = v_un - v_orig;
        if (diff < 0) diff = 0;
        *penalty = (float) diff;
    }
    PG_RETURN_POINTER(penalty);
}

/* ── picksplit: Guttman quadratic split in 4D ─────────────────────── */
static double
union_volume(const Box4D *a, const Box4D *b)
{
    Box4D u;
    hartonomous_bbox_union((const double *) a, (const double *) b, (double *) &u);
    return hartonomous_bbox_volume((const double *) &u);
}

Datum
gist_point4d_picksplit(PG_FUNCTION_ARGS)
{
    GistEntryVector *ev = (GistEntryVector *) PG_GETARG_POINTER(0);
    GIST_SPLITVEC   *v  = (GIST_SPLITVEC *) PG_GETARG_POINTER(1);
    int              n  = ev->n - 1; /* offsets are 1..n in PG GiST */
    int              seed_l = 1, seed_r = 2;
    double           worst = -1.0;
    Box4D           *box_l, *box_r;
    OffsetNumber    *left, *right;
    int              nleft = 0, nright = 0;
    bool            *assigned;

    /* Find seed pair maximizing wasted-area metric: V(union) - V(a) - V(b). */
    for (int i = 1; i <= n; i++)
    {
        Box4D *bi = DatumGetBox4DP(ev->vector[i].key);
        double vi = hartonomous_bbox_volume((const double *) bi);
        for (int j = i + 1; j <= n; j++)
        {
            Box4D *bj = DatumGetBox4DP(ev->vector[j].key);
            double vj = hartonomous_bbox_volume((const double *) bj);
            double waste = union_volume(bi, bj) - vi - vj;
            if (waste > worst)
            {
                worst = waste;
                seed_l = i;
                seed_r = j;
            }
        }
    }

    box_l = box4d_alloc();
    box_r = box4d_alloc();
    memcpy(box_l, DatumGetBox4DP(ev->vector[seed_l].key), sizeof(Box4D));
    memcpy(box_r, DatumGetBox4DP(ev->vector[seed_r].key), sizeof(Box4D));

    left  = (OffsetNumber *) palloc((n + 1) * sizeof(OffsetNumber));
    right = (OffsetNumber *) palloc((n + 1) * sizeof(OffsetNumber));
    assigned = (bool *) palloc0((n + 2) * sizeof(bool));

    left[nleft++]   = (OffsetNumber) seed_l;
    right[nright++] = (OffsetNumber) seed_r;
    assigned[seed_l] = true;
    assigned[seed_r] = true;

    /* Iterative assignment: each unassigned entry goes to the side whose
     * union volume grows less. Ties broken toward the smaller group. */
    for (int i = 1; i <= n; i++)
    {
        Box4D *bi;
        double dl, dr;

        if (assigned[i]) continue;

        bi = DatumGetBox4DP(ev->vector[i].key);
        dl = union_volume(box_l, bi) - hartonomous_bbox_volume((const double *) box_l);
        dr = union_volume(box_r, bi) - hartonomous_bbox_volume((const double *) box_r);

        if (dl < dr || (dl == dr && nleft <= nright))
        {
            hartonomous_bbox_union((const double *) box_l,
                                   (const double *) bi,
                                   (double *) box_l);
            left[nleft++] = (OffsetNumber) i;
        }
        else
        {
            hartonomous_bbox_union((const double *) box_r,
                                   (const double *) bi,
                                   (double *) box_r);
            right[nright++] = (OffsetNumber) i;
        }
    }

    pfree(assigned);

    v->spl_left    = left;
    v->spl_nleft   = nleft;
    v->spl_ldatum  = PointerGetDatum(box_l);
    v->spl_right   = right;
    v->spl_nright  = nright;
    v->spl_rdatum  = PointerGetDatum(box_r);

    PG_RETURN_POINTER(v);
}

/* consistent: dispatched by strategy. */
Datum
gist_point4d_consistent(PG_FUNCTION_ARGS)
{
    GISTENTRY  *entry    = (GISTENTRY *) PG_GETARG_POINTER(0);
    Datum       qd       = PG_GETARG_DATUM(1);
    StrategyNumber strat = (StrategyNumber) PG_GETARG_UINT16(2);
    /* Oid    subtype   = PG_GETARG_OID(3); */
    bool       *recheck  = (bool *) PG_GETARG_POINTER(4);
    Box4D      *key      = DatumGetBox4DP(entry->key);

    *recheck = false;

    switch (strat)
    {
        case 1: /* point4d <@ box4d : key intersects query box, leaves recheck */
        {
            Box4D *qbox = DatumGetBox4DP(qd);
            if (GIST_LEAF(entry))
                PG_RETURN_BOOL(hartonomous_bbox_contains_box(
                    (const double *) qbox, (const double *) key) != 0);
            PG_RETURN_BOOL(hartonomous_bbox_overlaps(
                (const double *) key, (const double *) qbox) != 0);
        }
        case 2: /* <-> ORDER BY: distance fn does the work; consistent must accept */
        case 3:
            /* For ORDER BY-only operators consistent isn't strategy-tested for
             * filtering; return true so distance fn drives priority. */
            PG_RETURN_BOOL(true);
        default:
            elog(ERROR, "unrecognized GiST strategy number: %d", strat);
            PG_RETURN_BOOL(false);
    }
}

/* distance: lower bound from query point to key box. Used for kNN ORDER BY. */
Datum
gist_point4d_distance(PG_FUNCTION_ARGS)
{
    GISTENTRY    *entry = (GISTENTRY *) PG_GETARG_POINTER(0);
    Datum         qd    = PG_GETARG_DATUM(1);
    StrategyNumber s    = (StrategyNumber) PG_GETARG_UINT16(2);
    /* Oid       subtype = PG_GETARG_OID(3); */
    bool         *recheck = (bool *) PG_GETARG_POINTER(4);
    Box4D        *key   = DatumGetBox4DP(entry->key);
    Point4D      *q     = DatumGetPoint4DP(qd);

    *recheck = false;

    switch (s)
    {
        case 2:
            /* Euclidean: tight lower bound = box-point min distance. */
            PG_RETURN_FLOAT8(hartonomous_bbox_min_distance_4d(
                (const double *) key, q->x));
        case 3:
        {
            /* S³ geodesic: no exact box-to-point geodesic LB. Use an
             * admissible LB derived from Euclidean chord:
             *   d_geo >= 2 * asin(d_chord / 2), valid for chord <= 2.
             * For chord > 2 (impossible on S³ with unit-norm), clamp. */
            double chord = hartonomous_bbox_min_distance_4d(
                (const double *) key, q->x);
            double half  = chord * 0.5;
            double lb;
            if (half >= 1.0) lb = M_PI;
            else lb = 2.0 * asin(half);
            *recheck = true; /* finalize with exact distance_s3 on heap */
            PG_RETURN_FLOAT8(lb);
        }
        default:
            elog(ERROR, "unrecognized GiST distance strategy: %d", s);
            PG_RETURN_FLOAT8(0.0);
    }
}
