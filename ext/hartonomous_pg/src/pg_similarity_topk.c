/*
 * pg_similarity_topk.c — bounded-K nearest-neighbor scan over arbitrary
 *                        candidate query producing (entity_type_id, entity_hash, geom).
 *
 * Substrate contract:
 *   substrate.similarity_topk(
 *       p_seed_geom            geometry,
 *       p_k                    int,
 *       p_distance_kind        text,           -- '4d' | 's3' | 'frechet'
 *       p_candidate_query      text,           -- SELECT (entity_type_id int, entity_hash bytea, geom geometry)
 *       p_distance_threshold   double precision DEFAULT NULL
 *   ) RETURNS TABLE (entity_type_id int, entity_hash bytea, distance double precision)
 *
 * The function spins a min-heap of size K (capacity-bounded), iterates the
 * candidate cursor exactly once, and emits the K closest candidates ordered
 * by ascending distance.
 *
 * Distance dispatch (resolved once at SRF init via LookupFuncName):
 *   '4d'      → substrate.dist_4d(geometry, geometry)
 *   's3'      → substrate.dist_s3(geometry, geometry)
 *   'frechet' → substrate.frechet_4d_geom(geometry, geometry)
 *
 * Memory discipline (mirrors pg_traversal.c UAF fix):
 *   Every bytea hash and geometry datum that needs to survive SPI_finish()
 *   is deep-copied into funcctx->multi_call_memory_ctx BEFORE the SPI_finish.
 *   Anything left in CurrentMemoryContext (the SPI procedure context) is
 *   freed when SPI_finish unwinds — a SRF_PERCALL read of such a payload
 *   would be a use-after-free.
 *
 * The seed geometry passed in by the caller is detoasted into the multi-call
 * context up front so the SPI loop's distance calls can reuse it without
 * worrying about its lifetime.
 */
#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "executor/spi.h"
#include "utils/builtins.h"
#include "utils/memutils.h"
#include "utils/lsyscache.h"
#include "catalog/pg_type.h"
#include "catalog/namespace.h"
#include "parser/parse_func.h"
#include "access/htup_details.h"
#include "nodes/value.h"
#include "nodes/pg_list.h"
#include "fmgr.h"

#include <string.h>
#include <math.h>

PG_FUNCTION_INFO_V1(pg_similarity_topk);

#define SIMTOPK_HASH_LEN 32   /* substrate hash_value domain = BYTEA(32) */

/*
 * One candidate kept in the bounded min-heap. The hash is deep-copied into
 * multi_call_memory_ctx as raw 32-byte storage; on emit we wrap it with a
 * fresh bytea palloc'd in the per-call context.
 */
typedef struct TopKEntry
{
    int32   entity_type_id;
    uint8   entity_hash[SIMTOPK_HASH_LEN];
    double  distance;
} TopKEntry;

/*
 * Min-heap over distance with bounded capacity K. We keep the heap as a
 * MAX-heap of size K (root = largest distance currently retained) so we can
 * cheaply check "should we evict?" by comparing against the root, and pop
 * the worst when a better candidate arrives.
 *
 * On final emission we re-sort the K entries in ascending distance order
 * (heap-pop in MAX-heap order, then reverse) — clearer than maintaining a
 * second sorted view.
 */
typedef struct TopKHeap
{
    TopKEntry *entries;   /* allocated in multi_call_memory_ctx, capacity K */
    int        count;
    int        capacity;  /* == K */
} TopKHeap;

typedef struct SimTopKState
{
    TopKEntry *sorted;    /* ascending by distance */
    int        num_results;
    int        current;
    TupleDesc  tupdesc;
} SimTopKState;

static inline void
heap_swap(TopKEntry *a, TopKEntry *b)
{
    TopKEntry tmp = *a;
    *a = *b;
    *b = tmp;
}

/* MAX-heap sift-up by distance: root is the worst (largest) distance. */
static void
maxheap_push(TopKHeap *h, const TopKEntry *e)
{
    int idx, parent;

    Assert(h->count < h->capacity);
    h->entries[h->count] = *e;
    idx = h->count++;

    while (idx > 0)
    {
        parent = (idx - 1) / 2;
        if (h->entries[parent].distance >= h->entries[idx].distance)
            break;
        heap_swap(&h->entries[parent], &h->entries[idx]);
        idx = parent;
    }
}

/* Replace the root (worst) with new entry and sift down. */
static void
maxheap_replace_root(TopKHeap *h, const TopKEntry *e)
{
    int idx = 0;

    Assert(h->count > 0);
    h->entries[0] = *e;

    for (;;)
    {
        int left  = 2 * idx + 1;
        int right = 2 * idx + 2;
        int largest = idx;

        if (left  < h->count && h->entries[left ].distance > h->entries[largest].distance) largest = left;
        if (right < h->count && h->entries[right].distance > h->entries[largest].distance) largest = right;
        if (largest == idx) break;
        heap_swap(&h->entries[idx], &h->entries[largest]);
        idx = largest;
    }
}

/*
 * Resolve substrate.dist_4d(geometry,geometry) / substrate.dist_s3(...) /
 * substrate.frechet_4d_geom(...) by name. Returns the OID. Errors with a
 * helpful message if the function isn't installed (e.g. caller asked for 's3'
 * but the substrate-side S³-on-geometry wrapper has not been declared yet).
 */
static Oid
resolve_distance_function(const char *kind)
{
    List   *qname;
    Oid     argtypes[2];
    Oid     fnoid;
    const char *fn_local;

    if (strcmp(kind, "4d") == 0)
        fn_local = "dist_4d";
    else if (strcmp(kind, "s3") == 0)
        fn_local = "dist_s3";
    else if (strcmp(kind, "frechet") == 0)
        fn_local = "frechet_4d_geom";
    else
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("unknown distance_kind: %s, expected '4d'|'s3'|'frechet'", kind)));

    argtypes[0] = TypenameGetTypid("geometry");
    argtypes[1] = TypenameGetTypid("geometry");
    if (!OidIsValid(argtypes[0]) || !OidIsValid(argtypes[1]))
        ereport(ERROR,
                (errcode(ERRCODE_UNDEFINED_OBJECT),
                 errmsg("PostGIS geometry type not found — postgis extension required")));

    qname = list_make2(makeString("substrate"), makeString((char *) fn_local));
    fnoid = LookupFuncName(qname, 2, argtypes, true /* missing_ok */);
    list_free_deep(qname);

    if (!OidIsValid(fnoid))
        ereport(ERROR,
                (errcode(ERRCODE_UNDEFINED_FUNCTION),
                 errmsg("substrate.%s(geometry, geometry) not found",
                        fn_local),
                 errhint("Distance kind '%s' requires the substrate-side wrapper. "
                         "See sql/schema/functions/geom_bridge_4d.sql.", kind)));

    return fnoid;
}

Datum
pg_similarity_topk(PG_FUNCTION_ARGS)
{
    FuncCallContext *funcctx;
    SimTopKState    *state;

    if (SRF_IS_FIRSTCALL())
    {
        MemoryContext   oldctx;
        MemoryContext   mctx;
        bytea          *seed_geom_in_raw;
        Datum           seed_geom_datum;          /* lives in multi_call_memory_ctx */
        int32           k;
        text           *kind_txt;
        char           *kind_str;
        text           *cand_txt;
        char           *cand_query;
        bool            threshold_is_null;
        double          threshold;
        Oid             dist_fnoid;
        FmgrInfo        dist_fmgr;
        TopKHeap        heap;
        int             ret;
        Portal          cur;
        SPIPlanPtr      cand_plan;
        TupleDesc       cand_desc;
        int             entity_type_attno = 0;
        int             entity_hash_attno = 0;
        int             geom_attno = 0;
        int             a;
        int             num_processed;

        funcctx = SRF_FIRSTCALL_INIT();
        mctx = funcctx->multi_call_memory_ctx;
        oldctx = MemoryContextSwitchTo(mctx);

        /* ── argument extraction & validation ─────────────────────────── */
        if (PG_ARGISNULL(0))
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("p_seed_geom must not be NULL")));
        if (PG_ARGISNULL(1))
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("p_k must not be NULL")));
        if (PG_ARGISNULL(2))
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("p_distance_kind must not be NULL")));
        if (PG_ARGISNULL(3))
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("p_candidate_query must not be NULL")));

        /*
         * Detoast the seed geometry into multi_call_memory_ctx so it survives
         * SPI_finish and can be reused on every distance call.
         */
        seed_geom_in_raw = PG_GETARG_BYTEA_P_COPY(0);
        seed_geom_datum = PointerGetDatum(seed_geom_in_raw);

        k = PG_GETARG_INT32(1);
        if (k < 1)
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                     errmsg("p_k must be >= 1, got %d", k)));
        if (k > 100000)
            ereport(ERROR,
                    (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                     errmsg("p_k must be <= 100000, got %d", k)));

        kind_txt = PG_GETARG_TEXT_PP(2);
        kind_str = text_to_cstring(kind_txt);

        cand_txt = PG_GETARG_TEXT_PP(3);
        cand_query = text_to_cstring(cand_txt);

        threshold_is_null = PG_ARGISNULL(4);
        threshold = threshold_is_null ? 0.0 : PG_GETARG_FLOAT8(4);

        /* ── distance function resolution + fmgr cache ────────────────── */
        dist_fnoid = resolve_distance_function(kind_str);
        fmgr_info(dist_fnoid, &dist_fmgr);

        /* ── allocate heap in mctx ────────────────────────────────────── */
        heap.capacity = k;
        heap.count = 0;
        heap.entries = (TopKEntry *) MemoryContextAllocZero(mctx, sizeof(TopKEntry) * k);

        /* ── SPI: prepare candidate query, open cursor, scan once ─────── */
        SPI_connect();

        cand_plan = SPI_prepare(cand_query, 0, NULL);
        if (cand_plan == NULL)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("SPI_prepare failed for candidate query: %s",
                            SPI_result_code_string(SPI_result))));

        cur = SPI_cursor_open(NULL, cand_plan, NULL, NULL, true /* read_only */);
        if (cur == NULL)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("SPI_cursor_open failed for candidate query")));

        /*
         * Resolve the result column attnos by name. The candidate query
         * MUST yield (entity_type_id int, entity_hash bytea, geom geometry).
         * Position-based extraction would silently misread arbitrary user
         * SELECTs; a name lookup fails loud if the contract is broken.
         */
        SPI_cursor_fetch(cur, true /* forward */, 1);
        cand_desc = SPI_tuptable ? SPI_tuptable->tupdesc : NULL;
        if (cand_desc == NULL)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("candidate cursor returned no tuple descriptor")));

        for (a = 0; a < cand_desc->natts; a++)
        {
            const char *aname = NameStr(TupleDescAttr(cand_desc, a)->attname);
            if (strcmp(aname, "entity_type_id") == 0) entity_type_attno = a + 1;
            else if (strcmp(aname, "entity_hash") == 0) entity_hash_attno = a + 1;
            else if (strcmp(aname, "geom") == 0) geom_attno = a + 1;
        }
        if (entity_type_attno == 0 || entity_hash_attno == 0 || geom_attno == 0)
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                     errmsg("candidate query must select columns "
                            "(entity_type_id int, entity_hash bytea, geom geometry)")));

        /* Process the first batch then keep fetching. */
        num_processed = (int) SPI_processed;
        while (num_processed > 0)
        {
            int row;

            for (row = 0; row < num_processed; row++)
            {
                HeapTuple  tuple = SPI_tuptable->vals[row];
                bool       isnull;
                Datum      d_etype, d_hash, d_geom;
                int32      etype;
                bytea     *hash_b;
                int        hash_len;
                double     dist;
                Datum      dist_d;
                TopKEntry  ent;

                d_etype = SPI_getbinval(tuple, cand_desc, entity_type_attno, &isnull);
                if (isnull) continue;
                etype = DatumGetInt32(d_etype);

                d_hash = SPI_getbinval(tuple, cand_desc, entity_hash_attno, &isnull);
                if (isnull) continue;
                hash_b = DatumGetByteaPP(d_hash);
                hash_len = VARSIZE_ANY_EXHDR(hash_b);
                if (hash_len != SIMTOPK_HASH_LEN) continue;

                d_geom = SPI_getbinval(tuple, cand_desc, geom_attno, &isnull);
                if (isnull) continue;

                /*
                 * Distance call. seed_geom_datum lives in mctx; the candidate
                 * geometry datum lives in the SPI tuple's context — both are
                 * read-only inputs to the immutable distance function.
                 */
                dist_d = FunctionCall2Coll(&dist_fmgr, InvalidOid,
                                           seed_geom_datum,
                                           d_geom);
                dist = DatumGetFloat8(dist_d);

                if (!threshold_is_null && dist > threshold)
                    continue;
                if (isnan(dist) || isinf(dist))
                    continue;

                /*
                 * Build a candidate entry. We deep-copy the hash bytes into
                 * the heap's TopKEntry storage (which lives in mctx), so it
                 * survives SPI_finish() — see file header memory discipline.
                 */
                ent.entity_type_id = etype;
                ent.distance = dist;
                memcpy(ent.entity_hash, VARDATA_ANY(hash_b), SIMTOPK_HASH_LEN);

                if (heap.count < heap.capacity)
                {
                    maxheap_push(&heap, &ent);
                }
                else if (heap.entries[0].distance > dist)
                {
                    maxheap_replace_root(&heap, &ent);
                }
            }

            SPI_freetuptable(SPI_tuptable);
            SPI_cursor_fetch(cur, true, 1024);
            num_processed = (int) SPI_processed;
        }

        SPI_cursor_close(cur);
        SPI_finish();
        /* From here on, anything in CurrentMemoryContext from inside the
         * SPI block has been freed. heap.entries (mctx) and seed_geom_datum
         * (mctx) survive. */

        /* ── Sort the heap ascending by distance for emission ─────────── */
        {
            TopKEntry *sorted = (TopKEntry *)
                MemoryContextAllocZero(mctx, sizeof(TopKEntry) * heap.count);
            int n = heap.count;
            int i;

            /* MAX-heap pop yields descending order; fill sorted from tail. */
            for (i = n - 1; i >= 0; i--)
            {
                /* Heap root is the current max; record it then pop. */
                sorted[i] = heap.entries[0];
                heap.entries[0] = heap.entries[--heap.count];
                if (heap.count > 0)
                {
                    int idx = 0;
                    for (;;)
                    {
                        int left  = 2 * idx + 1;
                        int right = 2 * idx + 2;
                        int largest = idx;
                        if (left  < heap.count && heap.entries[left ].distance > heap.entries[largest].distance) largest = left;
                        if (right < heap.count && heap.entries[right].distance > heap.entries[largest].distance) largest = right;
                        if (largest == idx) break;
                        heap_swap(&heap.entries[idx], &heap.entries[largest]);
                        idx = largest;
                    }
                }
            }

            state = (SimTopKState *) MemoryContextAllocZero(mctx, sizeof(SimTopKState));
            state->sorted = sorted;
            state->num_results = n;
            state->current = 0;
        }

        if (get_call_result_type(fcinfo, NULL, &state->tupdesc) != TYPEFUNC_COMPOSITE)
            ereport(ERROR,
                    (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                     errmsg("function returning record called in context that cannot accept type record")));
        BlessTupleDesc(state->tupdesc);

        funcctx->user_fctx = state;
        (void) ret;
        MemoryContextSwitchTo(oldctx);
    }

    funcctx = SRF_PERCALL_SETUP();
    state = (SimTopKState *) funcctx->user_fctx;

    if (state->current < state->num_results)
    {
        TopKEntry *e = &state->sorted[state->current++];
        Datum      values[3];
        bool       nulls[3] = {false, false, false};
        HeapTuple  tuple;
        Datum      result;
        bytea     *hash_out;

        /* Per-call bytea wrapper for the hash — palloc'd in the per-call
         * context (current default). The raw 32 bytes are copied from the
         * mctx-resident state->sorted entry. */
        hash_out = (bytea *) palloc(VARHDRSZ + SIMTOPK_HASH_LEN);
        SET_VARSIZE(hash_out, VARHDRSZ + SIMTOPK_HASH_LEN);
        memcpy(VARDATA(hash_out), e->entity_hash, SIMTOPK_HASH_LEN);

        values[0] = Int32GetDatum(e->entity_type_id);
        values[1] = PointerGetDatum(hash_out);
        values[2] = Float8GetDatum(e->distance);

        tuple = heap_form_tuple(state->tupdesc, values, nulls);
        result = HeapTupleGetDatum(tuple);
        SRF_RETURN_NEXT(funcctx, result);
    }

    SRF_RETURN_DONE(funcctx);
}
