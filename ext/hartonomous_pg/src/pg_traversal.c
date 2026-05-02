/*
 * pg_traversal.c — substrate A* and BFS over hash-as-PK substrate.
 *
 * Phase C unification: substrate.entity has hash-only PK. substrate.edge
 * still keeps composite (edge_type_id, hash) — edge identity is structural
 * (different edge_type_id with same participant hashes is a different
 * relation). substrate.edge_member references entities by hash only:
 * (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position).
 *
 * This file implements the substrate's invention: Glicko-2-rated A* over
 * typed edges, replacing transformer matmul. Bounded indexed traversal,
 * O(K log N). Edge cost = 1 / edge_mu where edge_mu is read from
 * substrate.edge_significance for the requested arena, falling back at
 * COALESCE level to provenance_edge_authority.initial_mu, then to the
 * formula prior  p.initial_mu * et.semantic_weight * p.derivation_decay
 * — never to a flat 1500.0 default.
 *
 * Memory discipline: every path payload allocated for the result set lives
 * in funcctx->multi_call_memory_ctx. SPI_connect() flips CurrentMemoryContext
 * to the SPI procedure context which SPI_finish() frees at the end of
 * SRF_IS_FIRSTCALL — anything palloc'd inside the SPI block and read again
 * from the SRF_PERCALL_SETUP path would be a use-after-free.
 */

#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "executor/spi.h"
#include "utils/builtins.h"
#include "utils/array.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "catalog/pg_type.h"
#include "access/htup_details.h"

#include <math.h>
#include <string.h>

extern int  hartonomous_max_traversal_results;
extern bool hartonomous_traversal_trace;

#define SUBSTRATE_HASH_LEN 32

PG_FUNCTION_INFO_V1(pg_neighbors);
PG_FUNCTION_INFO_V1(pg_traverse_astar);

/*
 * Hash-only entity key (Phase C). 32 bytes BLAKE3. Padded to 8-byte boundary
 * for HASH_BLOBS (memcmp / hashing of the raw struct bytes). Trailing
 * `_pad` bytes zeroed at construction so two keys with identical contents
 * hash to the same bucket regardless of stack-allocation pattern.
 */
typedef struct EntityKey
{
    uint8_t  hash[SUBSTRATE_HASH_LEN];
} EntityKey;

/*
 * Edge key carried inside path arrays. (edge_type_id, edge_hash) is the
 * composite PK of substrate.edge — edge identity is still structural.
 */
typedef struct EdgeKey
{
    int32_t  type_id;
    uint8_t  hash[SUBSTRATE_HASH_LEN];
    uint8_t  _pad[4];                 /* keep sizeof(EdgeKey) % 8 == 0 */
} EdgeKey;

static inline void
make_entity_key(EntityKey *k, const uint8_t *hash)
{
    memset(k, 0, sizeof(*k));
    memcpy(k->hash, hash, SUBSTRATE_HASH_LEN);
}

static inline void
make_edge_key(EdgeKey *k, int32_t type_id, const uint8_t *hash)
{
    memset(k, 0, sizeof(*k));
    k->type_id = type_id;
    memcpy(k->hash, hash, SUBSTRATE_HASH_LEN);
}

/*
 * Extract a 32-byte BLAKE3 hash from a SPI-returned bytea Datum. The substrate
 * domain hash_value is BYTEA CHECK (octet_length = 32), so any value coming
 * out of substrate.entity / substrate.edge is exactly 32 bytes.
 */
static inline bool
extract_hash32(Datum d, bool isnull, uint8_t *out)
{
    bytea *raw;
    int    len;

    if (isnull)
        return false;

    raw = DatumGetByteaPP(d);
    len = VARSIZE_ANY_EXHDR(raw);
    if (len != SUBSTRATE_HASH_LEN)
        return false;
    memcpy(out, VARDATA_ANY(raw), SUBSTRATE_HASH_LEN);
    return true;
}

/* ── BFS Neighbors (hash-only key form) ────────────────────────────── */

typedef struct BfsResult
{
    uint8_t target_hash[SUBSTRATE_HASH_LEN];
    int32   edge_etid;
    uint8_t edge_hash[SUBSTRATE_HASH_LEN];
    int32   depth;
    EntityKey *entity_path;          /* depth+1 entries */
    int32   path_len;
} BfsResult;

typedef struct BfsQueueEntry
{
    EntityKey  key;
    int32      depth;
    EntityKey *entity_path;
    int32      path_len;
} BfsQueueEntry;

typedef struct BfsState
{
    BfsResult *results;
    int        num_results;
    int        current;
    TupleDesc  tupdesc;
} BfsState;

typedef struct VisitedEntry
{
    EntityKey key;
    char      status;
} VisitedEntry;

/*
 * Construct a BYTEA[] datum from a path of EntityKey hashes. Each element is
 * a fresh 32-byte bytea palloc'd in mctx.
 */
static ArrayType *
construct_hash_array(MemoryContext mctx, const EntityKey *keys, int n)
{
    Datum     *vals;
    ArrayType *arr;
    MemoryContext old;
    int        i;

    old = MemoryContextSwitchTo(mctx);
    vals = (Datum *) palloc(sizeof(Datum) * n);
    for (i = 0; i < n; i++)
    {
        bytea *b = (bytea *) palloc(VARHDRSZ + SUBSTRATE_HASH_LEN);
        SET_VARSIZE(b, VARHDRSZ + SUBSTRATE_HASH_LEN);
        memcpy(VARDATA(b), keys[i].hash, SUBSTRATE_HASH_LEN);
        vals[i] = PointerGetDatum(b);
    }
    arr = construct_array(vals, n, BYTEAOID, -1, false, 'i');
    MemoryContextSwitchTo(old);
    return arr;
}

static ArrayType *
construct_edge_hash_array(MemoryContext mctx, const EdgeKey *keys, int n)
{
    Datum     *vals;
    ArrayType *arr;
    MemoryContext old;
    int        i;

    old = MemoryContextSwitchTo(mctx);
    if (n == 0)
    {
        arr = construct_empty_array(BYTEAOID);
        MemoryContextSwitchTo(old);
        return arr;
    }
    vals = (Datum *) palloc(sizeof(Datum) * n);
    for (i = 0; i < n; i++)
    {
        bytea *b = (bytea *) palloc(VARHDRSZ + SUBSTRATE_HASH_LEN);
        SET_VARSIZE(b, VARHDRSZ + SUBSTRATE_HASH_LEN);
        memcpy(VARDATA(b), keys[i].hash, SUBSTRATE_HASH_LEN);
        vals[i] = PointerGetDatum(b);
    }
    arr = construct_array(vals, n, BYTEAOID, -1, false, 'i');
    MemoryContextSwitchTo(old);
    return arr;
}

Datum
pg_neighbors(PG_FUNCTION_ARGS)
{
    FuncCallContext *funcctx;
    BfsState        *state;

    if (SRF_IS_FIRSTCALL())
    {
        MemoryContext   oldctx;
        bytea          *seed_hash_in;
        uint8_t         seed_hash[SUBSTRATE_HASH_LEN];
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
        EntityKey       seed_key;

        funcctx = SRF_FIRSTCALL_INIT();
        oldctx = MemoryContextSwitchTo(funcctx->multi_call_memory_ctx);

        if (PG_ARGISNULL(0))
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("seed_entity_hash must not be NULL")));

        seed_hash_in = PG_GETARG_BYTEA_PP(0);
        if (VARSIZE_ANY_EXHDR(seed_hash_in) != SUBSTRATE_HASH_LEN)
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                     errmsg("seed_entity_hash must be exactly %d bytes",
                            SUBSTRATE_HASH_LEN)));
        memcpy(seed_hash, VARDATA_ANY(seed_hash_in), SUBSTRATE_HASH_LEN);

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
        hctl.keysize = sizeof(EntityKey);
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
        make_entity_key(&seed_key, seed_hash);
        {
            bool found;
            VisitedEntry *ve = hash_search(visited, &seed_key, HASH_ENTER, &found);
            (void) ve;
        }
        queue[queue_tail].key = seed_key;
        queue[queue_tail].depth = 0;
        queue[queue_tail].entity_path = MemoryContextAlloc(funcctx->multi_call_memory_ctx,
                                                          sizeof(EntityKey));
        queue[queue_tail].entity_path[0] = seed_key;
        queue[queue_tail].path_len = 1;
        queue_tail++;

        SPI_connect();

        /*
         * Bulk neighbor expansion query, hash-only entity reference.
         * One SPI per popped node.
         * $1 = source entity_hash (BYTEA),
         * $2 = edge_type_filter (NULL = any).
         *
         * Returns every co-member of every edge in which the source entity
         * participates. Self-rows (same entity in any role) filtered out.
         */
        argtypes[0] = BYTEAOID;
        argtypes[1] = INT4OID;
        plan = SPI_prepare(
            "SELECT em2.entity_hash, "
            "       em1.edge_type_id, em1.edge_hash "
            "FROM substrate.edge_member em1 "
            "JOIN substrate.edge_member em2 "
            "  ON em2.edge_type_id = em1.edge_type_id "
            " AND em2.edge_hash    = em1.edge_hash "
            "WHERE em1.entity_hash = $1 "
            "  AND em2.entity_hash <> em1.entity_hash "
            "  AND ($2::int IS NULL OR em1.edge_type_id = $2)",
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
            bytea  *src_hash_arg;
            int     ret, row;

            if (cur.depth >= max_hops)
                continue;

            src_hash_arg = (bytea *) palloc(VARHDRSZ + SUBSTRATE_HASH_LEN);
            SET_VARSIZE(src_hash_arg, VARHDRSZ + SUBSTRATE_HASH_LEN);
            memcpy(VARDATA(src_hash_arg), cur.key.hash, SUBSTRATE_HASH_LEN);

            args[0] = PointerGetDatum(src_hash_arg);
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
                uint8_t     nbr_hash[SUBSTRATE_HASH_LEN];
                int32       edge_etid;
                uint8_t     edge_hash[SUBSTRATE_HASH_LEN];
                EntityKey   nbr_key;
                bool        found;
                EntityKey  *new_path;
                Datum       d;

                d = SPI_getbinval(tuple, spi_tupdesc, 1, &isnull);
                if (!extract_hash32(d, isnull, nbr_hash)) continue;
                d = SPI_getbinval(tuple, spi_tupdesc, 2, &isnull);
                if (isnull) continue;
                edge_etid = DatumGetInt32(d);
                d = SPI_getbinval(tuple, spi_tupdesc, 3, &isnull);
                if (!extract_hash32(d, isnull, edge_hash)) continue;

                make_entity_key(&nbr_key, nbr_hash);

                hash_search(visited, &nbr_key, HASH_ENTER, &found);
                if (found)
                    continue;

                new_path = MemoryContextAlloc(funcctx->multi_call_memory_ctx,
                                              sizeof(EntityKey) * (cur.path_len + 1));
                memcpy(new_path, cur.entity_path, sizeof(EntityKey) * cur.path_len);
                new_path[cur.path_len] = nbr_key;

                if (num_results >= results_cap)
                {
                    results_cap *= 2;
                    results = repalloc(results, sizeof(BfsResult) * results_cap);
                }
                memcpy(results[num_results].target_hash, nbr_hash, SUBSTRATE_HASH_LEN);
                results[num_results].edge_etid = edge_etid;
                memcpy(results[num_results].edge_hash, edge_hash, SUBSTRATE_HASH_LEN);
                results[num_results].depth = cur.depth + 1;
                results[num_results].entity_path = new_path;
                results[num_results].path_len = cur.path_len + 1;
                num_results++;

                if (cur.depth + 1 < max_hops)
                {
                    if (queue_tail >= queue_cap)
                    {
                        queue_cap *= 2;
                        queue = repalloc(queue, sizeof(BfsQueueEntry) * queue_cap);
                    }
                    queue[queue_tail].key = nbr_key;
                    queue[queue_tail].depth = cur.depth + 1;
                    queue[queue_tail].entity_path = new_path;
                    queue[queue_tail].path_len = cur.path_len + 1;
                    queue_tail++;
                }
            }

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
        bytea      *target_hash_b;
        bytea      *edge_hash_b;
        ArrayType  *hash_arr;

        target_hash_b = (bytea *) palloc(VARHDRSZ + SUBSTRATE_HASH_LEN);
        SET_VARSIZE(target_hash_b, VARHDRSZ + SUBSTRATE_HASH_LEN);
        memcpy(VARDATA(target_hash_b), r->target_hash, SUBSTRATE_HASH_LEN);

        edge_hash_b = (bytea *) palloc(VARHDRSZ + SUBSTRATE_HASH_LEN);
        SET_VARSIZE(edge_hash_b, VARHDRSZ + SUBSTRATE_HASH_LEN);
        memcpy(VARDATA(edge_hash_b), r->edge_hash, SUBSTRATE_HASH_LEN);

        hash_arr = construct_hash_array(CurrentMemoryContext, r->entity_path, r->path_len);

        values[0] = PointerGetDatum(target_hash_b);
        values[1] = Int32GetDatum(r->edge_etid);
        values[2] = PointerGetDatum(edge_hash_b);
        values[3] = Int32GetDatum(r->depth);
        values[4] = PointerGetDatum(hash_arr);

        tuple = heap_form_tuple(state->tupdesc, values, nulls);
        result = HeapTupleGetDatum(tuple);
        SRF_RETURN_NEXT(funcctx, result);
    }

    SRF_RETURN_DONE(funcctx);
}

/* ── A* Traversal (hash-only entity key form) ────────────────────────── */

typedef struct AstarNode
{
    double      cost;            /* sum of 1/edge_mu along the path so far */
    EntityKey   key;
    EntityKey  *entity_path;     /* path_len entries */
    EdgeKey    *edge_path;       /* path_len - 1 entries (NULL for seed) */
    int         path_len;
    int         depth;
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
    uint8_t    target_hash[SUBSTRATE_HASH_LEN];
    int        depth;
    double     total_cost;       /* sum of 1/mu along the path */
    EntityKey *entity_path;      /* path_len entries */
    EdgeKey   *edge_path;        /* path_len - 1 entries */
    int        path_len;
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
    EntityKey  key;
    double     best_cost;
} CostEntry;

Datum
pg_traverse_astar(PG_FUNCTION_ARGS)
{
    FuncCallContext *funcctx;
    AstarState      *state;

    if (SRF_IS_FIRSTCALL())
    {
        MemoryContext   oldctx;
        bytea          *seed_hash_in;
        uint8_t         seed_hash[SUBSTRATE_HASH_LEN];
        int32           edge_type_filter;
        bool            edge_type_filter_is_null;
        int32           arena_id;
        int32           max_depth;
        int32           max_results_arg;
        double          p_min_mu;
        bool            p_min_mu_is_null;
        AstarHeap       heap;
        HTAB           *best_costs;
        HASHCTL         hctl;
        AstarResult    *results;
        int             num_results, results_cap;
        SPIPlanPtr      nbr_plan;
        Oid             argtypes_nbr[3];
        EntityKey       seed_key;

        funcctx = SRF_FIRSTCALL_INIT();
        oldctx = MemoryContextSwitchTo(funcctx->multi_call_memory_ctx);

        /* Required: seed_entity_hash, arena_id */
        if (PG_ARGISNULL(0))
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("seed_entity_hash must not be NULL")));

        seed_hash_in = PG_GETARG_BYTEA_PP(0);
        if (VARSIZE_ANY_EXHDR(seed_hash_in) != SUBSTRATE_HASH_LEN)
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                     errmsg("seed_entity_hash must be exactly %d bytes",
                            SUBSTRATE_HASH_LEN)));
        memcpy(seed_hash, VARDATA_ANY(seed_hash_in), SUBSTRATE_HASH_LEN);

        if (PG_ARGISNULL(1))
        {
            edge_type_filter = 0;
            edge_type_filter_is_null = true;
        }
        else
        {
            edge_type_filter = PG_GETARG_INT32(1);
            edge_type_filter_is_null = false;
        }

        if (PG_ARGISNULL(2))
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("arena_id must not be NULL")));
        arena_id = PG_GETARG_INT32(2);

        max_depth        = PG_ARGISNULL(3) ? 5   : PG_GETARG_INT32(3);
        max_results_arg  = PG_ARGISNULL(4) ? 100 : PG_GETARG_INT32(4);

        if (PG_NARGS() > 5 && !PG_ARGISNULL(5))
        {
            p_min_mu = PG_GETARG_FLOAT8(5);
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
        hctl.keysize = sizeof(EntityKey);
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
        make_entity_key(&seed_key, seed_hash);
        {
            AstarNode  seed;
            CostEntry *ce;
            bool       found;

            seed.cost = 0.0;
            seed.key = seed_key;
            seed.entity_path = MemoryContextAlloc(funcctx->multi_call_memory_ctx,
                                                  sizeof(EntityKey));
            seed.entity_path[0] = seed_key;
            seed.edge_path = NULL;
            seed.path_len = 1;
            seed.depth = 0;
            heap_push(&heap, seed);

            ce = hash_search(best_costs, &seed_key, HASH_ENTER, &found);
            ce->best_cost = 0.0;
        }

        if (hartonomous_traversal_trace)
        {
            char hex[9];
            for (int hb = 0; hb < 4; hb++)
            {
                static const char H[] = "0123456789abcdef";
                hex[hb * 2]     = H[(seed_hash[hb] >> 4) & 0xF];
                hex[hb * 2 + 1] = H[seed_hash[hb] & 0xF];
            }
            hex[8] = '\0';
            ereport(NOTICE,
                    (errmsg("traverse_astar: enter seed=%s arena=%d max_depth=%d max_results=%d",
                            hex, arena_id, max_depth, max_results_arg)));
        }

        SPI_connect();

        /*
         * Bulk neighbor + COALESCE-prior bulk JOIN. One SPI per popped node,
         * returning every co-member with the edge's effective μ in the
         * requested arena.
         *   1. substrate.edge_significance.mu
         *   2. provenance_edge_authority.initial_mu
         *   3. p.initial_mu * et.semantic_weight * p.derivation_decay
         *
         * $1 = source entity_hash (BYTEA),
         * $2 = edge_type_filter (NULL = any),
         * $3 = arena context_type_id.
         */
        argtypes_nbr[0] = BYTEAOID;
        argtypes_nbr[1] = INT4OID;
        argtypes_nbr[2] = INT4OID;
        nbr_plan = SPI_prepare(
            "SELECT em2.entity_hash, "
            "       em1.edge_type_id,   em1.edge_hash, "
            "       COALESCE( "
            "           s.mu, "
            "           pea.initial_mu, "
            "           p.initial_mu * et.semantic_weight * p.derivation_decay "
            "       ) AS edge_mu "
            "FROM substrate.edge_member em1 "
            "JOIN substrate.edge e "
            "  ON e.edge_type_id = em1.edge_type_id "
            " AND e.hash         = em1.edge_hash "
            "JOIN substrate.edge_type  et ON et.id = e.edge_type_id "
            "JOIN substrate.provenance p  ON p.id  = e.provenance_id "
            "LEFT JOIN substrate.provenance_edge_authority pea "
            "  ON pea.provenance_id = p.id "
            " AND pea.edge_type_id  = e.edge_type_id "
            "JOIN substrate.edge_member em2 "
            "  ON em2.edge_type_id = em1.edge_type_id "
            " AND em2.edge_hash    = em1.edge_hash "
            "LEFT JOIN substrate.edge_significance s "
            "  ON s.context_type_id = $3 "
            " AND s.edge_type_id    = em1.edge_type_id "
            " AND s.edge_hash       = em1.edge_hash "
            "WHERE em1.entity_hash = $1 "
            "  AND em2.entity_hash <> em1.entity_hash "
            "  AND ($2::int IS NULL OR e.edge_type_id = $2)",
            3, argtypes_nbr
        );

        if (nbr_plan == NULL)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("SPI_prepare failed for A* neighbor query")));

        while (heap.count > 0 && num_results < max_results_arg)
        {
            AstarNode    cur = heap_pop(&heap);
            CostEntry   *ce;
            bool         found;
            Datum        args[3];
            char         nulls_arr[3];
            int          ret, row;
            bytea       *src_hash_arg;

            ce = hash_search(best_costs, &cur.key, HASH_FIND, &found);
            if (found && ce->best_cost < cur.cost)
                continue;

            if (cur.depth >= max_depth)
                continue;

            src_hash_arg = (bytea *) palloc(VARHDRSZ + SUBSTRATE_HASH_LEN);
            SET_VARSIZE(src_hash_arg, VARHDRSZ + SUBSTRATE_HASH_LEN);
            memcpy(VARDATA(src_hash_arg), cur.key.hash, SUBSTRATE_HASH_LEN);

            args[0] = PointerGetDatum(src_hash_arg);
            nulls_arr[0] = ' ';
            args[1] = Int32GetDatum(edge_type_filter);
            nulls_arr[1] = edge_type_filter_is_null ? 'n' : ' ';
            args[2] = Int32GetDatum(arena_id);
            nulls_arr[2] = ' ';

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

                for (row = 0; row < (int) nbr_processed; row++)
                {
                    HeapTuple   tuple = nbr_tuptable->vals[row];
                    TupleDesc   spi_tupdesc = nbr_tuptable->tupdesc;
                    bool        isnull;
                    int32       edge_etid;
                    uint8_t     nbr_hash[SUBSTRATE_HASH_LEN];
                    uint8_t     edge_hash[SUBSTRATE_HASH_LEN];
                    EntityKey   nbr_key;
                    double      edge_mu, edge_cost, new_cost;
                    CostEntry  *nbr_ce;
                    AstarNode   next;
                    Datum       d;

                    d = SPI_getbinval(tuple, spi_tupdesc, 1, &isnull);
                    if (!extract_hash32(d, isnull, nbr_hash)) continue;
                    d = SPI_getbinval(tuple, spi_tupdesc, 2, &isnull);
                    if (isnull) continue;
                    edge_etid = DatumGetInt32(d);
                    d = SPI_getbinval(tuple, spi_tupdesc, 3, &isnull);
                    if (!extract_hash32(d, isnull, edge_hash)) continue;
                    d = SPI_getbinval(tuple, spi_tupdesc, 4, &isnull);
                    if (isnull) continue;
                    edge_mu = DatumGetFloat8(d);

                    if (!p_min_mu_is_null && edge_mu < p_min_mu)
                        continue;
                    if (edge_mu <= 0.0)
                        continue;

                    edge_cost = 1.0 / edge_mu;
                    new_cost = cur.cost + edge_cost;

                    make_entity_key(&nbr_key, nbr_hash);

                    nbr_ce = hash_search(best_costs, &nbr_key, HASH_ENTER, &found);
                    if (found && nbr_ce->best_cost <= new_cost)
                        continue;
                    nbr_ce->best_cost = new_cost;

                    next.cost = new_cost;
                    next.key  = nbr_key;
                    next.depth = cur.depth + 1;
                    next.path_len = cur.path_len + 1;

                    next.entity_path = MemoryContextAlloc(funcctx->multi_call_memory_ctx,
                                                          sizeof(EntityKey) * next.path_len);
                    memcpy(next.entity_path, cur.entity_path,
                           sizeof(EntityKey) * cur.path_len);
                    next.entity_path[cur.path_len] = nbr_key;

                    next.edge_path = MemoryContextAlloc(funcctx->multi_call_memory_ctx,
                                                        sizeof(EdgeKey) * cur.path_len);
                    if (cur.edge_path && cur.path_len > 1)
                        memcpy(next.edge_path, cur.edge_path,
                               sizeof(EdgeKey) * (cur.path_len - 1));
                    make_edge_key(&next.edge_path[cur.path_len - 1], edge_etid, edge_hash);

                    heap_push(&heap, next);

                    if (num_results >= results_cap)
                    {
                        results_cap *= 2;
                        results = repalloc(results, sizeof(AstarResult) * results_cap);
                    }
                    memcpy(results[num_results].target_hash, nbr_hash, SUBSTRATE_HASH_LEN);
                    results[num_results].depth = next.depth;
                    results[num_results].total_cost = new_cost;
                    results[num_results].entity_path = next.entity_path;
                    results[num_results].edge_path = next.edge_path;
                    results[num_results].path_len = next.path_len;
                    num_results++;
                }

                SPI_freetuptable(nbr_tuptable);
            }
        }

        SPI_finish();

        if (hartonomous_traversal_trace)
        {
            ereport(NOTICE,
                    (errmsg("traverse_astar: SPI loop done; num_results=%d", num_results)));
        }

        state = palloc(sizeof(AstarState));
        state->results = results;
        state->num_results = num_results;
        state->current = 0;

        if (get_call_result_type(fcinfo, NULL, &state->tupdesc) != TYPEFUNC_COMPOSITE)
            ereport(ERROR,
                    (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                     errmsg("function returning record called in context that cannot accept type record")));
        BlessTupleDesc(state->tupdesc);

        if (hartonomous_traversal_trace)
        {
            ereport(NOTICE,
                    (errmsg("traverse_astar: tupdesc natts=%d (expected 4)", state->tupdesc->natts)));
        }

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
        bytea       *target_hash_b;
        ArrayType   *ehash_arr;
        double       total_mu;

        target_hash_b = (bytea *) palloc(VARHDRSZ + SUBSTRATE_HASH_LEN);
        SET_VARSIZE(target_hash_b, VARHDRSZ + SUBSTRATE_HASH_LEN);
        memcpy(VARDATA(target_hash_b), r->target_hash, SUBSTRATE_HASH_LEN);

        total_mu = (r->total_cost > 0.0) ? (1.0 / r->total_cost) : 0.0;

        ehash_arr = construct_edge_hash_array(CurrentMemoryContext,
                                              r->edge_path,
                                              r->path_len - 1);

        values[0] = PointerGetDatum(target_hash_b);
        values[1] = Int32GetDatum(r->depth);
        values[2] = Float8GetDatum(total_mu);
        values[3] = PointerGetDatum(ehash_arr);

        tuple = heap_form_tuple(state->tupdesc, values, nulls_arr);
        result = HeapTupleGetDatum(tuple);
        SRF_RETURN_NEXT(funcctx, result);
    }

    SRF_RETURN_DONE(funcctx);
}
