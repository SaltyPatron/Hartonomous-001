/*
 * text_decompose.c — native UAX #29 + BLAKE3 + 4D centroid pipeline,
 * factored out of ext/hartonomous_pg/src/pg_text_decompose.c so the SAME
 * algorithm runs in two adapters: the PG extension (SPI INSERTs into
 * substrate.*) and the C# P/Invoke surface (callback into the streaming
 * pipeline). One implementation, byte-identical hashes.
 *
 * Determinism (Law #6): codepoint hashes + centroids come from the
 * embedded UCD 17.0.0 blob loaded via hartonomous_ucd_load. UAX #29
 * boundary rules are ported from the spec verbatim. Same input → same
 * output across PG and C# callers.
 *
 * Memory: this file uses libc malloc/free and frees every allocation
 * before returning. No persistent state across calls.
 */

#include "hartonomous.h"
#include "generated/pg_unicode_version.h"
#include "generated/pg_ucd_segmentation.h"
#include "generated/pg_ucd_pictographic.h"

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#define HASH_LEN 32

/* Symbols from ucd_atoms_blob.c (compiled into this same library). */
extern const uint8_t* huc_cp_hash_at(int32_t cp);
extern const double*  huc_cp_centroid_at(int32_t cp);
extern int            hartonomous_ucd_loaded(void);

/*
 * UCD property tables are defined in pg_ucd_segmentation.c +
 * pg_ucd_pictographic.c. On Windows libhartonomous compiles those .c
 * files into itself (see CMakeLists.txt WIN32 branch). On Linux the PG
 * extension hartonomous.so owns the only copy and libhartonomous.so
 * leaves the symbols undefined.
 *
 * Declaring them weak here prevents the dynamic linker from failing the
 * libhartonomous.so load when it's pulled in as a DT_NEEDED dependency
 * of hartonomous.so — at that point hartonomous.so's symbols aren't yet
 * in the global scope, so eager resolution of the strong externs fails
 * (FATAL: undefined symbol: uc_gcb during PG startup).
 *
 * With weak bindings the runtime addresses come from hartonomous.so once
 * its RTLD_GLOBAL load completes; calls into td_gcb / td_wb / td_incb /
 * td_pict from outside a PG backend (e.g. standalone C# P/Invoke on
 * Linux) get NULL symbols, so each helper checks before dereferencing
 * and falls back to an Other / non-pictographic class.
 */
#if !defined(_WIN32) && (defined(__GNUC__) || defined(__clang__))
#pragma weak uc_gcb
#pragma weak uc_wb
#pragma weak uc_incb
#pragma weak uc_ext_pictographic_bitmap
#endif

/* Free helper that tolerates NULL. */
static inline void xfree(void* p) { if (p) free(p); }

/* ─────────────────────────────────────────────────────────────────────
 * (1) UTF-8 decode
 * ───────────────────────────────────────────────────────────────────── */
static size_t td_utf8_decode_one(const uint8_t* p, size_t len, int32_t* out)
{
    if (len == 0 || p == NULL || out == NULL) return 0;
    uint8_t b0 = p[0];
    if (b0 < 0x80) { *out = (int32_t) b0; return 1; }
    if ((b0 & 0xE0) == 0xC0) {
        if (len < 2 || (p[1] & 0xC0) != 0x80) return 0;
        int32_t cp = ((int32_t)(b0 & 0x1F) << 6) | (int32_t)(p[1] & 0x3F);
        if (cp < 0x80) return 0;
        *out = cp; return 2;
    }
    if ((b0 & 0xF0) == 0xE0) {
        if (len < 3 || (p[1] & 0xC0) != 0x80 || (p[2] & 0xC0) != 0x80) return 0;
        int32_t cp = ((int32_t)(b0 & 0x0F) << 12)
                   | ((int32_t)(p[1] & 0x3F) << 6)
                   |  (int32_t)(p[2] & 0x3F);
        if (cp < 0x800) return 0;
        if (cp >= 0xD800 && cp <= 0xDFFF) return 0;
        *out = cp; return 3;
    }
    if ((b0 & 0xF8) == 0xF0) {
        if (len < 4 || (p[1] & 0xC0) != 0x80 || (p[2] & 0xC0) != 0x80 || (p[3] & 0xC0) != 0x80) return 0;
        int32_t cp = ((int32_t)(b0 & 0x07) << 18)
                   | ((int32_t)(p[1] & 0x3F) << 12)
                   | ((int32_t)(p[2] & 0x3F) << 6)
                   |  (int32_t)(p[3] & 0x3F);
        if (cp < 0x10000 || cp > 0x10FFFF) return 0;
        *out = cp; return 4;
    }
    return 0;
}

/* Property lookups from generated tables. NULL guards cover the case
 * where libhartonomous.so was loaded standalone (no hartonomous.so
 * providing the symbols) — the weak bindings above resolve to NULL and
 * the helper returns the default-Other class. */
static inline uint8_t td_gcb(int32_t cp) {
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) return UC_GCB_Other;
    if (uc_gcb == NULL) return UC_GCB_Other;
    return uc_gcb[cp];
}
static inline uint8_t td_wb(int32_t cp) {
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) return UC_WB_Other;
    if (uc_wb == NULL) return UC_WB_Other;
    return uc_wb[cp];
}
static inline uint8_t td_incb(int32_t cp) {
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) return UC_INCB_None;
    if (uc_incb == NULL) return UC_INCB_None;
    return uc_incb[cp];
}
static inline int td_pict(int32_t cp) {
    /* uc_extended_pictographic() is a function in
     * pg_ucd_pictographic.c that internally indexes
     * uc_ext_pictographic_bitmap. The bitmap is weak-bound above; the
     * function symbol itself remains a normal extern, so we guard the
     * call site by checking the bitmap (and fall back to non-pict). */
    if (uc_ext_pictographic_bitmap == NULL) return 0;
    return uc_extended_pictographic(cp);
}

/* ─────────────────────────────────────────────────────────────────────
 * (2) Decoded codepoints
 * ───────────────────────────────────────────────────────────────────── */
typedef struct {
    int32_t* codepoints;
    int32_t  count;
} TdDecoded;

static int td_decode(const uint8_t* utf8, size_t len, TdDecoded* out)
{
    int32_t cap = (int32_t)(len + 1);
    out->codepoints = (int32_t*) malloc(sizeof(int32_t) * cap);
    out->count = 0;
    if (!out->codepoints) return -1;
    size_t pos = 0;
    while (pos < len) {
        int32_t cp;
        size_t consumed = td_utf8_decode_one(utf8 + pos, len - pos, &cp);
        if (consumed == 0) { pos++; continue; }
        out->codepoints[out->count++] = cp;
        pos += consumed;
    }
    return 0;
}

/* ─────────────────────────────────────────────────────────────────────
 * (3) UAX #29 grapheme cluster boundaries (GB1..GB13 + GB9c)
 * ───────────────────────────────────────────────────────────────────── */
typedef struct {
    int32_t* indices;
    int32_t  count;
} TdBoundaries;

static int td_grapheme_boundaries(const TdDecoded* d, TdBoundaries* out)
{
    out->indices = (int32_t*) malloc(sizeof(int32_t) * (d->count + 1));
    out->count = 0;
    if (!out->indices) return -1;
    if (d->count == 0) return 0;

    out->indices[out->count++] = 0;

    int riRun = 0;
    int chainPict = 0;
    int chainZwjAfterPict = 0;
    int incbConsonantSeen = 0;
    int incbLinkerSeen    = 0;

    for (int32_t i = 1; i < d->count; i++) {
        int32_t prev_cp = d->codepoints[i - 1];
        int32_t curr_cp = d->codepoints[i];
        uint8_t prev = td_gcb(prev_cp);
        uint8_t curr = td_gcb(curr_cp);
        int currIsPict = td_pict(curr_cp);
        uint8_t curr_incb = td_incb(curr_cp);

        int shouldBreak;

        if      (prev == UC_GCB_CR && curr == UC_GCB_LF)                                  shouldBreak = 0;
        else if (prev == UC_GCB_Control || prev == UC_GCB_CR || prev == UC_GCB_LF)        shouldBreak = 1;
        else if (curr == UC_GCB_Control || curr == UC_GCB_CR || curr == UC_GCB_LF)        shouldBreak = 1;
        else if (prev == UC_GCB_L && (curr == UC_GCB_L || curr == UC_GCB_V ||
                                      curr == UC_GCB_LV || curr == UC_GCB_LVT))            shouldBreak = 0;
        else if ((prev == UC_GCB_LV || prev == UC_GCB_V) &&
                 (curr == UC_GCB_V || curr == UC_GCB_T))                                   shouldBreak = 0;
        else if ((prev == UC_GCB_LVT || prev == UC_GCB_T) && curr == UC_GCB_T)             shouldBreak = 0;
        else if (curr == UC_GCB_Extend || curr == UC_GCB_ZWJ)                              shouldBreak = 0;
        else if (curr == UC_GCB_SpacingMark)                                               shouldBreak = 0;
        else if (prev == UC_GCB_Prepend)                                                   shouldBreak = 0;
        else if (incbConsonantSeen && incbLinkerSeen && curr_incb == UC_INCB_Consonant)    shouldBreak = 0;
        else if (chainZwjAfterPict && currIsPict)                                          shouldBreak = 0;
        else if (prev == UC_GCB_Regional_Indicator && curr == UC_GCB_Regional_Indicator)
            shouldBreak = (riRun % 2) == 0;
        else                                                                               shouldBreak = 1;

        if (shouldBreak) {
            out->indices[out->count++] = i;
            riRun = (curr == UC_GCB_Regional_Indicator) ? 1 : 0;
            chainPict = currIsPict ? 1 : 0;
            chainZwjAfterPict = 0;
            incbConsonantSeen = (curr_incb == UC_INCB_Consonant) ? 1 : 0;
            incbLinkerSeen    = 0;
        } else {
            if (curr == UC_GCB_Regional_Indicator) riRun++;
            else                                    riRun = 0;
            if (currIsPict) {
                chainPict = 1;
                chainZwjAfterPict = 0;
            } else if (curr == UC_GCB_Extend && chainPict) {
                /* Extend keeps chain alive */
            } else if (curr == UC_GCB_ZWJ && chainPict) {
                chainZwjAfterPict = 1;
            } else {
                chainPict = 0;
                chainZwjAfterPict = 0;
            }
            if (curr_incb == UC_INCB_Consonant) {
                incbConsonantSeen = 1;
                incbLinkerSeen    = 0;
            } else if (incbConsonantSeen &&
                       (curr_incb == UC_INCB_Linker || curr_incb == UC_INCB_Extend)) {
                if (curr_incb == UC_INCB_Linker) incbLinkerSeen = 1;
            } else {
                incbConsonantSeen = 0;
                incbLinkerSeen    = 0;
            }
        }
    }
    return 0;
}

/* ─────────────────────────────────────────────────────────────────────
 * (4) UAX #29 word boundaries (WB1..WB16 + WB999)
 * ───────────────────────────────────────────────────────────────────── */
typedef struct {
    int32_t* indices;
    int32_t  count;
} TdWords;

static int td_word_boundaries(const TdDecoded* d, TdWords* out)
{
    out->indices = (int32_t*) malloc(sizeof(int32_t) * (d->count + 1));
    out->count = 0;
    if (!out->indices) return -1;
    if (d->count == 0) return 0;

    out->indices[out->count++] = 0;

    uint8_t prev_lit = td_wb(d->codepoints[0]);
    uint8_t prevSig  = td_wb(d->codepoints[0]);
    uint8_t prev2Sig = UC_WB_Other;
    int     riRun    = (prevSig == UC_WB_Regional_Indicator) ? 1 : 0;

    for (int32_t i = 1; i < d->count; i++) {
        int32_t prev_cp = d->codepoints[i - 1];
        int32_t curr_cp = d->codepoints[i];
        uint8_t prev_l = td_wb(prev_cp);
        uint8_t curr   = td_wb(curr_cp);

        int shouldBreak;

        if      (prev_l == UC_WB_CR && curr == UC_WB_LF) shouldBreak = 0;
        else if (prev_l == UC_WB_Newline || prev_l == UC_WB_CR || prev_l == UC_WB_LF) shouldBreak = 1;
        else if (curr == UC_WB_Newline || curr == UC_WB_CR || curr == UC_WB_LF)       shouldBreak = 1;
        else if (prev_l == UC_WB_ZWJ && td_pict(curr_cp))                              shouldBreak = 0;
        else if (prev_l == UC_WB_WSegSpace && curr == UC_WB_WSegSpace)                 shouldBreak = 0;
        else if (curr == UC_WB_Extend || curr == UC_WB_Format || curr == UC_WB_ZWJ)    shouldBreak = 0;
        else if ((prevSig == UC_WB_ALetter || prevSig == UC_WB_Hebrew_Letter) &&
                 (curr    == UC_WB_ALetter || curr    == UC_WB_Hebrew_Letter))         shouldBreak = 0;
        else if ((prevSig == UC_WB_ALetter || prevSig == UC_WB_Hebrew_Letter) &&
                 (curr == UC_WB_MidLetter || curr == UC_WB_MidNumLet ||
                  curr == UC_WB_Single_Quote)) {
            int32_t k = i + 1;
            while (k < d->count) {
                uint8_t wbk = td_wb(d->codepoints[k]);
                if (wbk == UC_WB_Extend || wbk == UC_WB_Format || wbk == UC_WB_ZWJ) { k++; continue; }
                break;
            }
            int aheadIsLetter = (k < d->count) &&
                (td_wb(d->codepoints[k]) == UC_WB_ALetter ||
                 td_wb(d->codepoints[k]) == UC_WB_Hebrew_Letter);
            shouldBreak = aheadIsLetter ? 0 : 1;
        }
        else if ((prev2Sig == UC_WB_ALetter || prev2Sig == UC_WB_Hebrew_Letter) &&
                 (prevSig == UC_WB_MidLetter || prevSig == UC_WB_MidNumLet ||
                  prevSig == UC_WB_Single_Quote) &&
                 (curr == UC_WB_ALetter || curr == UC_WB_Hebrew_Letter))               shouldBreak = 0;
        else if (prevSig == UC_WB_Hebrew_Letter && curr == UC_WB_Single_Quote)         shouldBreak = 0;
        else if (prevSig == UC_WB_Hebrew_Letter && curr == UC_WB_Double_Quote) {
            int32_t k = i + 1;
            while (k < d->count) {
                uint8_t wbk = td_wb(d->codepoints[k]);
                if (wbk == UC_WB_Extend || wbk == UC_WB_Format || wbk == UC_WB_ZWJ) { k++; continue; }
                break;
            }
            int aheadHeb = (k < d->count) && td_wb(d->codepoints[k]) == UC_WB_Hebrew_Letter;
            shouldBreak = aheadHeb ? 0 : 1;
        }
        else if (prev2Sig == UC_WB_Hebrew_Letter && prevSig == UC_WB_Double_Quote &&
                 curr == UC_WB_Hebrew_Letter)                                          shouldBreak = 0;
        else if (prevSig == UC_WB_Numeric && curr == UC_WB_Numeric)                    shouldBreak = 0;
        else if ((prevSig == UC_WB_ALetter || prevSig == UC_WB_Hebrew_Letter) &&
                 curr == UC_WB_Numeric)                                                shouldBreak = 0;
        else if (prevSig == UC_WB_Numeric &&
                 (curr == UC_WB_ALetter || curr == UC_WB_Hebrew_Letter))               shouldBreak = 0;
        else if (prev2Sig == UC_WB_Numeric &&
                 (prevSig == UC_WB_MidNum || prevSig == UC_WB_MidNumLet ||
                  prevSig == UC_WB_Single_Quote) &&
                 curr == UC_WB_Numeric)                                                shouldBreak = 0;
        else if (prevSig == UC_WB_Numeric &&
                 (curr == UC_WB_MidNum || curr == UC_WB_MidNumLet ||
                  curr == UC_WB_Single_Quote)) {
            int32_t k = i + 1;
            while (k < d->count) {
                uint8_t wbk = td_wb(d->codepoints[k]);
                if (wbk == UC_WB_Extend || wbk == UC_WB_Format || wbk == UC_WB_ZWJ) { k++; continue; }
                break;
            }
            int aheadNum = (k < d->count) && td_wb(d->codepoints[k]) == UC_WB_Numeric;
            shouldBreak = aheadNum ? 0 : 1;
        }
        else if (prevSig == UC_WB_Katakana && curr == UC_WB_Katakana)                  shouldBreak = 0;
        else if ((prevSig == UC_WB_ALetter || prevSig == UC_WB_Hebrew_Letter ||
                  prevSig == UC_WB_Numeric || prevSig == UC_WB_Katakana ||
                  prevSig == UC_WB_ExtendNumLet) && curr == UC_WB_ExtendNumLet)        shouldBreak = 0;
        else if (prevSig == UC_WB_ExtendNumLet &&
                 (curr == UC_WB_ALetter || curr == UC_WB_Hebrew_Letter ||
                  curr == UC_WB_Numeric || curr == UC_WB_Katakana))                    shouldBreak = 0;
        else if (prevSig == UC_WB_Regional_Indicator && curr == UC_WB_Regional_Indicator)
            shouldBreak = (riRun % 2) == 0;
        else                                                                            shouldBreak = 1;

        if (shouldBreak) {
            out->indices[out->count++] = i;
            riRun = (curr == UC_WB_Regional_Indicator) ? 1 : 0;
        }

        prev_lit = curr;
        if (curr != UC_WB_Extend && curr != UC_WB_Format && curr != UC_WB_ZWJ) {
            prev2Sig = prevSig;
            prevSig  = curr;
            if (!shouldBreak && curr == UC_WB_Regional_Indicator) riRun++;
        }
    }
    return 0;
}

/* ─────────────────────────────────────────────────────────────────────
 * (5) Hashes + centroids
 * ───────────────────────────────────────────────────────────────────── */
static void td_hash_codepoints(const TdDecoded* d, uint8_t* out_hashes)
{
    for (int32_t i = 0; i < d->count; i++) {
        int32_t cp = d->codepoints[i];
        const uint8_t* h = (cp >= 0 && cp < UNICODE_CODEPOINT_MAX)
                         ? huc_cp_hash_at(cp) : NULL;
        if (h) memcpy(out_hashes + (size_t) i * HASH_LEN, h, HASH_LEN);
        else   memset(out_hashes + (size_t) i * HASH_LEN, 0, HASH_LEN);
    }
}

static void td_centroids_codepoints(const TdDecoded* d, double* out)
{
    for (int32_t i = 0; i < d->count; i++) {
        int32_t cp = d->codepoints[i];
        const double* c = (cp >= 0 && cp < UNICODE_CODEPOINT_MAX)
                        ? huc_cp_centroid_at(cp) : NULL;
        if (c) {
            out[i*4+0] = c[0]; out[i*4+1] = c[1];
            out[i*4+2] = c[2]; out[i*4+3] = c[3];
        } else {
            out[i*4+0] = out[i*4+1] = out[i*4+2] = out[i*4+3] = 0.0;
        }
    }
}

static void td_mean_centroid(const double* in, int32_t k, double* out)
{
    if (k <= 0) { out[0]=out[1]=out[2]=out[3]=0.0; return; }
    double sx=0, sy=0, sz=0, sm=0;
    for (int32_t i=0; i<k; i++) {
        sx += in[i*4+0]; sy += in[i*4+1]; sz += in[i*4+2]; sm += in[i*4+3];
    }
    double inv = 1.0 / (double) k;
    out[0] = sx*inv; out[1] = sy*inv; out[2] = sz*inv; out[3] = sm*inv;
}

/* ─────────────────────────────────────────────────────────────────────
 * (6) EWKB encoders for POINTZM and LINESTRINGZM
 * Type word: 0xC0000001 (POINT|Z|M), 0xC0000002 (LINESTRING|Z|M).
 * ───────────────────────────────────────────────────────────────────── */
static void td_pointzm_wkb(double x, double y, double z, double m, uint8_t out[37])
{
    out[0] = 0x01;
    uint32_t type = 0xC0000001u;
    memcpy(out + 1,  &type, 4);
    memcpy(out + 5,  &x, 8);
    memcpy(out + 13, &y, 8);
    memcpy(out + 21, &z, 8);
    memcpy(out + 29, &m, 8);
}

static uint8_t* td_linestringzm_wkb(const double* verts, int k, size_t* out_len)
{
    size_t sz = 1 + 4 + 4 + (size_t) k * 32;
    uint8_t* buf = (uint8_t*) malloc(sz);
    if (!buf) { *out_len = 0; return NULL; }
    buf[0] = 0x01;
    uint32_t type = 0xC0000002u;
    memcpy(buf + 1, &type, 4);
    uint32_t n = (uint32_t) k;
    memcpy(buf + 5, &n, 4);
    uint8_t* vp = buf + 9;
    for (int i = 0; i < k; i++) {
        memcpy(vp + 0,  &verts[i*4+0], 8);
        memcpy(vp + 8,  &verts[i*4+1], 8);
        memcpy(vp + 16, &verts[i*4+2], 8);
        memcpy(vp + 24, &verts[i*4+3], 8);
        vp += 32;
    }
    *out_len = sz;
    return buf;
}

/* ─────────────────────────────────────────────────────────────────────
 * (7) Emission helpers — invoke the user's callback for one record.
 *
 * Returns the callback's return value; non-zero aborts the walk.
 * ───────────────────────────────────────────────────────────────────── */

#define EMIT(rec_init) do { \
    hartonomous_text_record_t r = rec_init; \
    int rc = emit(ctx, &r); \
    if (rc != 0) { goto out_abort; } \
} while (0)

/* ─────────────────────────────────────────────────────────────────────
 * (8) Public entry point
 * ───────────────────────────────────────────────────────────────────── */
int hartonomous_text_decompose(
    const uint8_t* utf8,
    size_t utf8_len,
    int top_kind,
    double trust_mu,
    hartonomous_text_emit_cb emit,
    void* ctx,
    uint8_t out_root_hash[HARTONOMOUS_HASH_LEN],
    int* out_root_kind,
    double out_root_centroid[4])
{
    if (!utf8 || !emit || !out_root_hash) return -1;
    if (!hartonomous_ucd_loaded()) return -2;
    if (utf8_len == 0) return -3;

    int rc_final = 0;

    /* All allocations tracked here so the abort path can free them. */
    TdDecoded     d   = {0};
    TdBoundaries  g   = {0};
    TdWords       w   = {0};
    uint8_t* cp_h     = NULL;
    double*  cp_c     = NULL;
    uint8_t* gc_h     = NULL;
    double*  gc_c     = NULL;
    uint8_t* w_h      = NULL;
    double*  w_c      = NULL;
    uint8_t* ls_buf   = NULL;

    if (td_decode(utf8, utf8_len, &d) != 0) { rc_final = -9; goto out_cleanup; }
    if (d.count == 0) { rc_final = -3; goto out_cleanup; }

    cp_h = (uint8_t*) malloc((size_t) d.count * HASH_LEN);
    cp_c = (double*)  malloc(sizeof(double) * 4 * d.count);
    if (!cp_h || !cp_c) { rc_final = -9; goto out_cleanup; }

    td_hash_codepoints(&d, cp_h);
    td_centroids_codepoints(&d, cp_c);

    if (td_grapheme_boundaries(&d, &g) != 0) { rc_final = -9; goto out_cleanup; }
    if (td_word_boundaries(&d,    &w) != 0) { rc_final = -9; goto out_cleanup; }

    int gN = g.count;
    int wN = w.count;

    gc_h = (uint8_t*) malloc((size_t) gN * HASH_LEN);
    gc_c = (double*)  malloc(sizeof(double) * 4 * gN);
    w_h  = (uint8_t*) malloc((size_t) wN * HASH_LEN);
    w_c  = (double*)  malloc(sizeof(double) * 4 * wN);
    if (!gc_h || !gc_c || !w_h || !w_c) { rc_final = -9; goto out_cleanup; }

    /* Grapheme hashes (Merkle) + centroids (mean). */
    for (int gi = 0; gi < gN; gi++) {
        int firstCp = g.indices[gi];
        int endCp   = (gi + 1 < gN) ? g.indices[gi + 1] : d.count;
        int cpCount = endCp - firstCp;
        hartonomous_blake3_merkle(cp_h + (size_t) firstCp * HASH_LEN, (size_t) cpCount,
                                  gc_h + (size_t) gi * HASH_LEN);
        td_mean_centroid(cp_c + firstCp * 4, cpCount, gc_c + gi * 4);
    }

    /* Word hashes (Merkle of grapheme hashes covering range) + centroids. */
    for (int wi = 0; wi < wN; wi++) {
        int firstCpW = w.indices[wi];
        int endCpW   = (wi + 1 < wN) ? w.indices[wi + 1] : d.count;
        int firstGc = 0, endGc = gN;
        for (int gi = 0; gi < gN; gi++) {
            if (g.indices[gi] == firstCpW) { firstGc = gi; break; }
        }
        for (int gi = firstGc; gi < gN; gi++) {
            if (g.indices[gi] >= endCpW) { endGc = gi; break; }
        }
        int gcCount = endGc - firstGc;
        if (gcCount <= 0) gcCount = 1;
        hartonomous_blake3_merkle(gc_h + (size_t) firstGc * HASH_LEN, (size_t) gcCount,
                                  w_h  + (size_t) wi * HASH_LEN);
        td_mean_centroid(gc_c + firstGc * 4, gcCount, w_c + wi * 4);
    }

    /* Composition root: Merkle of word hashes, mean centroid of word centroids. */
    uint8_t comp_h[HASH_LEN];
    double  comp_c[4];
    if (wN > 0) {
        hartonomous_blake3_merkle(w_h, (size_t) wN, comp_h);
        td_mean_centroid(w_c, wN, comp_c);
    } else {
        hartonomous_blake3_merkle(NULL, 0, comp_h);
        comp_c[0]=comp_c[1]=comp_c[2]=comp_c[3]=0.0;
    }

    /* ── Emission ─────────────────────────────────────────────────── */

    /* Codepoints: entity + classification + s3_position physicality + significance. */
    for (int i = 0; i < d.count; i++) {
        const uint8_t* h = cp_h + (size_t) i * HASH_LEN;
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_ENTITY, .subkind = HARTONOMOUS_KIND_CODEPOINT,
            .hash_a = h
        }));
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_CLASSIFICATION, .subkind = HARTONOMOUS_KIND_CODEPOINT,
            .hash_a = h
        }));
        uint8_t pt[37];
        td_pointzm_wkb(cp_c[i*4+0], cp_c[i*4+1], cp_c[i*4+2], cp_c[i*4+3], pt);
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_PHYSICALITY, .subkind = HARTONOMOUS_PHYS_S3_POSITION,
            .hash_a = h, .hash_b = h,
            .wkb = pt, .wkb_len = 37
        }));
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_SIGNIFICANCE, .subkind = HARTONOMOUS_SIG_SOURCE_AUTHORITY,
            .hash_a = h, .double_param = trust_mu
        }));
    }

    /* Grapheme clusters. */
    for (int gi = 0; gi < gN; gi++) {
        int firstCp = g.indices[gi];
        int endCp   = (gi + 1 < gN) ? g.indices[gi + 1] : d.count;
        int cpCount = endCp - firstCp;
        const uint8_t* gh = gc_h + (size_t) gi * HASH_LEN;
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_ENTITY, .subkind = HARTONOMOUS_KIND_GRAPHEME_CLUSTER,
            .hash_a = gh
        }));
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_CLASSIFICATION, .subkind = HARTONOMOUS_KIND_GRAPHEME_CLUSTER,
            .hash_a = gh
        }));
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_SIGNIFICANCE, .subkind = HARTONOMOUS_SIG_SOURCE_AUTHORITY,
            .hash_a = gh, .double_param = trust_mu
        }));
        if (cpCount == 1) {
            uint8_t pt[37];
            td_pointzm_wkb(gc_c[gi*4+0], gc_c[gi*4+1], gc_c[gi*4+2], gc_c[gi*4+3], pt);
            EMIT(((hartonomous_text_record_t){
                .kind = HARTONOMOUS_REC_PHYSICALITY, .subkind = HARTONOMOUS_PHYS_S3_POSITION,
                .hash_a = gh, .hash_b = gh,
                .wkb = pt, .wkb_len = 37
            }));
        } else if (cpCount > 1) {
            size_t ls_len;
            xfree(ls_buf);
            ls_buf = td_linestringzm_wkb(cp_c + firstCp * 4, cpCount, &ls_len);
            if (!ls_buf) { rc_final = -9; goto out_cleanup; }
            EMIT(((hartonomous_text_record_t){
                .kind = HARTONOMOUS_REC_PHYSICALITY, .subkind = HARTONOMOUS_PHYS_CONTOUR,
                .hash_a = gh, .hash_b = gh,
                .wkb = ls_buf, .wkb_len = ls_len
            }));
        }
        for (int k = 0; k < cpCount; k++) {
            const uint8_t* ch = cp_h + (size_t) (firstCp + k) * HASH_LEN;
            EMIT(((hartonomous_text_record_t){
                .kind = HARTONOMOUS_REC_SEQUENCE,
                .hash_a = gh, .hash_b = ch,
                .int_param = k + 1
            }));
        }
    }

    /* Word forms / compositions per word range. */
    for (int wi = 0; wi < wN; wi++) {
        int firstCpW = w.indices[wi];
        int endCpW   = (wi + 1 < wN) ? w.indices[wi + 1] : d.count;
        int firstGc = 0, endGc = gN;
        for (int gi = 0; gi < gN; gi++) {
            if (g.indices[gi] == firstCpW) { firstGc = gi; break; }
        }
        for (int gi = firstGc; gi < gN; gi++) {
            if (g.indices[gi] >= endCpW) { endGc = gi; break; }
        }
        int gcCount = endGc - firstGc;
        if (gcCount <= 0) gcCount = 1;

        const uint8_t* wh = w_h + (size_t) wi * HASH_LEN;
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_ENTITY, .subkind = HARTONOMOUS_KIND_WORD_FORM,
            .hash_a = wh
        }));
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_CLASSIFICATION, .subkind = HARTONOMOUS_KIND_WORD_FORM,
            .hash_a = wh
        }));
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_SIGNIFICANCE, .subkind = HARTONOMOUS_SIG_SOURCE_AUTHORITY,
            .hash_a = wh, .double_param = trust_mu
        }));
        if (gcCount == 1) {
            uint8_t pt[37];
            td_pointzm_wkb(w_c[wi*4+0], w_c[wi*4+1], w_c[wi*4+2], w_c[wi*4+3], pt);
            EMIT(((hartonomous_text_record_t){
                .kind = HARTONOMOUS_REC_PHYSICALITY, .subkind = HARTONOMOUS_PHYS_S3_POSITION,
                .hash_a = wh, .hash_b = wh,
                .wkb = pt, .wkb_len = 37
            }));
        } else if (gcCount > 1) {
            size_t ls_len;
            xfree(ls_buf);
            ls_buf = td_linestringzm_wkb(gc_c + firstGc * 4, gcCount, &ls_len);
            if (!ls_buf) { rc_final = -9; goto out_cleanup; }
            EMIT(((hartonomous_text_record_t){
                .kind = HARTONOMOUS_REC_PHYSICALITY, .subkind = HARTONOMOUS_PHYS_CONTOUR,
                .hash_a = wh, .hash_b = wh,
                .wkb = ls_buf, .wkb_len = ls_len
            }));
        }
        for (int k = 0; k < gcCount; k++) {
            const uint8_t* ch = gc_h + (size_t) (firstGc + k) * HASH_LEN;
            EMIT(((hartonomous_text_record_t){
                .kind = HARTONOMOUS_REC_SEQUENCE,
                .hash_a = wh, .hash_b = ch,
                .int_param = k + 1
            }));
        }
    }

    /* Composition root. */
    EMIT(((hartonomous_text_record_t){
        .kind = HARTONOMOUS_REC_ENTITY, .subkind = top_kind,
        .hash_a = comp_h
    }));
    EMIT(((hartonomous_text_record_t){
        .kind = HARTONOMOUS_REC_CLASSIFICATION, .subkind = top_kind,
        .hash_a = comp_h
    }));
    EMIT(((hartonomous_text_record_t){
        .kind = HARTONOMOUS_REC_SIGNIFICANCE, .subkind = HARTONOMOUS_SIG_SOURCE_AUTHORITY,
        .hash_a = comp_h, .double_param = trust_mu
    }));
    if (wN == 1) {
        uint8_t pt[37];
        td_pointzm_wkb(comp_c[0], comp_c[1], comp_c[2], comp_c[3], pt);
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_PHYSICALITY, .subkind = HARTONOMOUS_PHYS_S3_POSITION,
            .hash_a = comp_h, .hash_b = comp_h,
            .wkb = pt, .wkb_len = 37
        }));
    } else if (wN > 1) {
        size_t ls_len;
        xfree(ls_buf);
        ls_buf = td_linestringzm_wkb(w_c, wN, &ls_len);
        if (!ls_buf) { rc_final = -9; goto out_cleanup; }
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_PHYSICALITY, .subkind = HARTONOMOUS_PHYS_CONTOUR,
            .hash_a = comp_h, .hash_b = comp_h,
            .wkb = ls_buf, .wkb_len = ls_len
        }));
    }
    for (int k = 0; k < wN; k++) {
        const uint8_t* ch = w_h + (size_t) k * HASH_LEN;
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_SEQUENCE,
            .hash_a = comp_h, .hash_b = ch,
            .int_param = k + 1
        }));
    }

    memcpy(out_root_hash, comp_h, HASH_LEN);
    if (out_root_kind) *out_root_kind = top_kind;
    if (out_root_centroid) {
        out_root_centroid[0] = comp_c[0];
        out_root_centroid[1] = comp_c[1];
        out_root_centroid[2] = comp_c[2];
        out_root_centroid[3] = comp_c[3];
    }
    rc_final = 0;
    goto out_cleanup;

out_abort:
    /* The callback returned non-zero. Propagate that as the function's
     * return code, after freeing buffers. */
    rc_final = 1;

out_cleanup:
    xfree(d.codepoints);
    xfree(g.indices);
    xfree(w.indices);
    xfree(cp_h);
    xfree(cp_c);
    xfree(gc_h);
    xfree(gc_c);
    xfree(w_h);
    xfree(w_c);
    xfree(ls_buf);
    return rc_final;
}
