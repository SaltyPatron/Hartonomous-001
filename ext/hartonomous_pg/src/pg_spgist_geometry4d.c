/*
 * pg_spgist_geometry4d.c — SP-GiST 16-way quad-tree opclass for geometry4d.
 *
 * Partitions on the centroid of the geometry's 4D bounding box (computed via
 * g4d_compute_bbox). Each inner node stores a 4D centroid as bytea (32 raw
 * bytes, mirroring Point4D layout — avoids runtime type OID lookup), with
 * 2^4 = 16 child quadrants.
 *
 * Strategies match the GiST opclass on geometry4d:
 *   1: && (geometry4d, geometry4d) — bbox overlap
 *   2: @> (geometry4d, geometry4d) — bbox contains
 *   3: @> (alias)
 *   4: <@ (geometry4d, geometry4d) — bbox contained-by
 *   5: =  (geometry4d, geometry4d) — recheck on heap
 *
 * leafType = geometry4d (variable), prefixType = bytea, label = void.
 */
#include "postgres.h"
#include "fmgr.h"
#include "varatt.h"
#include "access/spgist.h"
#include "access/skey.h"
#include "access/stratnum.h"
#include "catalog/pg_type.h"
#include "utils/builtins.h"
#include <math.h>

#include "hartonomous.h"
#include "hartonomous_pg.h"

PG_FUNCTION_INFO_V1(spg_geometry4d_config);
PG_FUNCTION_INFO_V1(spg_geometry4d_choose);
PG_FUNCTION_INFO_V1(spg_geometry4d_picksplit);
PG_FUNCTION_INFO_V1(spg_geometry4d_inner_consistent);
PG_FUNCTION_INFO_V1(spg_geometry4d_leaf_consistent);

#define CENTROID_BYTES (sizeof(Point4D))

static bytea *
centroid_to_bytea(const Point4D *p)
{
    bytea *b = (bytea *) palloc(VARHDRSZ + CENTROID_BYTES);
    SET_VARSIZE(b, VARHDRSZ + CENTROID_BYTES);
    memcpy(VARDATA(b), p, CENTROID_BYTES);
    return b;
}

static void
bytea_to_centroid(bytea *b, Point4D *out)
{
    Assert(VARSIZE_ANY_EXHDR(b) == CENTROID_BYTES);
    memcpy(out, VARDATA_ANY(b), CENTROID_BYTES);
}

/* Compute 4D centroid of geometry's bounding box. */
static void
geometry4d_centroid(const Geometry4D *g, Point4D *out)
{
    Box4D bb;
    g4d_compute_bbox(g, &bb);
    out->x[0] = 0.5 * (bb.min[0] + bb.max[0]);
    out->x[1] = 0.5 * (bb.min[1] + bb.max[1]);
    out->x[2] = 0.5 * (bb.min[2] + bb.max[2]);
    out->x[3] = 0.5 * (bb.min[3] + bb.max[3]);
}

static int
quadrant(const Point4D *centroid, const Point4D *p)
{
    int q = 0;
    if (p->x[0] >= centroid->x[0]) q |= 1;
    if (p->x[1] >= centroid->x[1]) q |= 2;
    if (p->x[2] >= centroid->x[2]) q |= 4;
    if (p->x[3] >= centroid->x[3]) q |= 8;
    return q;
}

Datum
spg_geometry4d_config(PG_FUNCTION_ARGS)
{
    spgConfigOut *out = (spgConfigOut *) PG_GETARG_POINTER(1);

    out->prefixType    = BYTEAOID;
    out->labelType     = VOIDOID;
    out->canReturnData = true;
    out->longValuesOK  = true;       /* geometry4d is variable-length */
    PG_RETURN_VOID();
}

Datum
spg_geometry4d_choose(PG_FUNCTION_ARGS)
{
    spgChooseIn  *in  = (spgChooseIn *) PG_GETARG_POINTER(0);
    spgChooseOut *out = (spgChooseOut *) PG_GETARG_POINTER(1);
    Geometry4D   *leaf;
    Point4D       leaf_centroid;
    Point4D       centroid;
    int           q;

    leaf = (Geometry4D *) PG_DETOAST_DATUM(in->datum);
    geometry4d_centroid(leaf, &leaf_centroid);

    if (in->allTheSame)
    {
        out->resultType = spgMatchNode;
        out->result.matchNode.nodeN     = 0;
        out->result.matchNode.levelAdd  = 0;
        out->result.matchNode.restDatum = PointerGetDatum(leaf);
        PG_RETURN_VOID();
    }

    bytea_to_centroid(DatumGetByteaP(in->prefixDatum), &centroid);
    q = quadrant(&centroid, &leaf_centroid);

    out->resultType = spgMatchNode;
    out->result.matchNode.nodeN     = q;
    out->result.matchNode.levelAdd  = 0;
    out->result.matchNode.restDatum = PointerGetDatum(leaf);
    PG_RETURN_VOID();
}

Datum
spg_geometry4d_picksplit(PG_FUNCTION_ARGS)
{
    spgPickSplitIn  *in  = (spgPickSplitIn *) PG_GETARG_POINTER(0);
    spgPickSplitOut *out = (spgPickSplitOut *) PG_GETARG_POINTER(1);
    Point4D          centroid = {{0, 0, 0, 0}};
    Point4D         *cents;
    int              i;

    cents = (Point4D *) palloc(in->nTuples * sizeof(Point4D));

    for (i = 0; i < in->nTuples; i++)
    {
        Geometry4D *g = (Geometry4D *) PG_DETOAST_DATUM(in->datums[i]);
        geometry4d_centroid(g, &cents[i]);
        centroid.x[0] += cents[i].x[0];
        centroid.x[1] += cents[i].x[1];
        centroid.x[2] += cents[i].x[2];
        centroid.x[3] += cents[i].x[3];
    }
    if (in->nTuples > 0)
    {
        centroid.x[0] /= (double) in->nTuples;
        centroid.x[1] /= (double) in->nTuples;
        centroid.x[2] /= (double) in->nTuples;
        centroid.x[3] /= (double) in->nTuples;
    }

    out->hasPrefix          = true;
    out->prefixDatum        = PointerGetDatum(centroid_to_bytea(&centroid));
    out->nNodes             = 16;
    out->nodeLabels         = NULL;
    out->mapTuplesToNodes   = (int *) palloc(in->nTuples * sizeof(int));
    out->leafTupleDatums    = (Datum *) palloc(in->nTuples * sizeof(Datum));

    for (i = 0; i < in->nTuples; i++)
    {
        out->mapTuplesToNodes[i] = quadrant(&centroid, &cents[i]);
        out->leafTupleDatums[i]  = in->datums[i];
    }
    PG_RETURN_VOID();
}

Datum
spg_geometry4d_inner_consistent(PG_FUNCTION_ARGS)
{
    spgInnerConsistentIn  *in  = (spgInnerConsistentIn *) PG_GETARG_POINTER(0);
    spgInnerConsistentOut *out = (spgInnerConsistentOut *) PG_GETARG_POINTER(1);
    Point4D centroid;
    int     i;

    bytea_to_centroid(DatumGetByteaP(in->prefixDatum), &centroid);

    out->nNodes      = 0;
    out->nodeNumbers = (int *) palloc(16 * sizeof(int));

    for (i = 0; i < 16; i++)
    {
        bool keep = true;
        int  j;

        /* Quadrant i covers, per axis k:
         *   upper half [centroid.x[k], +inf) if (i>>k)&1 == 1
         *   lower half (-inf, centroid.x[k]] otherwise
         * For each scan key compute the query bbox and prune. */
        for (j = 0; j < in->nkeys && keep; j++)
        {
            ScanKey     sk = &in->scankeys[j];
            Geometry4D *qg;
            Box4D       qbox;
            int         k;

            switch (sk->sk_strategy)
            {
                case 1: /* && bbox overlap */
                case 2: /* @> bbox contains */
                case 3: /* @> alias */
                case 4: /* <@ bbox contained-by */
                case 5: /* = (recheck) */
                    qg = (Geometry4D *) PG_DETOAST_DATUM(sk->sk_argument);
                    g4d_compute_bbox(qg, &qbox);
                    for (k = 0; k < 4 && keep; k++)
                    {
                        bool upper = ((i >> k) & 1) != 0;
                        if (upper)
                        {
                            if (qbox.max[k] < centroid.x[k]) keep = false;
                        }
                        else
                        {
                            if (qbox.min[k] > centroid.x[k]) keep = false;
                        }
                    }
                    break;
                default:
                    keep = false;
                    break;
            }
        }
        if (keep)
            out->nodeNumbers[out->nNodes++] = i;
    }
    PG_RETURN_VOID();
}

Datum
spg_geometry4d_leaf_consistent(PG_FUNCTION_ARGS)
{
    spgLeafConsistentIn  *in  = (spgLeafConsistentIn *) PG_GETARG_POINTER(0);
    spgLeafConsistentOut *out = (spgLeafConsistentOut *) PG_GETARG_POINTER(1);
    Geometry4D *leaf = (Geometry4D *) PG_DETOAST_DATUM(in->leafDatum);
    Box4D       leaf_bb;
    int         j;
    bool        ok = true;

    g4d_compute_bbox(leaf, &leaf_bb);

    out->leafValue   = PointerGetDatum(leaf);
    out->recheck     = false;

    for (j = 0; j < in->nkeys && ok; j++)
    {
        ScanKey     sk = &in->scankeys[j];
        Geometry4D *qg;
        Box4D       qbox;
        int         k;

        qg = (Geometry4D *) PG_DETOAST_DATUM(sk->sk_argument);
        g4d_compute_bbox(qg, &qbox);

        switch (sk->sk_strategy)
        {
            case 1: /* && overlap */
                for (k = 0; k < 4 && ok; k++)
                    if (leaf_bb.max[k] < qbox.min[k] ||
                        leaf_bb.min[k] > qbox.max[k]) ok = false;
                break;
            case 2: /* @> contains */
            case 3:
                for (k = 0; k < 4 && ok; k++)
                    if (leaf_bb.min[k] > qbox.min[k] ||
                        leaf_bb.max[k] < qbox.max[k]) ok = false;
                break;
            case 4: /* <@ contained-by */
                for (k = 0; k < 4 && ok; k++)
                    if (qbox.min[k] > leaf_bb.min[k] ||
                        qbox.max[k] < leaf_bb.max[k]) ok = false;
                break;
            case 5: /* = — recheck on heap for byte equality */
                for (k = 0; k < 4 && ok; k++)
                    if (leaf_bb.min[k] != qbox.min[k] ||
                        leaf_bb.max[k] != qbox.max[k]) ok = false;
                if (ok) out->recheck = true;
                break;
            default:
                ok = false;
                break;
        }
    }
    PG_RETURN_BOOL(ok);
}
