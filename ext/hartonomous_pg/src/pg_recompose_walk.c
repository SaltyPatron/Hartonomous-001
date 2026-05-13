/*
 * pg_recompose_walk.c — iterative depth-first traversal of physicality-backed
 *                       composition metadata starting from a root entity hash.
 *
 * Substrate contract:
 *   substrate.recompose_walk(
 *       p_root_hash bytea,
 *       p_max_depth int DEFAULT 16
 *   ) RETURNS TABLE (entity_hash bytea, ordinal_position int, content_label text, depth int)
 *
 * Yields the root first (depth=0, ordinal=0), then each descendant in
 * left-to-right depth-first order. Ordinal is the position of THIS node
 * within its parent's children (root reported as 0).
 *
 * Schema notes:
 *   - substrate.entity has a SINGLE column: hash substrate.hash_value PRIMARY KEY.
 *     There is NO content_label / label / content_text column in the entity
 *     table — content labels live elsewhere (junction tables / classification).
 *     This SRF therefore returns content_label = NULL and the C# layer joins
 *     content (e.g. via substrate.entity_classification or codepoint_value)
 *     out-of-band.
 *   - substrate.get_composition_children(parent_hash) exposes
 *     physicality-backed child identity, ordinal, and RLE metadata. The SRF
 *     return type uses `ordinal_position` as a column alias on the result tuple.
 *   - rle_count compresses contiguous runs of the same child. We expand each
 *     RLE row into rle_count emitted tuples at consecutive ordinals so the
 *     walk reflects every textual position, not just the deduplicated entry.
 *
 * Memory discipline (mirrors pg_traversal.c UAF fix):
 *   The full walk is performed inside the SRF init phase. All emitted tuples
 *   are accumulated into an array allocated in funcctx->multi_call_memory_ctx,
 *   with bytea hashes deep-copied to that context BEFORE SPI_finish() runs.
 *   Per-call protocol then iterates the array.
 */
#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "executor/spi.h"
#include "utils/builtins.h"
#include "utils/memutils.h"
#include "catalog/pg_type.h"
#include "access/htup_details.h"

#include <string.h>

PG_FUNCTION_INFO_V1(pg_recompose_walk);

#define RECOMPOSE_HASH_LEN     32
#define RECOMPOSE_DEPTH_HARD_CAP 256

typedef struct WalkRow
{
    uint8   entity_hash[RECOMPOSE_HASH_LEN];
    int32   ordinal_position;
    int32   depth;
} WalkRow;

/* DFS stack frame: a node pending expansion. */
typedef struct StackFrame
{
    uint8   node_hash[RECOMPOSE_HASH_LEN];
    int32   ordinal;       /* ordinal of this node within its parent */
    int32   depth;
} StackFrame;

typedef struct WalkState
{
    WalkRow   *rows;
    int        num_rows;
    int        current;
    TupleDesc  tupdesc;
} WalkState;

Datum
pg_recompose_walk(PG_FUNCTION_ARGS)
{
    FuncCallContext *funcctx;
    WalkState       *state;

    if (SRF_IS_FIRSTCALL())
    {
        MemoryContext   oldctx;
        MemoryContext   mctx;
        bytea          *root_hash_in;
        int32           max_depth;
        StackFrame     *stack;
        int             stack_top;
        int             stack_cap;
        WalkRow        *rows;
        int             rows_count;
        int             rows_cap;
        SPIPlanPtr      child_plan;
        Oid             argtypes[1];

        funcctx = SRF_FIRSTCALL_INIT();
        mctx = funcctx->multi_call_memory_ctx;
        oldctx = MemoryContextSwitchTo(mctx);

        if (PG_ARGISNULL(0))
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("p_root_hash must not be NULL")));

        root_hash_in = PG_GETARG_BYTEA_PP(0);
        if (VARSIZE_ANY_EXHDR(root_hash_in) != RECOMPOSE_HASH_LEN)
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                     errmsg("p_root_hash must be exactly %d bytes",
                            RECOMPOSE_HASH_LEN)));

        max_depth = PG_ARGISNULL(1) ? 16 : PG_GETARG_INT32(1);
        if (max_depth < 0)
            max_depth = 0;
        if (max_depth > RECOMPOSE_DEPTH_HARD_CAP)
            ereport(ERROR,
                    (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                     errmsg("p_max_depth must be <= %d, got %d",
                            RECOMPOSE_DEPTH_HARD_CAP, max_depth)));

        /* ── stacks + result buffer in mctx ───────────────────────────── */
        stack_cap = 256;
        stack = (StackFrame *) MemoryContextAlloc(mctx, sizeof(StackFrame) * stack_cap);
        stack_top = 0;

        rows_cap = 1024;
        rows = (WalkRow *) MemoryContextAlloc(mctx, sizeof(WalkRow) * rows_cap);
        rows_count = 0;

        /* Push root frame: ordinal=0 (no parent), depth=0. */
        memcpy(stack[stack_top].node_hash, VARDATA_ANY(root_hash_in), RECOMPOSE_HASH_LEN);
        stack[stack_top].ordinal = 0;
        stack[stack_top].depth = 0;
        stack_top++;

        SPI_connect();

        /*
         * Children query: order DESC so that when we push children onto the
         * LIFO stack and pop, the leftmost child is popped first → emitted
         * order matches in-order DFS (root, leftmost-subtree, …, rightmost).
         */
        argtypes[0] = BYTEAOID;
        child_plan = SPI_prepare(
            "SELECT child_hash, ordinal, rle_count "
            "FROM substrate.get_composition_children($1) "
            "ORDER BY ordinal DESC",
            1, argtypes
        );
        if (child_plan == NULL)
            ereport(ERROR,
                    (errcode(ERRCODE_INTERNAL_ERROR),
                     errmsg("SPI_prepare failed for composition child query")));

        while (stack_top > 0)
        {
            StackFrame  cur;
            Datum       args[1];
            bytea      *parent_hash_arg;
            int         ret, row;

            /* Pop. */
            cur = stack[--stack_top];

            /* Emit. */
            if (rows_count >= rows_cap)
            {
                int new_cap = rows_cap * 2;
                rows = (WalkRow *) repalloc(rows, sizeof(WalkRow) * new_cap);
                rows_cap = new_cap;
            }
            /*
             * Deep-copy the hash from the stack frame (mctx) into the rows
             * array (also mctx). Both live past SPI_finish(), satisfying the
             * UAF discipline: every payload that survives the SPI block is
             * resident in funcctx->multi_call_memory_ctx.
             */
            memcpy(rows[rows_count].entity_hash, cur.node_hash, RECOMPOSE_HASH_LEN);
            rows[rows_count].ordinal_position = cur.ordinal;
            rows[rows_count].depth = cur.depth;
            rows_count++;

            /* Don't expand past max_depth. */
            if (cur.depth >= max_depth)
                continue;

            /* Children. */
            parent_hash_arg = (bytea *) palloc(VARHDRSZ + RECOMPOSE_HASH_LEN);
            SET_VARSIZE(parent_hash_arg, VARHDRSZ + RECOMPOSE_HASH_LEN);
            memcpy(VARDATA(parent_hash_arg), cur.node_hash, RECOMPOSE_HASH_LEN);
            args[0] = PointerGetDatum(parent_hash_arg);

            ret = SPI_execute_plan(child_plan, args, NULL, true /* read_only */, 0);
            if (ret != SPI_OK_SELECT)
            {
                if (SPI_tuptable != NULL)
                    SPI_freetuptable(SPI_tuptable);
                continue;
            }

            for (row = 0; row < (int) SPI_processed; row++)
            {
                HeapTuple   tuple = SPI_tuptable->vals[row];
                TupleDesc   spi_desc = SPI_tuptable->tupdesc;
                bool        isnull;
                Datum       d;
                bytea      *child_hash_b;
                int         child_hash_len;
                int32       child_ordinal;
                int32       rle_count;
                int         rep;

                d = SPI_getbinval(tuple, spi_desc, 1, &isnull);
                if (isnull) continue;
                child_hash_b = DatumGetByteaPP(d);
                child_hash_len = VARSIZE_ANY_EXHDR(child_hash_b);
                if (child_hash_len != RECOMPOSE_HASH_LEN) continue;

                d = SPI_getbinval(tuple, spi_desc, 2, &isnull);
                if (isnull) continue;
                child_ordinal = DatumGetInt32(d);

                d = SPI_getbinval(tuple, spi_desc, 3, &isnull);
                rle_count = isnull ? 1 : DatumGetInt32(d);
                if (rle_count < 1) rle_count = 1;

                /*
                 * RLE expansion: a row with rle_count=N represents N
                 * consecutive ordinals all pointing at the same child entity.
                 * Push each one as its own DFS frame with the appropriate
                 * ordinal. Leftmost-first DFS requires we push the LARGEST
                 * ordinal first (it pops last).
                 */
                for (rep = rle_count - 1; rep >= 0; rep--)
                {
                    if (stack_top >= stack_cap)
                    {
                        int new_cap = stack_cap * 2;
                        stack = (StackFrame *) repalloc(stack, sizeof(StackFrame) * new_cap);
                        stack_cap = new_cap;
                    }
                    memcpy(stack[stack_top].node_hash,
                           VARDATA_ANY(child_hash_b),
                           RECOMPOSE_HASH_LEN);
                    stack[stack_top].ordinal = child_ordinal + rep;
                    stack[stack_top].depth = cur.depth + 1;
                    stack_top++;
                }
            }

            SPI_freetuptable(SPI_tuptable);
        }

        SPI_finish();

        state = (WalkState *) MemoryContextAllocZero(mctx, sizeof(WalkState));
        state->rows = rows;
        state->num_rows = rows_count;
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
    state = (WalkState *) funcctx->user_fctx;

    if (state->current < state->num_rows)
    {
        WalkRow   *r = &state->rows[state->current++];
        Datum      values[4];
        bool       nulls[4] = {false, false, true, false};  /* content_label = NULL */
        HeapTuple  tuple;
        Datum      result;
        bytea     *hash_out;

        hash_out = (bytea *) palloc(VARHDRSZ + RECOMPOSE_HASH_LEN);
        SET_VARSIZE(hash_out, VARHDRSZ + RECOMPOSE_HASH_LEN);
        memcpy(VARDATA(hash_out), r->entity_hash, RECOMPOSE_HASH_LEN);

        values[0] = PointerGetDatum(hash_out);
        values[1] = Int32GetDatum(r->ordinal_position);
        values[2] = (Datum) 0;            /* content_label NULL */
        values[3] = Int32GetDatum(r->depth);

        tuple = heap_form_tuple(state->tupdesc, values, nulls);
        result = HeapTupleGetDatum(tuple);
        SRF_RETURN_NEXT(funcctx, result);
    }

    SRF_RETURN_DONE(funcctx);
}
