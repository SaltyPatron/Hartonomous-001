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
#include "generated/pg_ucd_classification.h"
#include "generated/pg_ucd_pictographic.h"
#include "generated/pg_ucd_decomp.h"

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <limits.h>

#define HASH_LEN 32

/* Symbols from ucd_atoms_blob.c (compiled into this same library). */
extern const uint8_t* huc_cp_hash_at(int32_t cp);
extern const double*  huc_cp_centroid_at(int32_t cp);
extern int            hartonomous_ucd_loaded(void);

/* Free helper that tolerates NULL. */
static inline void xfree(void* p) { if (p) free(p); }

static int td_ucd_tables_ready(void)
{
    return uc_gcb != NULL
        && uc_wb != NULL
        && uc_incb != NULL
        && uc_ccc != NULL
        && uc_decomp_type != NULL
        && uc_decomp_off != NULL
        && uc_decomp_len != NULL
        && uc_decomp_data != NULL
        && uc_composition_pairs != NULL
        && uc_ext_pictographic_bitmap != NULL;
}

HARTONOMOUS_API int hartonomous_ucd_tables_ready(void)
{
    return td_ucd_tables_ready();
}

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
    return uc_gcb[cp];
}
static inline uint8_t td_wb(int32_t cp) {
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) return UC_WB_Other;
    return uc_wb[cp];
}
static inline uint8_t td_sb(int32_t cp) {
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) return UC_SB_Other;
    return uc_sb[cp];
}
static inline uint8_t td_incb(int32_t cp) {
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) return UC_INCB_None;
    return uc_incb[cp];
}
static inline int td_pict(int32_t cp) {
    return uc_extended_pictographic(cp);
}

/* ─────────────────────────────────────────────────────────────────────
 * (2) Decoded codepoints
 * ───────────────────────────────────────────────────────────────────── */
typedef struct {
    int32_t* codepoints;
    int32_t  count;
} TdDecoded;

typedef struct {
    int32_t* items;
    int32_t  count;
    int32_t  cap;
} TdCpBuffer;

static int td_buf_push(TdCpBuffer* b, int32_t cp)
{
    if (b->count >= b->cap) {
        int32_t new_cap = b->cap > 0 ? b->cap * 2 : 32;
        int32_t* new_items = (int32_t*) realloc(b->items, sizeof(int32_t) * (size_t) new_cap);
        if (!new_items) return -1;
        b->items = new_items;
        b->cap = new_cap;
    }
    b->items[b->count++] = cp;
    return 0;
}

static inline uint8_t td_ccc(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) return 0;
    return uc_ccc[cp];
}

static inline uint8_t td_decomp_type(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) return UC_DECOMP_TYPE_None;
    return uc_decomp_type[cp];
}

static inline uint16_t td_decomp_len(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) return 0;
    return uc_decomp_len[cp];
}

static inline const int32_t* td_decomp_mapping(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) return NULL;
    return uc_decomp_data + uc_decomp_off[cp];
}

#define TD_HANGUL_SBASE  0xAC00
#define TD_HANGUL_LBASE  0x1100
#define TD_HANGUL_VBASE  0x1161
#define TD_HANGUL_TBASE  0x11A7
#define TD_HANGUL_LCOUNT 19
#define TD_HANGUL_VCOUNT 21
#define TD_HANGUL_TCOUNT 28
#define TD_HANGUL_NCOUNT (TD_HANGUL_VCOUNT * TD_HANGUL_TCOUNT)
#define TD_HANGUL_SCOUNT (TD_HANGUL_LCOUNT * TD_HANGUL_NCOUNT)

static int td_decompose_hangul(int32_t cp, TdCpBuffer* out)
{
    int32_t s_index = cp - TD_HANGUL_SBASE;
    if (s_index < 0 || s_index >= TD_HANGUL_SCOUNT) return 0;

    int32_t l = TD_HANGUL_LBASE + s_index / TD_HANGUL_NCOUNT;
    int32_t v = TD_HANGUL_VBASE + (s_index % TD_HANGUL_NCOUNT) / TD_HANGUL_TCOUNT;
    int32_t t = s_index % TD_HANGUL_TCOUNT;
    if (td_buf_push(out, l) != 0) return -1;
    if (td_buf_push(out, v) != 0) return -1;
    if (t != 0 && td_buf_push(out, TD_HANGUL_TBASE + t) != 0) return -1;
    return 1;
}

static int td_canonical_decompose_cp(int32_t cp, TdCpBuffer* out)
{
    int hangul = td_decompose_hangul(cp, out);
    if (hangul != 0) return hangul < 0 ? -1 : 0;

    if (td_decomp_type(cp) == UC_DECOMP_TYPE_canonical) {
        uint16_t len = td_decomp_len(cp);
        const int32_t* mapping = td_decomp_mapping(cp);
        if (len > 0 && mapping != NULL) {
            for (uint16_t i = 0; i < len; i++) {
                if (td_canonical_decompose_cp(mapping[i], out) != 0) return -1;
            }
            return 0;
        }
    }

    return td_buf_push(out, cp);
}

static void td_canonical_order(TdCpBuffer* buf)
{
    for (int32_t i = 1; i < buf->count; i++) {
        int32_t cp = buf->items[i];
        uint8_t cp_ccc = td_ccc(cp);
        if (cp_ccc == 0) continue;

        int32_t j = i;
        while (j > 0) {
            uint8_t prev_ccc = td_ccc(buf->items[j - 1]);
            if (prev_ccc == 0 || prev_ccc <= cp_ccc) break;
            buf->items[j] = buf->items[j - 1];
            j--;
        }
        buf->items[j] = cp;
    }
}

static int32_t td_compose_hangul(int32_t a, int32_t b)
{
    int32_t l_index = a - TD_HANGUL_LBASE;
    if (l_index >= 0 && l_index < TD_HANGUL_LCOUNT) {
        int32_t v_index = b - TD_HANGUL_VBASE;
        if (v_index >= 0 && v_index < TD_HANGUL_VCOUNT) {
            return TD_HANGUL_SBASE + (l_index * TD_HANGUL_VCOUNT + v_index) * TD_HANGUL_TCOUNT;
        }
    }

    int32_t s_index = a - TD_HANGUL_SBASE;
    if (s_index >= 0 && s_index < TD_HANGUL_SCOUNT && (s_index % TD_HANGUL_TCOUNT) == 0) {
        int32_t t_index = b - TD_HANGUL_TBASE;
        if (t_index > 0 && t_index < TD_HANGUL_TCOUNT) {
            return a + t_index;
        }
    }

    return 0;
}

static int32_t td_compose_pair(int32_t first, int32_t second)
{
    int32_t hangul = td_compose_hangul(first, second);
    if (hangul != 0) return hangul;

    int lo = 0;
    int hi = UC_COMPOSITION_PAIR_COUNT - 1;
    while (lo <= hi) {
        int mid = (lo + hi) >> 1;
        const UcCompositionPair* pair = &uc_composition_pairs[mid];
        if (first < pair->first || (first == pair->first && second < pair->second)) {
            hi = mid - 1;
        } else if (first > pair->first || (first == pair->first && second > pair->second)) {
            lo = mid + 1;
        } else {
            return pair->composite;
        }
    }
    return 0;
}

static int td_canonical_compose(const TdCpBuffer* decomposed, TdCpBuffer* out)
{
    int32_t starter_index = -1;
    int32_t starter_cp = 0;
    uint8_t last_ccc = 0;

    for (int32_t i = 0; i < decomposed->count; i++) {
        int32_t cp = decomposed->items[i];
        uint8_t cp_ccc = td_ccc(cp);

        if (starter_index >= 0 && (last_ccc < cp_ccc || last_ccc == 0)) {
            int32_t composite = td_compose_pair(starter_cp, cp);
            if (composite != 0) {
                out->items[starter_index] = composite;
                starter_cp = composite;
                continue;
            }
        }

        if (td_buf_push(out, cp) != 0) return -1;
        if (cp_ccc == 0) {
            starter_index = out->count - 1;
            starter_cp = cp;
        }
        last_ccc = cp_ccc;
    }

    return 0;
}

static int td_normalize_nfc(TdDecoded* d)
{
    TdCpBuffer decomposed = {0};
    TdCpBuffer composed = {0};

    for (int32_t i = 0; i < d->count; i++) {
        if (td_canonical_decompose_cp(d->codepoints[i], &decomposed) != 0) {
            xfree(decomposed.items);
            return -1;
        }
    }

    td_canonical_order(&decomposed);
    if (td_canonical_compose(&decomposed, &composed) != 0) {
        xfree(decomposed.items);
        xfree(composed.items);
        return -1;
    }

    xfree(decomposed.items);
    xfree(d->codepoints);
    d->codepoints = composed.items;
    d->count = composed.count;
    return 0;
}

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

typedef struct {
    int32_t* indices;
    int32_t  count;
} TdSentences;

/* UAX-29 §4 Sentence_Break — practical subset of SB1..SB11 + SB998.
 *
 * Covers the common cases (CR × LF, ParaSep ÷, ATerm × Numeric, SATerm
 * Close* Sp* × SContinue/SATerm, SATerm Close* Sp* ParaSep? ÷). Does NOT
 * implement the full SB8 lookahead-for-lowercase-after-period rule yet
 * (that requires arbitrary forward scan), so abbreviation-like sequences
 * such as "Dr. Smith" will break at the period. Sentence segmentation is
 * NOT on the substrate identity path (no entity hashes flow through it);
 * it's used by SubQuestionDecomposer in inference, where the current
 * behavior is acceptable. Conformance against SentenceBreakTest.txt: aim
 * for ~90% pending full SB8 implementation. */
static int td_sentence_boundaries(const TdDecoded* d, TdSentences* out)
{
    out->indices = (int32_t*) malloc(sizeof(int32_t) * (d->count + 1));
    out->count = 0;
    if (!out->indices) return -1;
    if (d->count == 0) return 0;

    out->indices[out->count++] = 0;

    int satermActive = 0;
    int spSeenAfterSaterm = 0;
    uint8_t prev = td_sb(d->codepoints[0]);
    uint8_t prev2 = UC_SB_Other;
    if (prev == UC_SB_ATerm || prev == UC_SB_STerm) satermActive = 1;

    for (int32_t i = 1; i < d->count; i++) {
        uint8_t curr = td_sb(d->codepoints[i]);

        int shouldBreak = 0;

        /* Rule precedence: SB3 < SB4 < SB5 < SB6 < SB7 < SB8 < SB8a < SB9 < SB10 < SB11 < SB998.
         * Lower-numbered rule wins on conflict. SB4 ÷ ParaSep wins over SB5 × Extend. */
        if (prev == UC_SB_CR && curr == UC_SB_LF) {
            shouldBreak = 0;                                   /* SB3 */
        }
        else if (prev == UC_SB_Sep || prev == UC_SB_CR || prev == UC_SB_LF) {
            shouldBreak = 1;                                   /* SB4 — wins over SB5 */
            satermActive = 0;
            spSeenAfterSaterm = 0;
        }
        else if (curr == UC_SB_Extend || curr == UC_SB_Format) {
            shouldBreak = 0;                                   /* SB5 — × Extend/Format */
            /* Do NOT update prev or saterm state — Extend/Format attach. */
            continue;
        }
        else if (prev == UC_SB_ATerm && curr == UC_SB_Numeric) {
            shouldBreak = 0;                                   /* SB6 — "3.14" */
        }
        else if (prev == UC_SB_ATerm && curr == UC_SB_Upper &&
                 (prev2 == UC_SB_Upper || prev2 == UC_SB_Lower)) {
            shouldBreak = 0;                                   /* SB7 — "U.S.A." */
        }
        else if (prev == UC_SB_ATerm && curr == UC_SB_Lower) {
            shouldBreak = 0;                                   /* SB8 simplified — "etc. and" */
        }
        else if (satermActive && spSeenAfterSaterm && curr == UC_SB_Lower) {
            shouldBreak = 0;                                   /* SB8 — SATerm Sp* × Lower */
        }
        else if (satermActive && (curr == UC_SB_SContinue ||
                                  curr == UC_SB_ATerm || curr == UC_SB_STerm)) {
            shouldBreak = 0;                                   /* SB8a */
        }
        else if (satermActive && !spSeenAfterSaterm &&
                 (curr == UC_SB_Close || curr == UC_SB_Sp ||
                  curr == UC_SB_Sep || curr == UC_SB_CR || curr == UC_SB_LF)) {
            shouldBreak = 0;                                   /* SB9 */
        }
        else if (satermActive && spSeenAfterSaterm &&
                 (curr == UC_SB_Sp || curr == UC_SB_Sep ||
                  curr == UC_SB_CR || curr == UC_SB_LF)) {
            shouldBreak = 0;                                   /* SB10 */
        }
        else if (satermActive) {
            shouldBreak = 1;                                   /* SB11 */
            satermActive = 0;
            spSeenAfterSaterm = 0;
        }
        /* SB998: × Any — default no break */

        if (shouldBreak) {
            out->indices[out->count++] = i;
        }

        if (curr == UC_SB_ATerm || curr == UC_SB_STerm) {
            satermActive = 1;
            spSeenAfterSaterm = 0;
        } else if (satermActive) {
            if (curr == UC_SB_Sp) {
                spSeenAfterSaterm = 1;
            } else if (curr != UC_SB_Close) {
                satermActive = 0;
                spSeenAfterSaterm = 0;
            }
        }

        prev2 = prev;
        prev = curr;
    }
    return 0;
}

static int td_grapheme_boundaries(const TdDecoded* d, TdBoundaries* out)
{
    out->indices = (int32_t*) malloc(sizeof(int32_t) * (d->count + 1));
    out->count = 0;
    if (!out->indices) return -1;
    if (d->count == 0) return 0;

    out->indices[out->count++] = 0;

    /* GB12/GB13: an RI run starts at the first codepoint. If the first
     * codepoint is itself an RI we are already in an RI run of length 1. */
    int riRun = (d->count > 0 && td_gcb(d->codepoints[0]) == UC_GCB_Regional_Indicator) ? 1 : 0;
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
        /* WB7a — Hebrew_Letter × Single_Quote. Must fire BEFORE WB6's
         * AHLetter × (MidLetter|MidNumLet|Single_Quote) (AHLetter)
         * lookahead, since WB7a is unconditional and WB6 would otherwise
         * preempt it when no AHLetter follows. */
        else if (prevSig == UC_WB_Hebrew_Letter && curr == UC_WB_Single_Quote)         shouldBreak = 0;
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
        /* WB7a handled earlier (above the WB6 lookahead block) to avoid preemption. */
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
 * (6) geometry4d payload encoders for POINT4D and LINESTRING4D.
 * ───────────────────────────────────────────────────────────────────── */
static void td_point4d_geometry(double x, double y, double z, double m, uint8_t out[33])
{
    out[0] = 1;
    memcpy(out + 1,  &x, 8);
    memcpy(out + 9,  &y, 8);
    memcpy(out + 17, &z, 8);
    memcpy(out + 25, &m, 8);
}

static uint8_t* td_linestring4d_geometry(const double* verts, int k, size_t* out_len)
{
    size_t sz = 1 + 4 + (size_t) k * 32;
    uint8_t* buf = (uint8_t*) malloc(sz);
    if (!buf) { *out_len = 0; return NULL; }
    buf[0] = 2;
    uint32_t n = (uint32_t) k;
    memcpy(buf + 1, &n, 4);
    uint8_t* vp = buf + 5;
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
    if (!td_ucd_tables_ready()) return -4;
    if (utf8_len == 0) return -3;
    if (utf8_len > (size_t)INT32_MAX - 1) return -5;

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
    if (td_normalize_nfc(&d) != 0) { rc_final = -9; goto out_cleanup; }
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

    /* Codepoints: entity + classification + s3_position physicality + significance.
     * ENTITY records carry the centroid in their .centroid field so callers can
     * land centroid + Hilbert directly on substrate.entity at INSERT time
     * (eliminates the trigger-based reactive UPDATE). Same Merkle invariant. */
    for (int i = 0; i < d.count; i++) {
        const uint8_t* h = cp_h + (size_t) i * HASH_LEN;
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_ENTITY, .subkind = HARTONOMOUS_KIND_CODEPOINT,
            .hash_a = h,
            .centroid = { cp_c[i*4+0], cp_c[i*4+1], cp_c[i*4+2], cp_c[i*4+3] }
        }));
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_CLASSIFICATION, .subkind = HARTONOMOUS_KIND_CODEPOINT,
            .hash_a = h
        }));
        uint8_t pt[33];
        td_point4d_geometry(cp_c[i*4+0], cp_c[i*4+1], cp_c[i*4+2], cp_c[i*4+3], pt);
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_PHYSICALITY, .subkind = HARTONOMOUS_PHYS_S3_POSITION,
            .hash_a = h, .hash_b = h,
            .geometry = pt, .geometry_len = 33,
            .centroid = { cp_c[i*4+0], cp_c[i*4+1], cp_c[i*4+2], cp_c[i*4+3] }
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
            .hash_a = gh,
            .centroid = { gc_c[gi*4+0], gc_c[gi*4+1], gc_c[gi*4+2], gc_c[gi*4+3] }
        }));
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_CLASSIFICATION, .subkind = HARTONOMOUS_KIND_GRAPHEME_CLUSTER,
            .hash_a = gh
        }));
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_SIGNIFICANCE, .subkind = HARTONOMOUS_SIG_SOURCE_AUTHORITY,
            .hash_a = gh, .double_param = trust_mu
        }));
        if (cpCount > 0) {
            size_t ls_len;
            xfree(ls_buf);
            ls_buf = td_linestring4d_geometry(cp_c + firstCp * 4, cpCount, &ls_len);
            if (!ls_buf) { rc_final = -9; goto out_cleanup; }
            EMIT(((hartonomous_text_record_t){
                .kind = HARTONOMOUS_REC_PHYSICALITY, .subkind = HARTONOMOUS_PHYS_CONTOUR,
                .hash_a = gh, .hash_b = gh,
                .geometry = ls_buf, .geometry_len = ls_len,
                .centroid = { gc_c[gi*4+0], gc_c[gi*4+1], gc_c[gi*4+2], gc_c[gi*4+3] }
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
            .hash_a = wh,
            .centroid = { w_c[wi*4+0], w_c[wi*4+1], w_c[wi*4+2], w_c[wi*4+3] }
        }));
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_CLASSIFICATION, .subkind = HARTONOMOUS_KIND_WORD_FORM,
            .hash_a = wh
        }));
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_SIGNIFICANCE, .subkind = HARTONOMOUS_SIG_SOURCE_AUTHORITY,
            .hash_a = wh, .double_param = trust_mu
        }));
        if (gcCount > 0) {
            size_t ls_len;
            xfree(ls_buf);
            ls_buf = td_linestring4d_geometry(gc_c + firstGc * 4, gcCount, &ls_len);
            if (!ls_buf) { rc_final = -9; goto out_cleanup; }
            EMIT(((hartonomous_text_record_t){
                .kind = HARTONOMOUS_REC_PHYSICALITY, .subkind = HARTONOMOUS_PHYS_CONTOUR,
                .hash_a = wh, .hash_b = wh,
                .geometry = ls_buf, .geometry_len = ls_len,
                .centroid = { w_c[wi*4+0], w_c[wi*4+1], w_c[wi*4+2], w_c[wi*4+3] }
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

    /* Root-hash selection per top_kind.
     *
     * Per CLAUDE.md content-addressing invariant: same content bytes ⇒ same
     * BLAKE3 hash. Caller asks for an entity of a specific tier (codepoint /
     * grapheme_cluster / word_form / text_composition). The kernel returns
     * the hash of that tier's natural unit from the input, NOT a composition
     * root tagged with the wrong subkind. Pre-fix bug: kernel always returned
     * composition root, tagged as top_kind — so a decomposer passing "he"
     * vs "he " for top_kind=word_form got different hashes both classified
     * as word_form, fragmenting cross-source consensus on the same content.
     *
     * Sub-composition tiers (codepoint, grapheme_cluster, word_form): the
     * input is canonically the FIRST unit at that tier. Multi-unit inputs
     * still succeed (return first unit) so corpus decomposers passing
     * unintended trailing whitespace converge to the same hash.
     *
     * Composition tier (text_composition): emit composition-root entity
     * records, return composition-root hash (Merkle over word hashes). */
    if (top_kind == HARTONOMOUS_KIND_TEXT_COMPOSITION) {
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_ENTITY, .subkind = top_kind,
            .hash_a = comp_h,
            .centroid = { comp_c[0], comp_c[1], comp_c[2], comp_c[3] }
        }));
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_CLASSIFICATION, .subkind = top_kind,
            .hash_a = comp_h
        }));
        EMIT(((hartonomous_text_record_t){
            .kind = HARTONOMOUS_REC_SIGNIFICANCE, .subkind = HARTONOMOUS_SIG_SOURCE_AUTHORITY,
            .hash_a = comp_h, .double_param = trust_mu
        }));
        if (wN > 0) {
            size_t ls_len;
            xfree(ls_buf);
            ls_buf = td_linestring4d_geometry(w_c, wN, &ls_len);
            if (!ls_buf) { rc_final = -9; goto out_cleanup; }
            EMIT(((hartonomous_text_record_t){
                .kind = HARTONOMOUS_REC_PHYSICALITY, .subkind = HARTONOMOUS_PHYS_CONTOUR,
                .hash_a = comp_h, .hash_b = comp_h,
                .geometry = ls_buf, .geometry_len = ls_len,
                .centroid = { comp_c[0], comp_c[1], comp_c[2], comp_c[3] }
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
        if (out_root_centroid) {
            out_root_centroid[0] = comp_c[0];
            out_root_centroid[1] = comp_c[1];
            out_root_centroid[2] = comp_c[2];
            out_root_centroid[3] = comp_c[3];
        }
    } else if (top_kind == HARTONOMOUS_KIND_WORD_FORM) {
        /* Return the FIRST non-whitespace UAX-29 word_form. Skip leading
         * whitespace/CR/LF/Newline words so " he", "\nhe", "he", "he "
         * all return word_form("he"). Same input may pass through multiple
         * decomposers with different surrounding context; the substrate's
         * content-addressing invariant requires same word_form ⇒ same hash. */
        int picked = -1;
        for (int wi = 0; wi < wN; wi++) {
            int firstCpW = w.indices[wi];
            int endCpW   = (wi + 1 < wN) ? w.indices[wi + 1] : d.count;
            int all_space = 1;
            for (int k = firstCpW; k < endCpW; k++) {
                uint8_t wb = td_wb(d.codepoints[k]);
                if (wb != UC_WB_WSegSpace && wb != UC_WB_CR &&
                    wb != UC_WB_LF && wb != UC_WB_Newline) {
                    all_space = 0;
                    break;
                }
            }
            if (!all_space) { picked = wi; break; }
        }
        if (picked < 0) { rc_final = -10; goto out_cleanup; }
        memcpy(out_root_hash, w_h + (size_t) picked * HASH_LEN, HASH_LEN);
        if (out_root_centroid) {
            out_root_centroid[0] = w_c[picked * 4 + 0];
            out_root_centroid[1] = w_c[picked * 4 + 1];
            out_root_centroid[2] = w_c[picked * 4 + 2];
            out_root_centroid[3] = w_c[picked * 4 + 3];
        }
    } else if (top_kind == HARTONOMOUS_KIND_GRAPHEME_CLUSTER) {
        /* First non-whitespace grapheme cluster. */
        int picked = -1;
        for (int gi = 0; gi < gN; gi++) {
            int firstCp = g.indices[gi];
            int endCp   = (gi + 1 < gN) ? g.indices[gi + 1] : d.count;
            int all_space = 1;
            for (int k = firstCp; k < endCp; k++) {
                uint8_t wb = td_wb(d.codepoints[k]);
                if (wb != UC_WB_WSegSpace && wb != UC_WB_CR &&
                    wb != UC_WB_LF && wb != UC_WB_Newline) {
                    all_space = 0;
                    break;
                }
            }
            if (!all_space) { picked = gi; break; }
        }
        if (picked < 0) { rc_final = -11; goto out_cleanup; }
        memcpy(out_root_hash, gc_h + (size_t) picked * HASH_LEN, HASH_LEN);
        if (out_root_centroid) {
            out_root_centroid[0] = gc_c[picked * 4 + 0];
            out_root_centroid[1] = gc_c[picked * 4 + 1];
            out_root_centroid[2] = gc_c[picked * 4 + 2];
            out_root_centroid[3] = gc_c[picked * 4 + 3];
        }
    } else if (top_kind == HARTONOMOUS_KIND_CODEPOINT) {
        /* First non-whitespace codepoint. */
        int picked = -1;
        for (int i = 0; i < d.count; i++) {
            uint8_t wb = td_wb(d.codepoints[i]);
            if (wb != UC_WB_WSegSpace && wb != UC_WB_CR &&
                wb != UC_WB_LF && wb != UC_WB_Newline) {
                picked = i;
                break;
            }
        }
        if (picked < 0) { rc_final = -12; goto out_cleanup; }
        memcpy(out_root_hash, cp_h + (size_t) picked * HASH_LEN, HASH_LEN);
        if (out_root_centroid) {
            out_root_centroid[0] = cp_c[picked * 4 + 0];
            out_root_centroid[1] = cp_c[picked * 4 + 1];
            out_root_centroid[2] = cp_c[picked * 4 + 2];
            out_root_centroid[3] = cp_c[picked * 4 + 3];
        }
    } else {
        rc_final = -13;
        goto out_cleanup;
    }
    if (out_root_kind) *out_root_kind = top_kind;
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

/* ─────────────────────────────────────────────────────────────────────
 * (9) Boundary extraction — public lightweight API
 *
 * The substrate's SINGLE source of UAX-29 segmentation truth. C# callers
 * P/Invoke into these rather than reimplementing the state machine
 * (per CLAUDE.md compute-facade rule + Law #6 determinism).
 * ───────────────────────────────────────────────────────────────────── */

/* NOTE: Boundary functions do NOT NFC-normalize input. UAX-29 is defined
 * on raw codepoints, and the conformance test files expect original
 * codepoint indices (composed sequences not pre-merged). The substrate's
 * hashing path (hartonomous_text_decompose) DOES normalize so cross-source
 * consensus collapses different normalizations to the same hash. The two
 * use cases need different behaviors. */

int hartonomous_text_codepoint_count(
    const uint8_t* utf8, size_t utf8_len, int* out_count)
{
    if (!utf8 || !out_count) return -1;
    if (!hartonomous_ucd_loaded() || !td_ucd_tables_ready()) return -2;

    TdDecoded d = {0};
    int rc = 0;
    if (td_decode(utf8, utf8_len, &d) != 0) { rc = -9; goto cleanup; }
    *out_count = d.count;
cleanup:
    xfree(d.codepoints);
    return rc;
}

int hartonomous_text_grapheme_boundaries(
    const uint8_t* utf8, size_t utf8_len,
    int32_t* out_indices, int out_capacity, int* out_count)
{
    if (!utf8 || !out_count) return -1;
    if (!hartonomous_ucd_loaded() || !td_ucd_tables_ready()) return -2;

    TdDecoded d = {0};
    TdBoundaries g = {0};
    int rc = 0;
    if (td_decode(utf8, utf8_len, &d) != 0)        { rc = -9; goto cleanup; }
    if (td_grapheme_boundaries(&d, &g) != 0)       { rc = -9; goto cleanup; }
    *out_count = g.count;
    if (out_indices && out_capacity > 0) {
        int n = (g.count < out_capacity) ? g.count : out_capacity;
        for (int i = 0; i < n; i++) out_indices[i] = g.indices[i];
    }
cleanup:
    xfree(g.indices);
    xfree(d.codepoints);
    return rc;
}

int hartonomous_text_word_boundaries(
    const uint8_t* utf8, size_t utf8_len,
    int32_t* out_indices, int out_capacity, int* out_count)
{
    if (!utf8 || !out_count) return -1;
    if (!hartonomous_ucd_loaded() || !td_ucd_tables_ready()) return -2;

    TdDecoded d = {0};
    TdWords   w = {0};
    int rc = 0;
    if (td_decode(utf8, utf8_len, &d) != 0)    { rc = -9; goto cleanup; }
    if (td_word_boundaries(&d, &w) != 0)       { rc = -9; goto cleanup; }
    *out_count = w.count;
    if (out_indices && out_capacity > 0) {
        int n = (w.count < out_capacity) ? w.count : out_capacity;
        for (int i = 0; i < n; i++) out_indices[i] = w.indices[i];
    }
cleanup:
    xfree(w.indices);
    xfree(d.codepoints);
    return rc;
}

int hartonomous_text_sentence_boundaries(
    const uint8_t* utf8, size_t utf8_len,
    int32_t* out_indices, int out_capacity, int* out_count)
{
    if (!utf8 || !out_count) return -1;
    if (!hartonomous_ucd_loaded() || !td_ucd_tables_ready()) return -2;

    TdDecoded   d = {0};
    TdSentences s = {0};
    int rc = 0;
    if (td_decode(utf8, utf8_len, &d) != 0)        { rc = -9; goto cleanup; }
    if (td_sentence_boundaries(&d, &s) != 0)       { rc = -9; goto cleanup; }
    *out_count = s.count;
    if (out_indices && out_capacity > 0) {
        int n = (s.count < out_capacity) ? s.count : out_capacity;
        for (int i = 0; i < n; i++) out_indices[i] = s.indices[i];
    }
cleanup:
    xfree(s.indices);
    xfree(d.codepoints);
    return rc;
}
