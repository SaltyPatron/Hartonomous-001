/*
 * pg_spgist_point4d.c — SP-GiST quad-tree opclass for point4d.
 *
 * 16-way (2^4) sign-pattern partitioning. Each inner node carries a 4D
 * centroid stored as the prefix datum (encoded as bytea = 32 raw bytes
 * mirroring Point4D layout — avoids runtime type OID lookup).
 *
 * Strategies match the GiST opclass:
 *   1: <@ (point4d, box4d)
 *   2: <-> (point4d, point4d)   ORDER BY
 *   3: <=> (point4d, point4d)   ORDER BY
 *
 * leafType = point4d (column type), prefixType = bytea, label = void.
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

PG_FUNCTION_INFO_V1(spg_point4d_config);
PG_FUNCTION_INFO_V1(spg_point4d_choose);
PG_FUNCTION_INFO_V1(spg_point4d_picksplit);
PG_FUNCTION_INFO_V1(spg_point4d_inner_consistent);
PG_FUNCTION_INFO_V1(spg_point4d_leaf_consistent);

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
spg_point4d_config(PG_FUNCTION_ARGS)
{
    spgConfigOut *out = (spgConfigOut *) PG_GETARG_POINTER(1);

    out->prefixType    = BYTEAOID;
    out->labelType     = VOIDOID;
    out->canReturnData = true;
    out->longValuesOK  = false;
    PG_RETURN_VOID();
}

Datum
spg_point4d_choose(PG_FUNCTION_ARGS)
{
    spgChooseIn  *in  = (spgChooseIn *) PG_GETARG_POINTER(0);
    spgChooseOut *out = (spgChooseOut *) PG_GETARG_POINTER(1);
    Point4D      *leaf;
    Point4D       centroid;
    int           q;

    leaf = DatumGetPoint4DP(in->datum);

    if (in->allTheSame)
    {
        out->resultType = spgMatchNode;
        out->result.matchNode.nodeN     = 0;
        out->result.matchNode.levelAdd  = 0;
        out->result.matchNode.restDatum = PointerGetDatum(leaf);
        PG_RETURN_VOID();
    }

    bytea_to_centroid(DatumGetByteaP(in->prefixDatum), &centroid);
    q = quadrant(&centroid, leaf);

    out->resultType = spgMatchNode;
    out->result.matchNode.nodeN     = q;
    out->result.matchNode.levelAdd  = 0;
    out->result.matchNode.restDatum = PointerGetDatum(leaf);
    PG_RETURN_VOID();
}

Datum
spg_point4d_picksplit(PG_FUNCTION_ARGS)
{
    spgPickSplitIn  *in  = (spgPickSplitIn *) PG_GETARG_POINTER(0);
    spgPickSplitOut *out = (spgPickSplitOut *) PG_GETARG_POINTER(1);
    Point4D          centroid = {{0, 0, 0, 0}};
    int              i;

    for (i = 0; i < in->nTuples; i++)
    {
        Point4D *p = DatumGetPoint4DP(in->datums[i]);
        centroid.x[0] += p->x[0];
        centroid.x[1] += p->x[1];
        centroid.x[2] += p->x[2];
        centroid.x[3] += p->x[3];
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
        Point4D *p = DatumGetPoint4DP(in->datums[i]);
        out->mapTuplesToNodes[i] = quadrant(&centroid, p);
        out->leafTupleDatums[i]  = in->datums[i];
    }
    PG_RETURN_VOID();
}

Datum
spg_point4d_inner_consistent(PG_FUNCTION_ARGS)
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

        for (j = 0; j < in->nkeys && keep; j++)
        {
            ScanKey sk = &in->scankeys[j];
            switch (sk->sk_strategy)
            {
                case 1: /* point4d <@ box4d */
                {
                    Box4D *qb = DatumGetBox4DP(sk->sk_argument);
                    int k;
                    for (k = 0; k < 4 && keep; k++)
                    {
                        bool upper = ((i >> k) & 1) != 0;
                        if (upper)
                        {
                            if (qb->max[k] < centroid.x[k]) keep = false;
                        }
                        else
                        {
                            if (qb->min[k] > centroid.x[k]) keep = false;
                        }
                    }
                    break;
                }
                case 2:
                case 3:
                    /* kNN: visit all children. */
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
spg_point4d_leaf_consistent(PG_FUNCTION_ARGS)
{
    spgLeafConsistentIn  *in  = (spgLeafConsistentIn *) PG_GETARG_POINTER(0);
    spgLeafConsistentOut *out = (spgLeafConsistentOut *) PG_GETARG_POINTER(1);
    Point4D *leaf = DatumGetPoint4DP(in->leafDatum);
    int      j;
    bool     ok = true;

    out->leafValue   = PointerGetDatum(leaf);
    out->recheck     = false;

    for (j = 0; j < in->nkeys && ok; j++)
    {
        ScanKey sk = &in->scankeys[j];
        switch (sk->sk_strategy)
        {
            case 1:
            {
                Box4D *qb = DatumGetBox4DP(sk->sk_argument);
                if (hartonomous_bbox_contains_point(
                        (const double *) qb, leaf->x) == 0)
                    ok = false;
                break;
            }
            case 2:
            case 3:
                break;
            default:
                ok = false;
                break;
        }
    }
    PG_RETURN_BOOL(ok);
}
