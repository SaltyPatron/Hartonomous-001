/*
 * pg_ucd_sequences_pg.c — PG-callable SRFs over the multi-codepoint UCD
 * tables that the .py pre-gen emits (NamedSequences, EmojiSequences,
 * EmojiZwjSequences, StandardizedVariants, Confusables, IdnaMapping,
 * CjkRadicals).
 *
 * Each SRF returns one row per entry in the pre-gen blob; the
 * substrate.populate_unicode_*_from_ext() SQL functions then materialise
 * text_composition entities + LINESTRINGZM physicality + the typed edge
 * (has_named_sequence / has_emoji_sequence / has_emoji_zwj_sequence /
 * has_standardized_variant / confusable_with / idna_maps_to /
 * has_radical_stroke) under unicode_consortium provenance.
 *
 * No SPI, no allocations beyond palloc per row, no per-row C string
 * parsing — the pre-gen tables already carry offset/length pairs into
 * shared byte arrays.
 */
#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "utils/builtins.h"
#include "utils/array.h"
#include "catalog/pg_type.h"
#include "access/htup_details.h"
#include "hartonomous_pg.h"

#include "generated/pg_ucd_named_sequences.h"
#include "generated/pg_ucd_emoji_seq.h"
#include "generated/pg_ucd_emoji_zwj_seq.h"
#include "generated/pg_ucd_standardized_variants.h"
#include "generated/pg_ucd_confusables.h"
#include "generated/pg_ucd_idna_mapping.h"
#include "generated/pg_ucd_cjk_radicals.h"

#include <string.h>

/* Shared lightweight SRF state — same shape as pg_codepoint_atoms_pg.c. */
typedef struct UcdSeqSrfState { uint32_t cur; uint32_t total; } UcdSeqSrfState;

static UcdSeqSrfState*
ucd_seq_srf_init(PG_FUNCTION_ARGS, uint32_t total)
{
    FuncCallContext* funcctx;
    MemoryContext oldctx;
    TupleDesc tupdesc;
    UcdSeqSrfState* st;

    funcctx = SRF_FIRSTCALL_INIT();
    oldctx = MemoryContextSwitchTo(funcctx->multi_call_memory_ctx);
    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE) {
        ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                        errmsg("function returning record requires column definition")));
    }
    funcctx->tuple_desc = BlessTupleDesc(tupdesc);
    st = (UcdSeqSrfState*) palloc(sizeof(UcdSeqSrfState));
    st->cur = 0; st->total = total;
    funcctx->user_fctx = st;
    MemoryContextSwitchTo(oldctx);
    return st;
}

/* Slice the codepoint-array sub-segment for entry `cur` into an int[] datum. */
static ArrayType*
slice_cp_array(const uint32_t* cps_base, uint32_t off, uint32_t len)
{
    Datum* datums;
    ArrayType* result;
    uint32_t i;

    if (len == 0) {
        return construct_empty_array(INT4OID);
    }
    datums = (Datum*) palloc(sizeof(Datum) * len);
    for (i = 0; i < len; i++) {
        datums[i] = Int32GetDatum((int32) cps_base[off + i]);
    }
    result = construct_array(datums, (int) len, INT4OID, 4, true, 'i');
    return result;
}

/* Slice an inline UTF-8 byte sub-segment into a text datum. */
static text*
slice_text(const uint8_t* base, uint32_t off, uint32_t len)
{
    text* t = (text*) palloc(VARHDRSZ + len);
    SET_VARSIZE(t, VARHDRSZ + len);
    if (len > 0) {
        memcpy(VARDATA(t), base + off, len);
    }
    return t;
}

/* ─── substrate.ucd_named_sequences() ─────────────────────────────────── */
PG_FUNCTION_INFO_V1(pg_ucd_named_sequences);
Datum pg_ucd_named_sequences(PG_FUNCTION_ARGS)
{
    FuncCallContext* funcctx;
    UcdSeqSrfState* st;
    Datum values[2];
    bool  nulls[2] = { false, false };
    HeapTuple tuple;
    uint32_t cps_off, name_off;
    uint8_t  cps_len;
    uint16_t name_len;

    if (SRF_IS_FIRSTCALL()) ucd_seq_srf_init(fcinfo, UC_NAMED_SEQ_COUNT);
    funcctx = SRF_PERCALL_SETUP();
    st = (UcdSeqSrfState*) funcctx->user_fctx;
    if (st->cur >= st->total) SRF_RETURN_DONE(funcctx);

    cps_off  = uc_named_seq_off[st->cur];
    cps_len  = uc_named_seq_len[st->cur];
    name_off = uc_named_seq_name_off[st->cur];
    name_len = uc_named_seq_name_len[st->cur];

    values[0] = PointerGetDatum(slice_cp_array(uc_named_seq_cps, cps_off, cps_len));
    values[1] = PointerGetDatum(slice_text(uc_named_seq_names, name_off, name_len));
    tuple = heap_form_tuple(funcctx->tuple_desc, values, nulls);
    st->cur += 1;
    SRF_RETURN_NEXT(funcctx, HeapTupleGetDatum(tuple));
}

/* ─── substrate.ucd_emoji_sequences() ─────────────────────────────────── */
PG_FUNCTION_INFO_V1(pg_ucd_emoji_sequences);
Datum pg_ucd_emoji_sequences(PG_FUNCTION_ARGS)
{
    FuncCallContext* funcctx;
    UcdSeqSrfState* st;
    Datum values[3];
    bool  nulls[3] = { false, false, false };
    HeapTuple tuple;
    uint32_t cps_off, name_off, prop_off;
    uint8_t  cps_len;
    uint16_t name_len;
    uint8_t  prop_len;

    if (SRF_IS_FIRSTCALL()) ucd_seq_srf_init(fcinfo, UC_EMOJI_SEQ_COUNT);
    funcctx = SRF_PERCALL_SETUP();
    st = (UcdSeqSrfState*) funcctx->user_fctx;
    if (st->cur >= st->total) SRF_RETURN_DONE(funcctx);

    cps_off  = uc_emoji_seq_off[st->cur];
    cps_len  = uc_emoji_seq_len[st->cur];
    name_off = uc_emoji_seq_name_off[st->cur];
    name_len = uc_emoji_seq_name_len[st->cur];
    prop_off = uc_emoji_seq_prop_off[st->cur];
    prop_len = uc_emoji_seq_prop_len[st->cur];

    values[0] = PointerGetDatum(slice_cp_array(uc_emoji_seq_cps, cps_off, cps_len));
    values[1] = PointerGetDatum(slice_text(uc_emoji_seq_names, name_off, name_len));
    values[2] = PointerGetDatum(slice_text(uc_emoji_seq_props, prop_off, prop_len));
    tuple = heap_form_tuple(funcctx->tuple_desc, values, nulls);
    st->cur += 1;
    SRF_RETURN_NEXT(funcctx, HeapTupleGetDatum(tuple));
}

/* ─── substrate.ucd_emoji_zwj_sequences() ─────────────────────────────── */
PG_FUNCTION_INFO_V1(pg_ucd_emoji_zwj_sequences);
Datum pg_ucd_emoji_zwj_sequences(PG_FUNCTION_ARGS)
{
    FuncCallContext* funcctx;
    UcdSeqSrfState* st;
    Datum values[3];
    bool  nulls[3] = { false, false, false };
    HeapTuple tuple;
    uint32_t cps_off, name_off, prop_off;
    uint8_t  cps_len;
    uint16_t name_len;
    uint8_t  prop_len;

    if (SRF_IS_FIRSTCALL()) ucd_seq_srf_init(fcinfo, UC_EMOJI_ZWJ_SEQ_COUNT);
    funcctx = SRF_PERCALL_SETUP();
    st = (UcdSeqSrfState*) funcctx->user_fctx;
    if (st->cur >= st->total) SRF_RETURN_DONE(funcctx);

    cps_off  = uc_emoji_zwj_seq_off[st->cur];
    cps_len  = uc_emoji_zwj_seq_len[st->cur];
    name_off = uc_emoji_zwj_seq_name_off[st->cur];
    name_len = uc_emoji_zwj_seq_name_len[st->cur];
    prop_off = uc_emoji_zwj_seq_prop_off[st->cur];
    prop_len = uc_emoji_zwj_seq_prop_len[st->cur];

    values[0] = PointerGetDatum(slice_cp_array(uc_emoji_zwj_seq_cps, cps_off, cps_len));
    values[1] = PointerGetDatum(slice_text(uc_emoji_zwj_seq_names, name_off, name_len));
    values[2] = PointerGetDatum(slice_text(uc_emoji_zwj_seq_props, prop_off, prop_len));
    tuple = heap_form_tuple(funcctx->tuple_desc, values, nulls);
    st->cur += 1;
    SRF_RETURN_NEXT(funcctx, HeapTupleGetDatum(tuple));
}

/* ─── substrate.ucd_standardized_variants() ───────────────────────────── */
PG_FUNCTION_INFO_V1(pg_ucd_standardized_variants);
Datum pg_ucd_standardized_variants(PG_FUNCTION_ARGS)
{
    FuncCallContext* funcctx;
    UcdSeqSrfState* st;
    Datum values[4];
    bool  nulls[4] = { false, false, false, false };
    HeapTuple tuple;
    uint32_t desc_off, scope_off;
    uint16_t desc_len;
    uint8_t  scope_len;

    if (SRF_IS_FIRSTCALL()) ucd_seq_srf_init(fcinfo, UC_STD_VAR_COUNT);
    funcctx = SRF_PERCALL_SETUP();
    st = (UcdSeqSrfState*) funcctx->user_fctx;
    if (st->cur >= st->total) SRF_RETURN_DONE(funcctx);

    desc_off  = uc_std_var_desc_off[st->cur];
    desc_len  = uc_std_var_desc_len[st->cur];
    scope_off = uc_std_var_scope_off[st->cur];
    scope_len = uc_std_var_scope_len[st->cur];

    values[0] = Int32GetDatum((int32) uc_std_var_base[st->cur]);
    values[1] = Int32GetDatum((int32) uc_std_var_vs[st->cur]);
    values[2] = PointerGetDatum(slice_text(uc_std_var_descs, desc_off, desc_len));
    values[3] = PointerGetDatum(slice_text(uc_std_var_scopes, scope_off, scope_len));
    tuple = heap_form_tuple(funcctx->tuple_desc, values, nulls);
    st->cur += 1;
    SRF_RETURN_NEXT(funcctx, HeapTupleGetDatum(tuple));
}

/* ─── substrate.ucd_confusables() ─────────────────────────────────────── */
PG_FUNCTION_INFO_V1(pg_ucd_confusables);
Datum pg_ucd_confusables(PG_FUNCTION_ARGS)
{
    FuncCallContext* funcctx;
    UcdSeqSrfState* st;
    Datum values[3];
    bool  nulls[3] = { false, false, false };
    HeapTuple tuple;
    uint32_t src_off, tgt_off, cls_off;
    uint8_t  src_len, tgt_len, cls_len;

    if (SRF_IS_FIRSTCALL()) ucd_seq_srf_init(fcinfo, UC_CONFUSABLES_COUNT);
    funcctx = SRF_PERCALL_SETUP();
    st = (UcdSeqSrfState*) funcctx->user_fctx;
    if (st->cur >= st->total) SRF_RETURN_DONE(funcctx);

    src_off = uc_conf_src_off[st->cur];
    src_len = uc_conf_src_len[st->cur];
    tgt_off = uc_conf_tgt_off[st->cur];
    tgt_len = uc_conf_tgt_len[st->cur];
    cls_off = uc_conf_cls_off[st->cur];
    cls_len = uc_conf_cls_len[st->cur];

    values[0] = PointerGetDatum(slice_cp_array(uc_conf_src_cps, src_off, src_len));
    values[1] = PointerGetDatum(slice_cp_array(uc_conf_tgt_cps, tgt_off, tgt_len));
    values[2] = PointerGetDatum(slice_text(uc_conf_cls, cls_off, cls_len));
    tuple = heap_form_tuple(funcctx->tuple_desc, values, nulls);
    st->cur += 1;
    SRF_RETURN_NEXT(funcctx, HeapTupleGetDatum(tuple));
}

/* ─── substrate.ucd_idna_mapping() ────────────────────────────────────── */
PG_FUNCTION_INFO_V1(pg_ucd_idna_mapping);
Datum pg_ucd_idna_mapping(PG_FUNCTION_ARGS)
{
    FuncCallContext* funcctx;
    UcdSeqSrfState* st;
    Datum values[4];
    bool  nulls[4] = { false, false, false, false };
    HeapTuple tuple;
    uint32_t status_off, map_off;
    uint8_t  status_len, map_len;
    text* status_text;

    if (SRF_IS_FIRSTCALL()) ucd_seq_srf_init(fcinfo, UC_IDNA_COUNT);
    funcctx = SRF_PERCALL_SETUP();
    st = (UcdSeqSrfState*) funcctx->user_fctx;
    if (st->cur >= st->total) SRF_RETURN_DONE(funcctx);

    status_off = uc_idna_status_off[st->cur];
    status_len = uc_idna_status_len[st->cur];
    map_off    = uc_idna_map_off[st->cur];
    map_len    = uc_idna_map_len[st->cur];

    /* uc_idna_status is a uint8_t stream of statusid bytes, one per row,
     * with the off+len convention identical to other slices. The mapping
     * vocabulary lives in uc_idna_status_codes — but at run-time the row
     * just exposes the raw status byte slice as text so the populate
     * function can resolve via a static reference mapping in SQL. */
    status_text = (text*) palloc(VARHDRSZ + status_len);
    SET_VARSIZE(status_text, VARHDRSZ + status_len);
    if (status_len > 0) {
        memcpy(VARDATA(status_text), uc_idna_status + status_off, status_len);
    }

    values[0] = Int32GetDatum((int32) uc_idna_lo[st->cur]);
    values[1] = Int32GetDatum((int32) uc_idna_hi[st->cur]);
    values[2] = PointerGetDatum(status_text);
    values[3] = PointerGetDatum(slice_cp_array(uc_idna_map, map_off, map_len));
    tuple = heap_form_tuple(funcctx->tuple_desc, values, nulls);
    st->cur += 1;
    SRF_RETURN_NEXT(funcctx, HeapTupleGetDatum(tuple));
}

/* ─── substrate.ucd_cjk_radicals() ────────────────────────────────────── */
PG_FUNCTION_INFO_V1(pg_ucd_cjk_radicals);
Datum pg_ucd_cjk_radicals(PG_FUNCTION_ARGS)
{
    FuncCallContext* funcctx;
    UcdSeqSrfState* st;
    Datum values[3];
    bool  nulls[3] = { false, false, false };
    HeapTuple tuple;
    uint32_t num_off;
    uint8_t  num_len;

    if (SRF_IS_FIRSTCALL()) ucd_seq_srf_init(fcinfo, UC_CJK_RADICALS_COUNT);
    funcctx = SRF_PERCALL_SETUP();
    st = (UcdSeqSrfState*) funcctx->user_fctx;
    if (st->cur >= st->total) SRF_RETURN_DONE(funcctx);

    num_off = uc_cjk_radical_num_off[st->cur];
    num_len = uc_cjk_radical_num_len[st->cur];

    values[0] = PointerGetDatum(slice_text(uc_cjk_radical_nums, num_off, num_len));
    values[1] = Int32GetDatum((int32) uc_cjk_radical_radical[st->cur]);
    values[2] = Int32GetDatum((int32) uc_cjk_radical_unified[st->cur]);
    tuple = heap_form_tuple(funcctx->tuple_desc, values, nulls);
    st->cur += 1;
    SRF_RETURN_NEXT(funcctx, HeapTupleGetDatum(tuple));
}
