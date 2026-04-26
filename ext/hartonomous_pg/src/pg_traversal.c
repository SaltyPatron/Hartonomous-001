#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "executor/spi.h"
#include "utils/builtins.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "catalog/pg_type.h"
#include "access/htup_details.h"

extern int hartonomous_max_traversal_results;

PG_FUNCTION_INFO_V1(pg_neighbors);
PG_FUNCTION_INFO_V1(pg_traverse_astar);

/* ── BFS Neighbors ─────────────────────────────────────────── */

typedef struct BfsResult
{
    int64   entity_id;
    int64   edge_id;
    int32   edge_type_id;
    int32   depth;
    int64  *path;
    int     path_len;
} BfsResult;

typedef struct BfsQueueEntry
{
    int64   entity_id;
    int32   depth;
    int64  *path;
    int     path_len;
} BfsQueueEntry;

typedef struct BfsState
{
    BfsResult *results;
    int         num_results;
    int         current;
    TupleDesc   tupdesc;
} BfsState;

typedef struct VisitedEntry
{
    int64 entity_id;
    char  status;
} VisitedEntry;

Datum
pg_neighbors(PG_FUNCTION_ARGS)
{
    FuncCallContext *funcctx;
    BfsState        *state;

    if (SRF_IS_FIRSTCALL())
    {
        MemoryContext   oldctx;
        int64           seed_id;
        int32           edge_type_filter;
        bool            edge_type_is_null;
        int32           max_hops;
        int             max_results;
        HTAB           *visited;
        HASHCTL         hctl;
        BfsQueueEntry  *queue;
        int             queue_head, queue_tail, queue_cap;
        BfsResult      *results;
        int             num_results, results_cap;
        SPIPlanPtr      plan;
        Oid             argtypes[2];

        funcctx = SRF_FIRSTCALL_INIT();
        oldctx = MemoryContextSwitchTo(funcctx->multi_call_memory_ctx);

        seed_id = PG_GETARG_INT64(0);
        if (PG_ARGISNULL(1))
        {
            edge_type_filter = 0;
            edge_type_is_null = true;
        }
        else
        {
            edge_type_filter = PG_GETARG_INT32(1);
            edge_type_is_null = false;
        }
        max_hops = PG_ARGISNULL(2) ? 1 : PG_GETARG_INT32(2);

        if (max_hops < 1)
            max_hops = 1;
        if (max_hops > 10)
            ereport(ERROR,
                    (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                     errmsg("max_hops must be 1..10, got %d", max_hops)));

        max_results = hartonomous_max_traversal_results;

        memset(&hctl, 0, sizeof(hctl));
        hctl.keysize = sizeof(int64);
        hctl.entrysize = sizeof(VisitedEntry);
        hctl.hcxt = funcctx->multi_call_memory_ctx;
        visited = hash_create("bfs_visited", 1024, &hctl,
                              HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

        queue_cap = 1024;
        queue = palloc(sizeof(BfsQueueEntry) * queue_cap);
        queue_head = 0;
        queue_tail = 0;

        results_cap = 256;
        results = palloc(sizeof(BfsResult) * results_cap);
        num_results = 0;

        /* Seed */
        {
            bool found;
            hash_search(visited, &seed_id, HASH_ENTER, &found);
        }
        queue[queue_tail].entity_id = seed_id;
        queue[queue_tail].depth = 0;
        queue[queue_tail].path = palloc(sizeof(int64));
        queue[queue_tail].path[0] = seed_id;
        queue[queue_tail].path_len = 1;
        queue_tail++;

        SPI_connect();

        argtypes[0] = INT8OID;
        argtypes[1] = INT4OID;
        plan = SPI_prepare(
            "SELECT em2.entity_id, em1.edge_id, e.edge_type_id "
            "FROM substrate.edge_member em1 "
            "JOIN substrate.edge e ON e.id = em1.edge_id "
            "JOIN substrate.edge_member em2 ON em2.edge_id = em1.edge_id "
            "  AND em2.entity_id != $1 "
            "WHERE em1.entity_id = $1 "
            "  AND ($2::int IS NULL OR e.edge_type_id = $2)",
            2, argtypes
        );

        if (plan == NULL)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("SPI_prepare failed for neighbors query")));

        while (queue_head < queue_tail && num_results < max_results)
        {
            BfsQueueEntry cur = queue[queue_head++];
            Datum   args[2];
            char    nulls[2];
            int     ret, row;

            if (cur.depth >= max_hops)
                continue;

            args[0] = Int64GetDatum(cur.entity_id);
            args[1] = Int32GetDatum(edge_type_filter);
            nulls[0] = ' ';
            nulls[1] = edge_type_is_null ? 'n' : ' ';

            ret = SPI_execute_plan(plan, args, nulls, true, 0);
            if (ret != SPI_OK_SELECT)
            {
                if (SPI_tuptable != NULL)
                    SPI_freetuptable(SPI_tuptable);
                continue;
            }

            for (row = 0; row < (int)SPI_processed && num_results < max_results; row++)
            {
                HeapTuple   tuple = SPI_tuptable->vals[row];
                TupleDesc   spi_tupdesc = SPI_tuptable->tupdesc;
                bool        isnull;
                int64       nbr_entity_id;
                int64       nbr_edge_id;
                int32       nbr_edge_type_id;
                bool        found;
                int64      *new_path;

                nbr_entity_id = DatumGetInt64(SPI_getbinval(tuple, spi_tupdesc, 1, &isnull));
                if (isnull) continue;
                nbr_edge_id = DatumGetInt64(SPI_getbinval(tuple, spi_tupdesc, 2, &isnull));
                if (isnull) continue;
                nbr_edge_type_id = DatumGetInt32(SPI_getbinval(tuple, spi_tupdesc, 3, &isnull));
                if (isnull) continue;

                hash_search(visited, &nbr_entity_id, HASH_ENTER, &found);
                if (found)
                    continue;

                new_path = palloc(sizeof(int64) * (cur.path_len + 1));
                memcpy(new_path, cur.path, sizeof(int64) * cur.path_len);
                new_path[cur.path_len] = nbr_entity_id;

                if (num_results >= results_cap)
                {
                    results_cap *= 2;
                    results = repalloc(results, sizeof(BfsResult) * results_cap);
                }
                results[num_results].entity_id = nbr_entity_id;
                results[num_results].edge_id = nbr_edge_id;
                results[num_results].edge_type_id = nbr_edge_type_id;
                results[num_results].depth = cur.depth + 1;
                results[num_results].path = new_path;
                results[num_results].path_len = cur.path_len + 1;
                num_results++;

                if (cur.depth + 1 < max_hops)
                {
                    if (queue_tail >= queue_cap)
                    {
                        queue_cap *= 2;
                        queue = repalloc(queue, sizeof(BfsQueueEntry) * queue_cap);
                    }
                    queue[queue_tail].entity_id = nbr_entity_id;
                    queue[queue_tail].depth = cur.depth + 1;
                    queue[queue_tail].path = new_path;
                    queue[queue_tail].path_len = cur.path_len + 1;
                    queue_tail++;
                }
            }

            /* Release tuptable memory before next SPI_execute_plan. Without this,
             * the SPI procedure context accumulates one tuptable per iteration and
             * exhausts memory when the BFS touches thousands of nodes (high-degree
             * seeds at max_hops >= 2). */
            if (SPI_tuptable != NULL)
                SPI_freetuptable(SPI_tuptable);
        }

        SPI_finish();

        state = palloc(sizeof(BfsState));
        state->results = results;
        state->num_results = num_results;
        state->current = 0;

        if (get_call_result_type(fcinfo, NULL, &state->tupdesc) != TYPEFUNC_COMPOSITE)
            ereport(ERROR,
                    (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                     errmsg("function returning record called in context that cannot accept type record")));
        BlessTupleDesc(state->tupdesc);

        funcctx->user_fctx = state;
        MemoryContextSwitchTo(oldctx);
    }

    funcctx = SRF_PERCALL_SETUP();
    state = (BfsState *)funcctx->user_fctx;

    if (state->current < state->num_results)
    {
        BfsResult  *r = &state->results[state->current++];
        Datum       values[5];
        bool        nulls[5] = {false, false, false, false, false};
        HeapTuple   tuple;
        Datum       result;
        ArrayType  *path_arr;
        Datum      *path_datums;
        int         i;

        values[0] = Int64GetDatum(r->entity_id);
        values[1] = Int64GetDatum(r->edge_id);
        values[2] = Int32GetDatum(r->edge_type_id);
        values[3] = Int32GetDatum(r->depth);

        path_datums = palloc(sizeof(Datum) * r->path_len);
        for (i = 0; i < r->path_len; i++)
            path_datums[i] = Int64GetDatum(r->path[i]);
        path_arr = construct_array(path_datums, r->path_len, INT8OID, 8, true, 'd');
        values[4] = PointerGetDatum(path_arr);

        tuple = heap_form_tuple(state->tupdesc, values, nulls);
        result = HeapTupleGetDatum(tuple);
        SRF_RETURN_NEXT(funcctx, result);
    }

    SRF_RETURN_DONE(funcctx);
}

/* ── A* Traversal ──────────────────────────────────────────── */

typedef struct AstarNode
{
    double  cost;
    int64   entity_id;
    int64  *entity_path;
    int64  *edge_path;
    int     path_len;
    int     depth;
} AstarNode;

typedef struct AstarHeap
{
    AstarNode  *nodes;
    int         count;
    int         capacity;
} AstarHeap;

static void
heap_push(AstarHeap *h, AstarNode node)
{
    int idx, parent;

    if (h->count >= h->capacity)
    {
        h->capacity *= 2;
        h->nodes = repalloc(h->nodes, sizeof(AstarNode) * h->capacity);
    }

    idx = h->count++;
    h->nodes[idx] = node;

    while (idx > 0)
    {
        parent = (idx - 1) / 2;
        if (h->nodes[parent].cost <= h->nodes[idx].cost)
            break;
        {
            AstarNode tmp = h->nodes[parent];
            h->nodes[parent] = h->nodes[idx];
            h->nodes[idx] = tmp;
        }
        idx = parent;
    }
}

static AstarNode
heap_pop(AstarHeap *h)
{
    AstarNode result = h->nodes[0];
    int idx = 0;

    h->nodes[0] = h->nodes[--h->count];

    for (;;)
    {
        int left = 2 * idx + 1;
        int right = 2 * idx + 2;
        int smallest = idx;

        if (left < h->count && h->nodes[left].cost < h->nodes[smallest].cost)
            smallest = left;
        if (right < h->count && h->nodes[right].cost < h->nodes[smallest].cost)
            smallest = right;
        if (smallest == idx)
            break;
        {
            AstarNode tmp = h->nodes[idx];
            h->nodes[idx] = h->nodes[smallest];
            h->nodes[smallest] = tmp;
        }
        idx = smallest;
    }

    return result;
}

typedef struct AstarResult
{
    int64   target_entity_id;
    double  cost;
    int64  *entity_path;
    int64  *edge_path;
    int     path_len;
} AstarResult;

typedef struct AstarState
{
    AstarResult *results;
    int          num_results;
    int          current;
    TupleDesc    tupdesc;
} AstarState;

typedef struct CostEntry
{
    int64  entity_id;
    double best_cost;
} CostEntry;

Datum
pg_traverse_astar(PG_FUNCTION_ARGS)
{
    FuncCallContext *funcctx;
    AstarState      *state;

    if (SRF_IS_FIRSTCALL())
    {
        MemoryContext   oldctx;
        int64           seed_id;
        int32           target_type_id;
        int32           arena_id;
        int32           max_depth;
        int32           max_results_arg;
        int32           edge_type_filter;
        bool            edge_type_filter_is_null;
        double          p_min_mu;
        bool            p_min_mu_is_null;
        AstarHeap       heap;
        HTAB           *best_costs;
        HASHCTL         hctl;
        AstarResult    *results;
        int             num_results, results_cap;
        SPIPlanPtr      nbr_plan;
        SPIPlanPtr      sig_plan;
        Oid             argtypes_nbr[2];
        Oid             argtypes_sig[2];

        funcctx = SRF_FIRSTCALL_INIT();
        oldctx = MemoryContextSwitchTo(funcctx->multi_call_memory_ctx);

        seed_id = PG_GETARG_INT64(0);
        target_type_id = PG_GETARG_INT32(1);
        arena_id = PG_GETARG_INT32(2);
        max_depth = PG_ARGISNULL(3) ? 5 : PG_GETARG_INT32(3);
        max_results_arg = PG_ARGISNULL(4) ? 100 : PG_GETARG_INT32(4);

        /* edge_type_filter: optional int — restrict traversal to edges of a single type
         * (inference.md Step 1.1 "follow edges of relevant types via edge.edge_type_id").
         * NULL = no edge-type filter (all edge types traversable). */
        if (PG_NARGS() > 5 && !PG_ARGISNULL(5))
        {
            edge_type_filter = PG_GETARG_INT32(5);
            edge_type_filter_is_null = false;
        }
        else
        {
            edge_type_filter = 0;
            edge_type_filter_is_null = true;
        }

        /* p_min_mu: optional double — significance threshold (inference.md Step 1.3
         * "Connected entities above the significance threshold (p_min_mu)"). Edges with
         * arena mu below this are not traversed. NULL = no threshold (all edges). */
        if (PG_NARGS() > 6 && !PG_ARGISNULL(6))
        {
            p_min_mu = PG_GETARG_FLOAT8(6);
            p_min_mu_is_null = false;
        }
        else
        {
            p_min_mu = 0.0;
            p_min_mu_is_null = true;
        }

        if (max_depth < 1) max_depth = 1;
        if (max_depth > 10) max_depth = 10;
        if (max_results_arg < 1) max_results_arg = 1;
        if (max_results_arg > hartonomous_max_traversal_results)
            max_results_arg = hartonomous_max_traversal_results;

        memset(&hctl, 0, sizeof(hctl));
        hctl.keysize = sizeof(int64);
        hctl.entrysize = sizeof(CostEntry);
        hctl.hcxt = funcctx->multi_call_memory_ctx;
        best_costs = hash_create("astar_costs", 1024, &hctl,
                                 HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

        heap.capacity = 256;
        heap.count = 0;
        heap.nodes = palloc(sizeof(AstarNode) * heap.capacity);

        results_cap = 64;
        results = palloc(sizeof(AstarResult) * results_cap);
        num_results = 0;

        /* Seed node */
        {
            AstarNode seed;
            CostEntry *ce;
            bool found;

            seed.cost = 0.0;
            seed.entity_id = seed_id;
            seed.entity_path = palloc(sizeof(int64));
            seed.entity_path[0] = seed_id;
            seed.edge_path = NULL;
            seed.path_len = 1;
            seed.depth = 0;
            heap_push(&heap, seed);

            ce = hash_search(best_costs, &seed_id, HASH_ENTER, &found);
            ce->best_cost = 0.0;
        }

        SPI_connect();

        /* Neighbor query: typed-edge filter per inference.md Step 1.1. $2 is the edge-type
         * filter (NULL = no filter). The entity_type_id of the neighbor is required for the
         * target-type match downstream. */
        argtypes_nbr[0] = INT8OID;
        argtypes_nbr[1] = INT4OID;
        nbr_plan = SPI_prepare(
            "SELECT em2.entity_id, em1.edge_id, ent.entity_type_id "
            "FROM substrate.edge_member em1 "
            "JOIN substrate.edge e ON e.id = em1.edge_id "
            "JOIN substrate.edge_member em2 ON em2.edge_id = em1.edge_id "
            "  AND em2.entity_id != $1 "
            "JOIN substrate.entity ent ON ent.id = em2.entity_id "
            "WHERE em1.entity_id = $1 "
            "  AND ($2::int IS NULL OR e.edge_type_id = $2)",
            2, argtypes_nbr
        );

        argtypes_sig[0] = INT8OID;
        argtypes_sig[1] = INT4OID;
        sig_plan = SPI_prepare(
            "SELECT COALESCE(s.mu, 1500.0) AS mu "
            "FROM substrate.significance s "
            "WHERE s.edge_id = $1 AND s.context_type_id = $2",
            2, argtypes_sig
        );

        while (heap.count > 0 && num_results < max_results_arg)
        {
            AstarNode cur = heap_pop(&heap);
            CostEntry *ce;
            bool found;
            Datum args[2];
            char nulls_arr[2];
            int ret, row;

            ce = hash_search(best_costs, &cur.entity_id, HASH_FIND, &found);
            if (found && ce->best_cost < cur.cost)
                continue;

            if (cur.depth >= max_depth)
                continue;

            args[0] = Int64GetDatum(cur.entity_id);
            nulls_arr[0] = ' ';
            args[1] = Int32GetDatum(edge_type_filter);
            nulls_arr[1] = edge_type_filter_is_null ? 'n' : ' ';

            ret = SPI_execute_plan(nbr_plan, args, nulls_arr, true, 0);
            if (ret != SPI_OK_SELECT)
            {
                if (SPI_tuptable != NULL)
                    SPI_freetuptable(SPI_tuptable);
                continue;
            }

            {
                SPITupleTable *nbr_tuptable = SPI_tuptable;
                uint64         nbr_processed = SPI_processed;

                for (row = 0; row < (int)nbr_processed; row++)
                {
                    HeapTuple   tuple = nbr_tuptable->vals[row];
                    TupleDesc   spi_tupdesc = nbr_tuptable->tupdesc;
                    bool        isnull;
                    int64       nbr_id, nbr_edge_id;
                    int32       nbr_type_id;
                    double      edge_mu, edge_cost, new_cost;
                    CostEntry  *nbr_ce;
                    AstarNode   next;
                    int         i;

                    nbr_id = DatumGetInt64(SPI_getbinval(tuple, spi_tupdesc, 1, &isnull));
                    if (isnull) continue;
                    nbr_edge_id = DatumGetInt64(SPI_getbinval(tuple, spi_tupdesc, 2, &isnull));
                    if (isnull) continue;
                    nbr_type_id = DatumGetInt32(SPI_getbinval(tuple, spi_tupdesc, 3, &isnull));
                    if (isnull) continue;

                    /* Get significance for this edge in the arena.
                     * SPI_execute_plan here mutates SPI_tuptable — read value, then free
                     * immediately so the BFS/A* outer loop doesn't accumulate sig-query
                     * tuptables across thousands of neighbor hops. */
                    {
                        Datum sig_args[2];
                        char sig_nulls[2] = {' ', ' '};
                        int sig_ret;

                        sig_args[0] = Int64GetDatum(nbr_edge_id);
                        sig_args[1] = Int32GetDatum(arena_id);
                        sig_ret = SPI_execute_plan(sig_plan, sig_args, sig_nulls, true, 1);

                        if (sig_ret == SPI_OK_SELECT && SPI_processed > 0)
                        {
                            edge_mu = DatumGetFloat8(
                                SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull));
                            if (isnull) edge_mu = 1500.0;
                        }
                        else
                        {
                            edge_mu = 1500.0;
                        }
                        if (SPI_tuptable != NULL)
                            SPI_freetuptable(SPI_tuptable);
                    }

                /* Significance threshold prune: inference.md Step 1.3 — entities below
                 * p_min_mu do not enter the candidate pool. Applied to edge mu (the
                 * Glicko-2 rating of this specific relationship in this arena). */
                if (!p_min_mu_is_null && edge_mu < p_min_mu)
                    continue;

                edge_cost = 1.0 / edge_mu;
                new_cost = cur.cost + edge_cost;

                nbr_ce = hash_search(best_costs, &nbr_id, HASH_ENTER, &found);
                if (found && nbr_ce->best_cost <= new_cost)
                    continue;
                nbr_ce->best_cost = new_cost;

                next.cost = new_cost;
                next.entity_id = nbr_id;
                next.depth = cur.depth + 1;
                next.path_len = cur.path_len + 1;
                /* Allocate path arrays in multi_call_memory_ctx so they survive
                 * SPI_finish at the end of the FIRSTCALL setup. SPI_connect
                 * switches CurrentMemoryContext to its private context; any
                 * palloc here would be freed by SPI_finish, leaving dangling
                 * pointers in results[] that the SRF_PERCALL_SETUP read path
                 * dereferences. The use-after-free is silent under the simple
                 * query protocol (psql) because the freed memory is rarely
                 * overwritten before SRF_RETURN_NEXT runs, but the extended
                 * query protocol (Npgsql) reliably trips a SIGSEGV. */
                next.entity_path = MemoryContextAlloc(funcctx->multi_call_memory_ctx,
                                                      sizeof(int64) * next.path_len);
                memcpy(next.entity_path, cur.entity_path, sizeof(int64) * cur.path_len);
                next.entity_path[cur.path_len] = nbr_id;

                next.edge_path = MemoryContextAlloc(funcctx->multi_call_memory_ctx,
                                                    sizeof(int64) * cur.path_len);
                if (cur.edge_path && cur.path_len > 1)
                    memcpy(next.edge_path, cur.edge_path, sizeof(int64) * (cur.path_len - 1));
                next.edge_path[cur.path_len - 1] = nbr_edge_id;

                heap_push(&heap, next);

                if (nbr_type_id == target_type_id)
                {
                    if (num_results >= results_cap)
                    {
                        results_cap *= 2;
                        results = repalloc(results, sizeof(AstarResult) * results_cap);
                    }
                    results[num_results].target_entity_id = nbr_id;
                    results[num_results].cost = new_cost;
                    results[num_results].entity_path = next.entity_path;
                    results[num_results].edge_path = next.edge_path;
                    results[num_results].path_len = next.path_len;
                    num_results++;

                    for (i = 0; i < num_results - 1; i++)
                    {
                        if (results[i].cost > results[num_results - 1].cost)
                        {
                            AstarResult tmp = results[i];
                            results[i] = results[num_results - 1];
                            results[num_results - 1] = tmp;
                        }
                    }
                }
                }

                /* Release neighbor tuptable before the next while-iteration pops another node.
                 * Without this free the SPI procedure context accumulates one tuptable per
                 * A*-hop; at high-degree seeds the process segfaults from exhaustion. */
                SPI_freetuptable(nbr_tuptable);
            }
        }

        SPI_finish();

        state = palloc(sizeof(AstarState));
        state->results = results;
        state->num_results = num_results;
        state->current = 0;

        if (get_call_result_type(fcinfo, NULL, &state->tupdesc) != TYPEFUNC_COMPOSITE)
            ereport(ERROR,
                    (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                     errmsg("function returning record called in context that cannot accept type record")));
        BlessTupleDesc(state->tupdesc);

        funcctx->user_fctx = state;
        MemoryContextSwitchTo(oldctx);
    }

    funcctx = SRF_PERCALL_SETUP();
    state = (AstarState *)funcctx->user_fctx;

    if (state->current < state->num_results)
    {
        AstarResult *r = &state->results[state->current++];
        Datum        values[4];
        bool         nulls_arr[4] = {false, false, false, false};
        HeapTuple    tuple;
        Datum        result;
        ArrayType   *entity_arr, *edge_arr;
        Datum       *path_datums;
        int          i;

        values[0] = Int64GetDatum(r->target_entity_id);
        values[1] = Float8GetDatum(r->cost);

        path_datums = palloc(sizeof(Datum) * r->path_len);
        for (i = 0; i < r->path_len; i++)
            path_datums[i] = Int64GetDatum(r->entity_path[i]);
        entity_arr = construct_array(path_datums, r->path_len, INT8OID, 8, true, 'd');
        values[2] = PointerGetDatum(entity_arr);

        if (r->path_len > 1 && r->edge_path)
        {
            path_datums = palloc(sizeof(Datum) * (r->path_len - 1));
            for (i = 0; i < r->path_len - 1; i++)
                path_datums[i] = Int64GetDatum(r->edge_path[i]);
            edge_arr = construct_array(path_datums, r->path_len - 1, INT8OID, 8, true, 'd');
        }
        else
        {
            edge_arr = construct_array(NULL, 0, INT8OID, 8, true, 'd');
            nulls_arr[3] = true;
        }
        values[3] = PointerGetDatum(edge_arr);

        tuple = heap_form_tuple(state->tupdesc, values, nulls_arr);
        result = HeapTupleGetDatum(tuple);
        SRF_RETURN_NEXT(funcctx, result);
    }

    SRF_RETURN_DONE(funcctx);
}
