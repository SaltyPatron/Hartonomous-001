/*
 * pg_gist_geometry4d.c — GiST opclass for the umbrella geometry4d type.
 *
 * Storage: box4d. Leaf entries are compressed via g4d_compute_bbox(); inner
 * nodes hold the union MBR. Same picksplit/penalty/union as point4d's GiST,
 * which is correct because both opclasses index a 4D bounding rectangle.
 *
 * Strategy numbers (kept in sync with hartonomous--1.0.sql):
 *   1: && (geometry4d, geometry4d)  — bbox overlap
 *   2: ~  (geometry4d, geometry4d)  — bbox of A contains bbox of B
 *   3: @> (geometry4d, geometry4d)  — alias for 2 (containment)
 *   4: <@ (geometry4d, geometry4d)  — A contained by B
 *   5: =  (geometry4d, geometry4d)  — bbox-equal (recheck for byte-equality)
 *   6: <-> (geometry4d, point4d)    — kNN ORDER BY (4D Euclidean to bbox)
 *
 * The umbrella opclass intentionally matches the point4d opclass shape so
 * planner cost models can be cross-validated.
 */
#include "postgres.h"
#include "fmgr.h"
#include "access/gist.h"
#include "access/skey.h"
#include "access/stratnum.h"
#include "utils/builtins.h"
#include <math.h>
#include <float.h>

#include "hartonomous.h"
#include "hartonomous_pg.h"

PG_FUNCTION_INFO_V1(gist_geometry4d_consistent);
PG_FUNCTION_INFO_V1(gist_geometry4d_union);
PG_FUNCTION_INFO_V1(gist_geometry4d_compress);
PG_FUNCTION_INFO_V1(gist_geometry4d_decompress);
PG_FUNCTION_INFO_V1(gist_geometry4d_penalty);
PG_FUNCTION_INFO_V1(gist_geometry4d_picksplit);
PG_FUNCTION_INFO_V1(gist_geometry4d_same);
PG_FUNCTION_INFO_V1(gist_geometry4d_distance);

/* ── compress: geometry4d leaf → box4d (via g4d_compute_bbox) ─────── */
Datum
gist_geometry4d_compress(PG_FUNCTION_ARGS)
{
    GISTENTRY *entry = (GISTENTRY *) PG_GETARG_POINTER(0);
    GISTENTRY *retval;

    if (entry->leafkey)
    {
        Geometry4D *g = (Geometry4D *) PG_DETOAST_DATUM(entry->key);
        Box4D      *b = box4d_alloc();
        g4d_compute_bbox(g, b);
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

Datum
gist_geometry4d_decompress(PG_FUNCTION_ARGS)
{
    PG_RETURN_POINTER(PG_GETARG_POINTER(0));
}

Datum
gist_geometry4d_same(PG_FUNCTION_ARGS)
{
    Box4D *a = PG_GETARG_BOX4D_P(0);
    Box4D *b = PG_GETARG_BOX4D_P(1);
    bool  *result = (bool *) PG_GETARG_POINTER(2);
    *result = (hartonomous_bbox_equals((const double *) a, (const double *) b) != 0);
    PG_RETURN_POINTER(result);
}

Datum
gist_geometry4d_union(PG_FUNCTION_ARGS)
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

Datum
gist_geometry4d_penalty(PG_FUNCTION_ARGS)
{
    GISTENTRY *origentry = (GISTENTRY *) PG_GETARG_POINTER(0);
    GISTENTRY *newentry  = (GISTENTRY *) PG_GETARG_POINTER(1);
    float     *penalty   = (float *) PG_GETARG_POINTER(2);
    Box4D     *orig      = DatumGetBox4DP(origentry->key);
    Box4D     *neu       = DatumGetBox4DP(newentry->key);
    Box4D      united;
    double     v_orig, v_un, diff;

    hartonomous_bbox_union((const double *) orig, (const double *) neu,
                           (double *) &united);
    v_orig = hartonomous_bbox_volume((const double *) orig);
    v_un   = hartonomous_bbox_volume((const double *) &united);
    diff   = v_un - v_orig;
    if (diff < 0) diff = 0;
    *penalty = (float) diff;
    PG_RETURN_POINTER(penalty);
}

static double
g4d_union_volume(const Box4D *a, const Box4D *b)
{
    Box4D u;
    hartonomous_bbox_union((const double *) a, (const double *) b, (double *) &u);
    return hartonomous_bbox_volume((const double *) &u);
}

Datum
gist_geometry4d_picksplit(PG_FUNCTION_ARGS)
{
    GistEntryVector *ev = (GistEntryVector *) PG_GETARG_POINTER(0);
    GIST_SPLITVEC   *v  = (GIST_SPLITVEC *) PG_GETARG_POINTER(1);
    int              n  = ev->n - 1;
    int              seed_l = 1, seed_r = 2;
    double           worst = -1.0;
    Box4D           *box_l, *box_r;
    OffsetNumber    *left, *right;
    int              nleft = 0, nright = 0;
    bool            *assigned;

    for (int i = 1; i <= n; i++)
    {
        Box4D *bi = DatumGetBox4DP(ev->vector[i].key);
        double vi = hartonomous_bbox_volume((const double *) bi);
        for (int j = i + 1; j <= n; j++)
        {
            Box4D *bj = DatumGetBox4DP(ev->vector[j].key);
            double vj = hartonomous_bbox_volume((const double *) bj);
            double waste = g4d_union_volume(bi, bj) - vi - vj;
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

    for (int i = 1; i <= n; i++)
    {
        Box4D *bi;
        double dl, dr;
        if (assigned[i]) continue;
        bi = DatumGetBox4DP(ev->vector[i].key);
        dl = g4d_union_volume(box_l, bi) - hartonomous_bbox_volume((const double *) box_l);
        dr = g4d_union_volume(box_r, bi) - hartonomous_bbox_volume((const double *) box_r);
        if (dl < dr || (dl == dr && nleft <= nright))
        {
            hartonomous_bbox_union((const double *) box_l, (const double *) bi,
                                   (double *) box_l);
            left[nleft++] = (OffsetNumber) i;
        }
        else
        {
            hartonomous_bbox_union((const double *) box_r, (const double *) bi,
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

/* consistent: bbox-only filtering. Strategies 1/2/3/4/5 take a geometry4d as
 * the right-hand argument and we compress it on the fly to a box. Strategy 5
 * (equality) sets recheck=true so byte-equality is enforced on the heap. */
Datum
gist_geometry4d_consistent(PG_FUNCTION_ARGS)
{
    GISTENTRY     *entry = (GISTENTRY *) PG_GETARG_POINTER(0);
    Datum          qd    = PG_GETARG_DATUM(1);
    StrategyNumber strat = (StrategyNumber) PG_GETARG_UINT16(2);
    /* Oid    subtype   = PG_GETARG_OID(3); */
    bool          *recheck = (bool *) PG_GETARG_POINTER(4);
    Box4D         *key     = DatumGetBox4DP(entry->key);
    Geometry4D    *qg;
    Box4D          qbox;

    qg = (Geometry4D *) PG_DETOAST_DATUM(qd);
    g4d_compute_bbox(qg, &qbox);

    *recheck = false;

    switch (strat)
    {
        case 1: /* && bbox overlap */
            PG_RETURN_BOOL(hartonomous_bbox_overlaps(
                (const double *) key, (const double *) &qbox) != 0);
        case 2: /* ~ key contains qbox */
        case 3: /* @> alias */
            if (GIST_LEAF(entry))
                PG_RETURN_BOOL(hartonomous_bbox_contains_box(
                    (const double *) key, (const double *) &qbox) != 0);
            /* inner: must overlap to descend; final check on leaf. */
            PG_RETURN_BOOL(hartonomous_bbox_overlaps(
                (const double *) key, (const double *) &qbox) != 0);
        case 4: /* <@ key contained by qbox */
            if (GIST_LEAF(entry))
                PG_RETURN_BOOL(hartonomous_bbox_contains_box(
                    (const double *) &qbox, (const double *) key) != 0);
            PG_RETURN_BOOL(hartonomous_bbox_overlaps(
                (const double *) key, (const double *) &qbox) != 0);
        case 5: /* = bbox-equal, recheck enforces byte-equal */
            *recheck = true;
            PG_RETURN_BOOL(hartonomous_bbox_equals(
                (const double *) key, (const double *) &qbox) != 0);
        default:
            elog(ERROR, "unrecognized GiST strategy: %d", strat);
            PG_RETURN_BOOL(false);
    }
}

/* distance: kNN to a point4d (strategy 6). Lower bound = box-point distance. */
Datum
gist_geometry4d_distance(PG_FUNCTION_ARGS)
{
    GISTENTRY     *entry = (GISTENTRY *) PG_GETARG_POINTER(0);
    Datum          qd    = PG_GETARG_DATUM(1);
    StrategyNumber s     = (StrategyNumber) PG_GETARG_UINT16(2);
    /* Oid    subtype = PG_GETARG_OID(3); */
    bool          *recheck = (bool *) PG_GETARG_POINTER(4);
    Box4D         *key   = DatumGetBox4DP(entry->key);
    Point4D       *q     = DatumGetPoint4DP(qd);

    *recheck = true; /* exact distance recomputed on heap */

    if (s == 6)
    {
        PG_RETURN_FLOAT8(hartonomous_bbox_min_distance_4d(
            (const double *) key, q->x));
    }
    elog(ERROR, "unrecognized GiST distance strategy: %d", s);
    PG_RETURN_FLOAT8(0.0);
}
