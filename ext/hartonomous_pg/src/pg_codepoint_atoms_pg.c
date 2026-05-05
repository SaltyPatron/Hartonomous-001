/*
 * pg_codepoint_atoms_pg.c — PG-callable surface over the tier-0 codepoint
 * atoms generated from UCD 17.0.0 (see scripts/build/generate_unicode_tables.py).
 *
 * Functions exposed to substrate.* SQL:
 *
 *   substrate.cp_hash(int)         → bytea       — precomputed BLAKE3
 *   substrate.cp_centroid(int)     → point4d     — precomputed S^3 centroid
 *   substrate.cp_hilbert(int)      → bigint      — precomputed Hilbert index
 *   substrate.cp_from_hash(bytea)  → int         — hash → codepoint reverse
 *   substrate.cp_gcb(int)          → int         — Grapheme_Cluster_Break enum
 *   substrate.cp_wb(int)           → int         — Word_Break enum
 *   substrate.cp_sb(int)           → int         — Sentence_Break enum
 *   substrate.cp_lb(int)           → int         — Line_Break enum
 *   substrate.cp_incb(int)         → int         — Indic_Conjunct_Break enum
 *   substrate.cp_extended_pictographic(int) → bool
 *   substrate.cp_general_category(int) → int     — General_Category enum
 *   substrate.cp_ccc(int)          → int         — Canonical_Combining_Class
 *   substrate.cp_script(int)       → int         — Script enum
 *   substrate.cp_block(int)        → int         — Block enum
 *   substrate.cp_simple_uppercase(int) → int
 *   substrate.cp_simple_lowercase(int) → int
 *   substrate.cp_simple_titlecase(int) → int
 *   substrate.cp_simple_case_fold(int) → int
 *   substrate.cp_uca_index(int)    → int         — UCA-sorted position
 *   substrate.cp_uca_total()       → int
 *   substrate.ucd_version()        → text
 *
 * All O(1) array loads (cp_from_hash is O(log N) binary search). No SPI,
 * no DB round-trip. The tables are baked in at extension build time;
 * Law #6 deterministic by construction (extension version pins UCD version).
 */
#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "utils/builtins.h"
#include "utils/numeric.h"
#include "utils/array.h"
#include "catalog/pg_type.h"
#include "access/htup_details.h"
#include "hartonomous_pg.h"

#include "generated/pg_unicode_version.h"
#include "generated/pg_ucd_segmentation.h"
#include "generated/pg_ucd_classification.h"
#include "generated/pg_ucd_casing.h"
#include "generated/pg_ucd_pictographic.h"
#include "generated/pg_ucd_decomp.h"
#include "generated/pg_ucd_fcf.h"
#include "generated/pg_ucd_uca.h"
#include "generated/pg_ucd_names.h"
#include "generated/pg_ucd_inventory.h"
#include "generated/pg_ucd_tier1.h"
#include "generated/pg_ucd_atoms_blob.h"

#include <string.h>

/* point4d uses x[4] layout — matches hartonomous_pg.h. */

/* ── Helper: bounds-check a codepoint argument. ── */
static int32_t arg_cp(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                        errmsg("codepoint %d out of range [0, %d)", cp, UNICODE_CODEPOINT_MAX)));
    }
    return cp;
}

/* ─────────────────────────────────────────────────────────────────── */
PG_FUNCTION_INFO_V1(pg_cp_hash);

Datum pg_cp_hash(PG_FUNCTION_ARGS)
{
    int32_t cp = arg_cp(PG_GETARG_INT32(0));
    const uint8_t* h = huc_cp_hash_at(cp);
    if (!h) PG_RETURN_NULL();
    bytea* b = (bytea*) palloc(VARHDRSZ + CP_HASH_LEN);
    SET_VARSIZE(b, VARHDRSZ + CP_HASH_LEN);
    memcpy(VARDATA(b), h, CP_HASH_LEN);
    PG_RETURN_BYTEA_P(b);
}

PG_FUNCTION_INFO_V1(pg_cp_centroid);

Datum pg_cp_centroid(PG_FUNCTION_ARGS)
{
    int32_t cp = arg_cp(PG_GETARG_INT32(0));
    const double* c = huc_cp_centroid_at(cp);
    if (!c) PG_RETURN_NULL();
    Point4D* p = (Point4D*) palloc(sizeof(Point4D));
    p->x[0] = c[0]; p->x[1] = c[1]; p->x[2] = c[2]; p->x[3] = c[3];
    PG_RETURN_POINT4D_P(p);
}

/* Per-axis scalar accessors. Combined with PostGIS ST_MakePoint(x, y, z, m)
 * these let SQL construct POINTZM geometry directly — substrate.populate_codepoint_atoms
 * uses them to bulk-insert 1.1M physicality rows in one statement. */
PG_FUNCTION_INFO_V1(pg_cp_x);
Datum pg_cp_x(PG_FUNCTION_ARGS)
{ int32_t cp = arg_cp(PG_GETARG_INT32(0));
  const double* c = huc_cp_centroid_at(cp);
  if (!c) PG_RETURN_NULL();
  PG_RETURN_FLOAT8(c[0]); }

PG_FUNCTION_INFO_V1(pg_cp_y);
Datum pg_cp_y(PG_FUNCTION_ARGS)
{ int32_t cp = arg_cp(PG_GETARG_INT32(0));
  const double* c = huc_cp_centroid_at(cp);
  if (!c) PG_RETURN_NULL();
  PG_RETURN_FLOAT8(c[1]); }

PG_FUNCTION_INFO_V1(pg_cp_z);
Datum pg_cp_z(PG_FUNCTION_ARGS)
{ int32_t cp = arg_cp(PG_GETARG_INT32(0));
  const double* c = huc_cp_centroid_at(cp);
  if (!c) PG_RETURN_NULL();
  PG_RETURN_FLOAT8(c[2]); }

PG_FUNCTION_INFO_V1(pg_cp_m);
Datum pg_cp_m(PG_FUNCTION_ARGS)
{ int32_t cp = arg_cp(PG_GETARG_INT32(0));
  const double* c = huc_cp_centroid_at(cp);
  if (!c) PG_RETURN_NULL();
  PG_RETURN_FLOAT8(c[3]); }

PG_FUNCTION_INFO_V1(pg_cp_hilbert);

Datum pg_cp_hilbert(PG_FUNCTION_ARGS)
{
    int32_t cp = arg_cp(PG_GETARG_INT32(0));
    PG_RETURN_INT64((int64_t) huc_cp_hilbert_at(cp));
}

PG_FUNCTION_INFO_V1(pg_cp_from_hash);

Datum pg_cp_from_hash(PG_FUNCTION_ARGS)
{
    bytea* h_arg = PG_GETARG_BYTEA_PP(0);
    if (VARSIZE_ANY_EXHDR(h_arg) != CP_HASH_LEN) {
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                        errmsg("cp_from_hash: hash must be %d bytes", CP_HASH_LEN)));
    }
    int32_t cp = uc_cp_from_hash((const uint8_t*) VARDATA_ANY(h_arg));
    if (cp < 0) PG_RETURN_NULL();
    PG_RETURN_INT32(cp);
}

#define CP_INT_GETTER(NAME, ARRAY)                              \
    PG_FUNCTION_INFO_V1(pg_cp_##NAME);                          \
    Datum pg_cp_##NAME(PG_FUNCTION_ARGS)                        \
    {                                                           \
        int32_t cp = arg_cp(PG_GETARG_INT32(0));                \
        PG_RETURN_INT32((int32_t) ARRAY[cp]);                   \
    }

CP_INT_GETTER(gcb,                       uc_gcb)
CP_INT_GETTER(wb,                        uc_wb)
CP_INT_GETTER(sb,                        uc_sb)
CP_INT_GETTER(lb,                        uc_lb)
CP_INT_GETTER(incb,                      uc_incb)
CP_INT_GETTER(general_category,          uc_gc)
CP_INT_GETTER(ccc,                       uc_ccc)
CP_INT_GETTER(script,                    uc_script)
CP_INT_GETTER(block,                     uc_block)
CP_INT_GETTER(simple_uppercase,          uc_simple_uppercase)
CP_INT_GETTER(simple_lowercase,          uc_simple_lowercase)
CP_INT_GETTER(simple_titlecase,          uc_simple_titlecase)
CP_INT_GETTER(simple_case_fold,          uc_simple_case_fold)
CP_INT_GETTER(uca_index,                 uc_uca_index)
/* Extended properties added in the full UCD/UCA catalog pass. */
CP_INT_GETTER(bidi,                      uc_bidi)
CP_INT_GETTER(eaw,                       uc_eaw)
CP_INT_GETTER(hsy,                       uc_hsy)
CP_INT_GETTER(num_type,                  uc_num_type)
CP_INT_GETTER(decomp_type,               uc_decomp_type)

PG_FUNCTION_INFO_V1(pg_cp_extended_pictographic);

Datum pg_cp_extended_pictographic(PG_FUNCTION_ARGS)
{
    int32_t cp = arg_cp(PG_GETARG_INT32(0));
    PG_RETURN_BOOL(uc_extended_pictographic(cp) != 0);
}

PG_FUNCTION_INFO_V1(pg_cp_uca_total);

Datum pg_cp_uca_total(PG_FUNCTION_ARGS)
{
    PG_RETURN_INT32(UC_UCA_TOTAL);
}

PG_FUNCTION_INFO_V1(pg_ucd_version);

Datum pg_ucd_version(PG_FUNCTION_ARGS)
{
    PG_RETURN_TEXT_P(cstring_to_text(UCD_VERSION_STRING));
}

/* ─── Variable-length per-codepoint payloads ─────────────────────────── */
/* Build an int[] from a slice of uc_decomp_data / uc_fcf_data / uc_uca_data.
 * Empty slices return an empty int[] (NOT NULL) so SQL callers can
 * ARRAY_LENGTH() without nullability ceremony. */
static ArrayType* slice_int_array(const int32_t* data, uint32_t off, uint16_t len)
{
    Datum* elems = (Datum*) palloc(sizeof(Datum) * (len == 0 ? 1 : len));
    for (uint16_t i = 0; i < len; ++i) {
        elems[i] = Int32GetDatum(data[off + i]);
    }
    int dims[1] = { len };
    int lbs[1]  = { 1 };
    ArrayType* a = construct_md_array(elems, NULL, 1, dims, lbs,
                                      INT4OID, sizeof(int32), true, TYPALIGN_INT);
    pfree(elems);
    return a;
}

PG_FUNCTION_INFO_V1(pg_cp_decomp);
Datum pg_cp_decomp(PG_FUNCTION_ARGS)
{
    int32_t cp = arg_cp(PG_GETARG_INT32(0));
    PG_RETURN_ARRAYTYPE_P(slice_int_array(uc_decomp_data,
                                          uc_decomp_off[cp],
                                          uc_decomp_len[cp]));
}

PG_FUNCTION_INFO_V1(pg_cp_full_case_fold);
Datum pg_cp_full_case_fold(PG_FUNCTION_ARGS)
{
    int32_t cp = arg_cp(PG_GETARG_INT32(0));
    PG_RETURN_ARRAYTYPE_P(slice_int_array(uc_fcf_data,
                                          uc_fcf_off[cp],
                                          uc_fcf_len[cp]));
}

PG_FUNCTION_INFO_V1(pg_cp_uca_weights);
Datum pg_cp_uca_weights(PG_FUNCTION_ARGS)
{
    int32_t cp = arg_cp(PG_GETARG_INT32(0));
    /* uc_uca_off / uc_uca_len are tuple-indexed; data is uint32_t with
     * 3 weights per tuple (primary, secondary, tertiary). Flatten. */
    uint32_t flat_off = uc_uca_off[cp] * 3;
    uint16_t flat_len = (uint16_t) (uc_uca_len[cp] * 3);
    PG_RETURN_ARRAYTYPE_P(slice_int_array((const int32_t*) uc_uca_data,
                                          flat_off,
                                          flat_len));
}

PG_FUNCTION_INFO_V1(pg_cp_name);
Datum pg_cp_name(PG_FUNCTION_ARGS)
{
    int32_t cp = arg_cp(PG_GETARG_INT32(0));
    uint16_t len = uc_name_len[cp];
    if (len == 0) PG_RETURN_NULL();
    text* t = (text*) palloc(VARHDRSZ + len);
    SET_VARSIZE(t, VARHDRSZ + len);
    memcpy(VARDATA(t), uc_name_blob + uc_name_off[cp], len);
    PG_RETURN_TEXT_P(t);
}

/* ─── SETOF inventory accessors ──────────────────────────────────────── */
/* The generator emits one struct array per inventory under
 * generated/pg_unicode_inventory.h:
 *
 *   GCEntry         { code, description, group }   uc_inv_gc[UC_GC_COUNT]
 *   ScriptEntry     { code }                       uc_inv_scripts[UC_SCRIPT_COUNT]
 *   BlockEntry      { code, range_start, range_end } uc_inv_blocks[UC_BLOCK_COUNT]
 *   BreakPropEntry  { category, code, enum_id }    uc_inv_break_props[UC_BREAK_COUNT]
 *
 * Each inventory is its own SETOF with a return shape that matches the
 * struct fields exactly — substrate.populate_*_from_ext() functions then
 * do INSERT ... ON CONFLICT DO NOTHING with no parsing/derivation layer.
 */

typedef struct UcdSimpleSrfState { uint32_t cur; uint32_t total; } UcdSimpleSrfState;

static UcdSimpleSrfState*
ucd_simple_srf_init(PG_FUNCTION_ARGS, uint32_t total)
{
    FuncCallContext* funcctx = SRF_FIRSTCALL_INIT();
    MemoryContext oldctx = MemoryContextSwitchTo(funcctx->multi_call_memory_ctx);
    TupleDesc tupdesc;
    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE) {
        ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                        errmsg("function returning record requires column definition")));
    }
    funcctx->tuple_desc = BlessTupleDesc(tupdesc);
    UcdSimpleSrfState* st = (UcdSimpleSrfState*) palloc(sizeof(UcdSimpleSrfState));
    st->cur = 0; st->total = total;
    funcctx->user_fctx = st;
    MemoryContextSwitchTo(oldctx);
    return st;
}

PG_FUNCTION_INFO_V1(pg_ucd_general_categories);
Datum pg_ucd_general_categories(PG_FUNCTION_ARGS)
{
    if (SRF_IS_FIRSTCALL()) ucd_simple_srf_init(fcinfo, UC_GC_COUNT);
    FuncCallContext* funcctx = SRF_PERCALL_SETUP();
    UcdSimpleSrfState* st = (UcdSimpleSrfState*) funcctx->user_fctx;
    if (st->cur >= st->total) SRF_RETURN_DONE(funcctx);

    Datum values[4];
    bool  nulls[4] = { false, false, false, false };
    const GCEntry* e = &uc_inv_gc[st->cur];
    values[0] = Int32GetDatum((int32) st->cur);
    values[1] = CStringGetTextDatum(e->code);
    values[2] = CStringGetTextDatum(e->description);
    values[3] = CStringGetTextDatum(e->group);
    HeapTuple tuple = heap_form_tuple(funcctx->tuple_desc, values, nulls);
    st->cur += 1;
    SRF_RETURN_NEXT(funcctx, HeapTupleGetDatum(tuple));
}

PG_FUNCTION_INFO_V1(pg_ucd_scripts);
Datum pg_ucd_scripts(PG_FUNCTION_ARGS)
{
    if (SRF_IS_FIRSTCALL()) ucd_simple_srf_init(fcinfo, UC_SCRIPT_COUNT);
    FuncCallContext* funcctx = SRF_PERCALL_SETUP();
    UcdSimpleSrfState* st = (UcdSimpleSrfState*) funcctx->user_fctx;
    if (st->cur >= st->total) SRF_RETURN_DONE(funcctx);

    Datum values[2];
    bool  nulls[2] = { false, false };
    values[0] = Int32GetDatum((int32) st->cur);
    values[1] = CStringGetTextDatum(uc_inv_scripts[st->cur].code);
    HeapTuple tuple = heap_form_tuple(funcctx->tuple_desc, values, nulls);
    st->cur += 1;
    SRF_RETURN_NEXT(funcctx, HeapTupleGetDatum(tuple));
}

PG_FUNCTION_INFO_V1(pg_ucd_blocks);
Datum pg_ucd_blocks(PG_FUNCTION_ARGS)
{
    if (SRF_IS_FIRSTCALL()) ucd_simple_srf_init(fcinfo, UC_BLOCK_COUNT);
    FuncCallContext* funcctx = SRF_PERCALL_SETUP();
    UcdSimpleSrfState* st = (UcdSimpleSrfState*) funcctx->user_fctx;
    if (st->cur >= st->total) SRF_RETURN_DONE(funcctx);

    Datum values[4];
    bool  nulls[4] = { false, false, false, false };
    const BlockEntry* b = &uc_inv_blocks[st->cur];
    values[0] = Int32GetDatum((int32) st->cur);
    values[1] = CStringGetTextDatum(b->code);
    values[2] = Int32GetDatum(b->range_start);
    values[3] = Int32GetDatum(b->range_end);
    HeapTuple tuple = heap_form_tuple(funcctx->tuple_desc, values, nulls);
    st->cur += 1;
    SRF_RETURN_NEXT(funcctx, HeapTupleGetDatum(tuple));
}

PG_FUNCTION_INFO_V1(pg_ucd_break_properties);
Datum pg_ucd_break_properties(PG_FUNCTION_ARGS)
{
    if (SRF_IS_FIRSTCALL()) ucd_simple_srf_init(fcinfo, UC_BREAK_COUNT);
    FuncCallContext* funcctx = SRF_PERCALL_SETUP();
    UcdSimpleSrfState* st = (UcdSimpleSrfState*) funcctx->user_fctx;
    if (st->cur >= st->total) SRF_RETURN_DONE(funcctx);

    Datum values[4];
    bool  nulls[4] = { false, false, false, false };
    const BreakPropEntry* bp = &uc_inv_break_props[st->cur];
    values[0] = Int32GetDatum((int32) st->cur);
    values[1] = CStringGetTextDatum(bp->category);
    values[2] = CStringGetTextDatum(bp->code);
    values[3] = Int32GetDatum((int32) bp->enum_id);
    HeapTuple tuple = heap_form_tuple(funcctx->tuple_desc, values, nulls);
    st->cur += 1;
    SRF_RETURN_NEXT(funcctx, HeapTupleGetDatum(tuple));
}

/* ─── Composite codepoint_atom row ───────────────────────────────────── */
/* 28-column composite: cp + hash + 4D coords + hilbert + 18 enum/scalar
 * properties + extended_pictographic flag + name. Built once in C, blessed
 * once, emitted via SRF — eliminates 28 IMMUTABLE function evaluations
 * per row when SQL needs the full row. */
#define ATOM_COL_COUNT 28

/* build_atom_values allocates varlena payloads for hash (index 1) and
 * optional name (index 27). Keep ownership in the calling memory context;
 * eager pfree here has shown unstable behavior in PG18 SRF paths. */

static void
build_atom_values(int32_t cp, Datum* values, bool* nulls)
{
    const uint8_t* hash_src = huc_cp_hash_at(cp);
    const double*  cent_src = huc_cp_centroid_at(cp);
    bytea* hash_b = (bytea*) palloc(VARHDRSZ + CP_HASH_LEN);
    SET_VARSIZE(hash_b, VARHDRSZ + CP_HASH_LEN);
    if (hash_src) memcpy(VARDATA(hash_b), hash_src, CP_HASH_LEN);
    else          memset(VARDATA(hash_b), 0, CP_HASH_LEN);

    /* extended_pictographic — inline function over the bitmap */
    bool ext_pict = uc_extended_pictographic(cp) != 0;

    /* name — NULL when len==0 */
    Datum name_datum;
    bool  name_null;
    {
        uint16_t len = uc_name_len[cp];
        if (len == 0) { name_datum = (Datum) 0; name_null = true; }
        else {
            text* t = (text*) palloc(VARHDRSZ + len);
            SET_VARSIZE(t, VARHDRSZ + len);
            memcpy(VARDATA(t), uc_name_blob + uc_name_off[cp], len);
            name_datum = PointerGetDatum(t);
            name_null  = false;
        }
    }

    int i = 0;
    values[i] = Int32GetDatum(cp);                                      nulls[i++] = false;
    values[i] = PointerGetDatum(hash_b);                                nulls[i++] = false;
    values[i] = Float8GetDatum(cent_src ? cent_src[0] : 0.0);             nulls[i++] = (cent_src == NULL);
    values[i] = Float8GetDatum(cent_src ? cent_src[1] : 0.0);             nulls[i++] = (cent_src == NULL);
    values[i] = Float8GetDatum(cent_src ? cent_src[2] : 0.0);             nulls[i++] = (cent_src == NULL);
    values[i] = Float8GetDatum(cent_src ? cent_src[3] : 0.0);             nulls[i++] = (cent_src == NULL);
    values[i] = Int64GetDatum((int64_t) huc_cp_hilbert_at(cp));           nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_gcb[cp]);                     nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_wb[cp]);                      nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_sb[cp]);                      nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_lb[cp]);                      nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_incb[cp]);                    nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_gc[cp]);                      nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_ccc[cp]);                     nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_script[cp]);                  nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_block[cp]);                   nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_simple_uppercase[cp]);        nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_simple_lowercase[cp]);        nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_simple_titlecase[cp]);        nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_simple_case_fold[cp]);        nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_uca_index[cp]);               nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_bidi[cp]);                    nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_eaw[cp]);                     nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_hsy[cp]);                     nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_num_type[cp]);                nulls[i++] = false;
    values[i] = Int32GetDatum((int32_t) uc_decomp_type[cp]);             nulls[i++] = false;
    values[i] = BoolGetDatum(ext_pict);                                  nulls[i++] = false;
    values[i] = name_datum;                                              nulls[i++] = name_null;
}

PG_FUNCTION_INFO_V1(pg_cp_atom);
Datum pg_cp_atom(PG_FUNCTION_ARGS)
{
    int32_t cp = arg_cp(PG_GETARG_INT32(0));
    TupleDesc tupdesc;
    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE) {
        ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                        errmsg("cp_atom: composite tuple desc unavailable")));
    }
    tupdesc = BlessTupleDesc(tupdesc);

    Datum values[ATOM_COL_COUNT];
    bool  nulls[ATOM_COL_COUNT];
    build_atom_values(cp, values, nulls);
    HeapTuple t = heap_form_tuple(tupdesc, values, nulls);
    PG_RETURN_DATUM(HeapTupleGetDatum(t));
}

/* ─── Bulk SRF over a slice or a predicate ──────────────────────────── */
typedef enum {
    UCD_SRF_RANGE,
    UCD_SRF_PRED_BLOCK,
    UCD_SRF_PRED_SCRIPT,
    UCD_SRF_PRED_GC,
} UcdSrfKind;

typedef struct UcdAtomState
{
    int32_t      cur;
    int32_t      end;     /* exclusive */
    UcdSrfKind   kind;
    int32_t      pred;
} UcdAtomState;

static Datum
ucd_atom_setof(PG_FUNCTION_ARGS, UcdSrfKind kind, int32_t start, int32_t end, int32_t pred)
{
    FuncCallContext* funcctx;
    UcdAtomState*    st;

    if (SRF_IS_FIRSTCALL()) {
        MemoryContext oldctx;
        TupleDesc     tupdesc;

        funcctx = SRF_FIRSTCALL_INIT();
        oldctx = MemoryContextSwitchTo(funcctx->multi_call_memory_ctx);

        if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE) {
            ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                            errmsg("function returning record requires composite tupdesc")));
        }
        funcctx->tuple_desc = BlessTupleDesc(tupdesc);

        st = (UcdAtomState*) palloc(sizeof(UcdAtomState));
        st->cur  = start;
        st->end  = end;
        st->kind = kind;
        st->pred = pred;
        funcctx->user_fctx = st;

        MemoryContextSwitchTo(oldctx);
    }

    funcctx = SRF_PERCALL_SETUP();
    st = (UcdAtomState*) funcctx->user_fctx;

    /* Skip non-matching codepoints inside the C loop — predicate-pushdown. */
    while (st->cur < st->end) {
        int32_t cp = st->cur++;
        bool match;
        switch (st->kind) {
            case UCD_SRF_RANGE:       match = true; break;
            case UCD_SRF_PRED_BLOCK:  match = (uc_block[cp]  == st->pred); break;
            case UCD_SRF_PRED_SCRIPT: match = (uc_script[cp] == st->pred); break;
            case UCD_SRF_PRED_GC:     match = (uc_gc[cp]     == st->pred); break;
            default:                  match = false; break;
        }
        if (!match) continue;

        Datum values[ATOM_COL_COUNT];
        bool  nulls[ATOM_COL_COUNT];
        build_atom_values(cp, values, nulls);
        HeapTuple t = heap_form_tuple(funcctx->tuple_desc, values, nulls);
        SRF_RETURN_NEXT(funcctx, HeapTupleGetDatum(t));
    }

    SRF_RETURN_DONE(funcctx);
}

PG_FUNCTION_INFO_V1(pg_ucd_codepoints);
Datum pg_ucd_codepoints(PG_FUNCTION_ARGS)
{
    int32_t start = PG_ARGISNULL(0) ? 0                     : PG_GETARG_INT32(0);
    int32_t count = PG_ARGISNULL(1) ? UNICODE_CODEPOINT_MAX : PG_GETARG_INT32(1);
    if (start < 0) start = 0;
    if (start > UNICODE_CODEPOINT_MAX) start = UNICODE_CODEPOINT_MAX;
    int64_t end64 = (int64_t) start + (int64_t) count;
    if (end64 > UNICODE_CODEPOINT_MAX) end64 = UNICODE_CODEPOINT_MAX;
    return ucd_atom_setof(fcinfo, UCD_SRF_RANGE, start, (int32_t) end64, 0);
}

PG_FUNCTION_INFO_V1(pg_ucd_codepoints_in_block);
Datum pg_ucd_codepoints_in_block(PG_FUNCTION_ARGS)
{
    int32_t block_id = PG_GETARG_INT32(0);
    return ucd_atom_setof(fcinfo, UCD_SRF_PRED_BLOCK, 0, UNICODE_CODEPOINT_MAX, block_id);
}

PG_FUNCTION_INFO_V1(pg_ucd_codepoints_in_script);
Datum pg_ucd_codepoints_in_script(PG_FUNCTION_ARGS)
{
    int32_t script_id = PG_GETARG_INT32(0);
    return ucd_atom_setof(fcinfo, UCD_SRF_PRED_SCRIPT, 0, UNICODE_CODEPOINT_MAX, script_id);
}

PG_FUNCTION_INFO_V1(pg_ucd_codepoints_with_gc);
Datum pg_ucd_codepoints_with_gc(PG_FUNCTION_ARGS)
{
    int32_t gc_id = PG_GETARG_INT32(0);
    return ucd_atom_setof(fcinfo, UCD_SRF_PRED_GC, 0, UNICODE_CODEPOINT_MAX, gc_id);
}

/* ─── Bulk hash array ⇄ codepoint array ─────────────────────────────── */
PG_FUNCTION_INFO_V1(pg_cp_hashes);
Datum pg_cp_hashes(PG_FUNCTION_ARGS)
{
    ArrayType* arr = PG_GETARG_ARRAYTYPE_P(0);
    if (ARR_NDIM(arr) > 1) {
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                        errmsg("cp_hashes: input must be 1-D int[]")));
    }
    int n = ArrayGetNItems(ARR_NDIM(arr), ARR_DIMS(arr));
    int32* in = (int32*) ARR_DATA_PTR(arr);

    Datum* out = (Datum*) palloc(sizeof(Datum) * (n == 0 ? 1 : n));
    bool*  nls = (bool*)  palloc(sizeof(bool)  * (n == 0 ? 1 : n));
    for (int i = 0; i < n; ++i) {
        int32_t cp = in[i];
        if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) { out[i] = (Datum) 0; nls[i] = true; continue; }
        const uint8_t* hsrc = huc_cp_hash_at(cp);
        if (!hsrc)                                  { out[i] = (Datum) 0; nls[i] = true; continue; }
        bytea* b = (bytea*) palloc(VARHDRSZ + CP_HASH_LEN);
        SET_VARSIZE(b, VARHDRSZ + CP_HASH_LEN);
        memcpy(VARDATA(b), hsrc, CP_HASH_LEN);
        out[i] = PointerGetDatum(b);
        nls[i] = false;
    }
    int dims[1] = { n };
    int lbs[1]  = { 1 };
    ArrayType* result = construct_md_array(out, nls, 1, dims, lbs,
                                           BYTEAOID, -1, false, TYPALIGN_INT);
    pfree(out); pfree(nls);
    PG_RETURN_ARRAYTYPE_P(result);
}

PG_FUNCTION_INFO_V1(pg_cp_from_hashes);
Datum pg_cp_from_hashes(PG_FUNCTION_ARGS)
{
    ArrayType* arr = PG_GETARG_ARRAYTYPE_P(0);
    if (ARR_NDIM(arr) > 1) {
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                        errmsg("cp_from_hashes: input must be 1-D bytea[]")));
    }
    int n = ArrayGetNItems(ARR_NDIM(arr), ARR_DIMS(arr));

    Datum* out = (Datum*) palloc(sizeof(Datum) * (n == 0 ? 1 : n));
    bool*  nls = (bool*)  palloc(sizeof(bool)  * (n == 0 ? 1 : n));

    Datum* elems;
    bool*  in_nulls;
    int    nelems;
    deconstruct_array(arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &elems, &in_nulls, &nelems);

    for (int i = 0; i < n; ++i) {
        if (in_nulls[i]) { out[i] = (Datum) 0; nls[i] = true; continue; }
        bytea* h = DatumGetByteaPP(elems[i]);
        if (VARSIZE_ANY_EXHDR(h) != CP_HASH_LEN) {
            out[i] = (Datum) 0; nls[i] = true; continue;
        }
        int32_t cp = uc_cp_from_hash((const uint8_t*) VARDATA_ANY(h));
        if (cp < 0) { out[i] = (Datum) 0; nls[i] = true; }
        else        { out[i] = Int32GetDatum(cp); nls[i] = false; }
    }
    int dims[1] = { n };
    int lbs[1]  = { 1 };
    ArrayType* result = construct_md_array(out, nls, 1, dims, lbs,
                                           INT4OID, sizeof(int32), true, TYPALIGN_INT);
    pfree(out); pfree(nls);
    PG_RETURN_ARRAYTYPE_P(result);
}

/* ─── UCA sort key over a TEXT input ────────────────────────────────── */
/* For each codepoint in the UTF-8 input, append its UCA weight blob to
 * the output bytea. Result is suitable for ORDER BY uca_sort_key(t). */

/* UTF-8 forward decode — same convention as pg_text_decompose.c. Returns
 * the codepoint and advances *pos. Defensive: U+FFFD on bad sequence. */
static int32_t utf8_advance(const uint8_t* s, int len, int* pos)
{
    if (*pos >= len) return -1;
    uint8_t b0 = s[*pos];
    if (b0 < 0x80)             { *pos += 1; return b0; }
    if ((b0 & 0xE0) == 0xC0 && *pos + 1 < len) {
        int32_t cp = ((b0 & 0x1F) << 6) | (s[*pos + 1] & 0x3F); *pos += 2; return cp;
    }
    if ((b0 & 0xF0) == 0xE0 && *pos + 2 < len) {
        int32_t cp = ((b0 & 0x0F) << 12) | ((s[*pos + 1] & 0x3F) << 6) | (s[*pos + 2] & 0x3F);
        *pos += 3; return cp;
    }
    if ((b0 & 0xF8) == 0xF0 && *pos + 3 < len) {
        int32_t cp = ((b0 & 0x07) << 18) | ((s[*pos + 1] & 0x3F) << 12) |
                     ((s[*pos + 2] & 0x3F) << 6) | (s[*pos + 3] & 0x3F);
        *pos += 4; return cp;
    }
    *pos += 1; return 0xFFFD;
}

PG_FUNCTION_INFO_V1(pg_uca_sort_key);
Datum pg_uca_sort_key(PG_FUNCTION_ARGS)
{
    text* in = PG_GETARG_TEXT_PP(0);
    const uint8_t* s = (const uint8_t*) VARDATA_ANY(in);
    int len = (int) VARSIZE_ANY_EXHDR(in);

    /* Two-pass: count total uint32 weights (3 per UCA tuple), then materialize.
     * The blob is 4 bytes per uint32 weight, big-endian for binary-collation
     * ORDER BY suitability. */
    int total = 0;
    int pos = 0;
    while (pos < len) {
        int32_t cp = utf8_advance(s, len, &pos);
        if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) continue;
        total += (int) uc_uca_len[cp] * 3;
    }
    bytea* out = (bytea*) palloc(VARHDRSZ + (size_t) total * 4);
    SET_VARSIZE(out, VARHDRSZ + (size_t) total * 4);
    uint8_t* w = (uint8_t*) VARDATA(out);
    pos = 0;
    while (pos < len) {
        int32_t cp = utf8_advance(s, len, &pos);
        if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) continue;
        uint32_t off = uc_uca_off[cp] * 3;
        uint16_t n   = (uint16_t) (uc_uca_len[cp] * 3);
        for (uint16_t i = 0; i < n; ++i) {
            uint32_t v = uc_uca_data[off + i];
            *w++ = (uint8_t) (v >> 24);
            *w++ = (uint8_t) (v >> 16);
            *w++ = (uint8_t) (v >>  8);
            *w++ = (uint8_t)  v;
        }
    }
    PG_RETURN_BYTEA_P(out);
}

PG_FUNCTION_INFO_V1(pg_cp_uca_compare);
Datum pg_cp_uca_compare(PG_FUNCTION_ARGS)
{
    int32_t a = arg_cp(PG_GETARG_INT32(0));
    int32_t b = arg_cp(PG_GETARG_INT32(1));
    int32_t ai = uc_uca_index[a];
    int32_t bi = uc_uca_index[b];
    if (ai < bi) PG_RETURN_INT32(-1);
    if (ai > bi) PG_RETURN_INT32( 1);
    PG_RETURN_INT32(0);
}

/* ─── Full case fold a string in one pass ───────────────────────────── */
/* Walks codepoints in input; for each, emits its uc_fcf[] slice if
 * non-empty, otherwise the uc_simple_case_fold[] target (which is the
 * codepoint itself for non-cased chars). Output is UTF-8 encoded text. */
static int utf8_encode_cp(int32_t cp, uint8_t* out)
{
    if (cp < 0x80)        { out[0] = (uint8_t) cp; return 1; }
    if (cp < 0x800)       { out[0] = 0xC0 | (cp >> 6); out[1] = 0x80 | (cp & 0x3F); return 2; }
    if (cp < 0x10000)     { out[0] = 0xE0 | (cp >> 12); out[1] = 0x80 | ((cp >> 6) & 0x3F); out[2] = 0x80 | (cp & 0x3F); return 3; }
    if (cp < 0x110000)    { out[0] = 0xF0 | (cp >> 18); out[1] = 0x80 | ((cp >> 12) & 0x3F); out[2] = 0x80 | ((cp >> 6) & 0x3F); out[3] = 0x80 | (cp & 0x3F); return 4; }
    /* Replace invalid with U+FFFD. */
    out[0] = 0xEF; out[1] = 0xBF; out[2] = 0xBD; return 3;
}

PG_FUNCTION_INFO_V1(pg_case_fold_text);
Datum pg_case_fold_text(PG_FUNCTION_ARGS)
{
    text* in = PG_GETARG_TEXT_PP(0);
    const uint8_t* s = (const uint8_t*) VARDATA_ANY(in);
    int len = (int) VARSIZE_ANY_EXHDR(in);

    /* Worst case: each codepoint expands to up to 3 codepoints × 4 UTF-8
     * bytes = 12 bytes per input codepoint. Bound by 12 × len. */
    size_t cap = (size_t) len * 12 + 16;
    text*  out = (text*) palloc(VARHDRSZ + cap);
    uint8_t* w = (uint8_t*) VARDATA(out);
    uint8_t* w_end = w + cap;

    int pos = 0;
    while (pos < len) {
        int32_t cp = utf8_advance(s, len, &pos);
        if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
            if (w + 3 > w_end) break;
            *w++ = 0xEF; *w++ = 0xBF; *w++ = 0xBD;
            continue;
        }
        uint16_t fl = uc_fcf_len[cp];
        if (fl > 0) {
            uint32_t off = uc_fcf_off[cp];
            for (uint16_t i = 0; i < fl; ++i) {
                int32_t f = uc_fcf_data[off + i];
                if (w + 4 > w_end) break;
                w += utf8_encode_cp(f, w);
            }
        } else {
            int32_t scf = uc_simple_case_fold[cp];
            if (scf < 0) scf = cp;
            if (w + 4 > w_end) break;
            w += utf8_encode_cp(scf, w);
        }
    }
    SET_VARSIZE(out, VARHDRSZ + (Size) (w - (uint8_t*) VARDATA(out)));
    PG_RETURN_TEXT_P(out);
}
