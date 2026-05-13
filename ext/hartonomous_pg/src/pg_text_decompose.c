/*
 * pg_text_decompose.c — native UAX #29 + BLAKE3 + 4D centroid pipeline.
 *
 * Tier 1 of the substrate: heavy lifting in compiled C, batch parallelism
 * via OpenMP. Tier 2 (SQL) and Tier 3 (C#) call this function once per
 * text and the loop happens here.
 *
 * UAX #29 spec: https://www.unicode.org/reports/tr29/  (Unicode 17.0.0)
 * Property tables: substrate.codepoint_property (cached per backend);
 *                  pg_text_decompose_incb.h (generated from UCD
 *                  DerivedCoreProperties.txt for GB9c).
 *
 * Determinism: GraphemeBreakTest.txt + WordBreakTest.txt conformance is
 * the gate. test/text_decompose_test.sql exercises the canonical UCD
 * test files plus a sampled Wiktionary corpus and asserts byte-identical
 * boundaries vs the spec (Law #6).
 */

#include "pg_text_decompose.h"
#include "hartonomous_pg.h"
#include "hartonomous.h"
#include "generated/pg_unicode_version.h"
#include "generated/pg_ucd_segmentation.h"
#include "generated/pg_ucd_classification.h"
#include "generated/pg_ucd_pictographic.h"
#include "generated/pg_ucd_atoms_blob.h"

#include "executor/spi.h"
#include "miscadmin.h"
#include "utils/builtins.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/array.h"
#include "access/htup_details.h"
#include "catalog/pg_type.h"
#include "funcapi.h"

#include <string.h>
#include <stdlib.h>
#include <stdint.h>

static void pg_text_decompose_keep_future_symbols(void);

/* ═════════════════════════════════════════════════════════════════════
 * (1) UTF-8 decode
 * ═════════════════════════════════════════════════════════════════════ */
size_t utf8_decode_one(const uint8_t* p, size_t len, int32_t* out)
{
    uint8_t b0;
    int32_t cp;

    if (len == 0 || p == NULL || out == NULL) return 0;
    b0 = p[0];

    if (b0 < 0x80) { *out = (int32_t)b0; return 1; }
    if ((b0 & 0xE0) == 0xC0) {
        if (len < 2 || (p[1] & 0xC0) != 0x80) return 0;
        cp = ((int32_t)(b0 & 0x1F) << 6) | (int32_t)(p[1] & 0x3F);
        if (cp < 0x80) return 0;
        *out = cp; return 2;
    }
    if ((b0 & 0xF0) == 0xE0) {
        if (len < 3 || (p[1] & 0xC0) != 0x80 || (p[2] & 0xC0) != 0x80) return 0;
        cp = ((int32_t)(b0 & 0x0F) << 12)
           | ((int32_t)(p[1] & 0x3F) << 6)
           |  (int32_t)(p[2] & 0x3F);
        if (cp < 0x800) return 0;
        if (cp >= 0xD800 && cp <= 0xDFFF) return 0;
        *out = cp; return 3;
    }
    if ((b0 & 0xF8) == 0xF0) {
        if (len < 4 || (p[1] & 0xC0) != 0x80 || (p[2] & 0xC0) != 0x80 || (p[3] & 0xC0) != 0x80) return 0;
        cp = ((int32_t)(b0 & 0x07) << 18)
           | ((int32_t)(p[1] & 0x3F) << 12)
           | ((int32_t)(p[2] & 0x3F) << 6)
           |  (int32_t)(p[3] & 0x3F);
        if (cp < 0x10000 || cp > 0x10FFFF) return 0;
        *out = cp; return 4;
    }
    return 0;
}

/* ═════════════════════════════════════════════════════════════════════
 * (2) Codepoint property lookups — pure array loads from generated tables.
 *
 * No SPI, no DB round-trip, no per-backend cache load. The generated
 * tables (uc_gcb / uc_wb / uc_extended_pictographic / uc_incb) are baked
 * into the extension at build time from UCD 17.0.0; lookup is one array
 * dereference. Identical determinism guarantees as the SPI-fed cache,
 * with extension version pinning UCD version.
 * ═════════════════════════════════════════════════════════════════════ */

void pg_text_decompose_cache_load(void)  { pg_text_decompose_keep_future_symbols(); }
void pg_text_decompose_cache_clear(void) { /* no-op — embedded */ }

GraphemeBreak gcb_for(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) return GCB_Other;
    return (GraphemeBreak) uc_gcb[cp];
}

WordBreak wb_for(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) return WB_Other;
    return (WordBreak) uc_wb[cp];
}

int is_extended_pictographic(int32_t cp)
{
    return uc_extended_pictographic(cp);
}

static uint8_t incb_for(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) return UC_INCB_None;
    return uc_incb[cp];
}

/* ═════════════════════════════════════════════════════════════════════
 * (3) Decoded codepoints
 * ═════════════════════════════════════════════════════════════════════ */
typedef struct {
    int32_t* codepoints;
    int32_t* byte_offsets;
    int32_t* byte_widths;
    int32_t  count;
} DecodedCodepoints;

static DecodedCodepoints decode_utf8_buf(const uint8_t* utf8, size_t utf8_len)
{
    DecodedCodepoints d;
    int32_t cap = (int32_t)(utf8_len + 1);
    size_t pos;
    d.codepoints   = (int32_t*) palloc(sizeof(int32_t) * cap);
    d.byte_offsets = (int32_t*) palloc(sizeof(int32_t) * cap);
    d.byte_widths  = (int32_t*) palloc(sizeof(int32_t) * cap);
    d.count        = 0;

    pos = 0;
    while (pos < utf8_len) {
        int32_t cp;
        size_t consumed;
        consumed = utf8_decode_one(utf8 + pos, utf8_len - pos, &cp);
        if (consumed == 0) { pos++; continue; }
        d.codepoints[d.count]   = cp;
        d.byte_offsets[d.count] = (int32_t) pos;
        d.byte_widths[d.count]  = (int32_t) consumed;
        d.count++;
        pos += consumed;
    }
    return d;
}

/* ═════════════════════════════════════════════════════════════════════
 * (4) UAX #29 grapheme cluster boundaries — GB1..GB13 + GB9c + GB999.
 *
 * Returns an int array of grapheme START indices (offsets into the
 * codepoint stream). The first element is always 0; one boundary per
 * grapheme cluster start. Grapheme i spans codepoints
 * [boundaries[i], boundaries[i+1]).
 * ═════════════════════════════════════════════════════════════════════ */
typedef struct {
    int32_t* indices;   /* boundary indices (start of each grapheme) */
    int32_t  count;     /* number of boundaries (== grapheme count) */
} BoundaryArray;

static BoundaryArray grapheme_boundaries(const DecodedCodepoints* d)
{
    BoundaryArray b;
    int riRun;
    int chainPict;
    int chainZwjAfterPict;
    int incbConsonantSeen;
    int incbLinkerSeen;
    int32_t i;

    b.indices = (int32_t*) palloc(sizeof(int32_t) * (d->count + 1));
    b.count = 0;

    if (d->count == 0) return b;

    /* GB1: sot ÷ — first cluster always starts at 0. */
    b.indices[b.count++] = 0;

    /* State for cross-cluster rules: */
    riRun = 0;                        /* GB12/13 — trailing RI run length */
    chainPict = 0;                    /* GB11 — saw Extended_Pictographic */
    chainZwjAfterPict = 0;            /* GB11 — saw ZWJ after Pict (with optional Extends) */
    /* GB9c InCB state: tracks "Consonant (Linker|Extend)*" with at least one Linker */
    incbConsonantSeen = 0;
    incbLinkerSeen = 0;

    for (i = 1; i < d->count; i++) {
        int32_t prev_cp = d->codepoints[i - 1];
        int32_t curr_cp = d->codepoints[i];
        GraphemeBreak prev = gcb_for(prev_cp);
        GraphemeBreak curr = gcb_for(curr_cp);
        int currIsPict = is_extended_pictographic(curr_cp);
        uint8_t curr_incb = incb_for(curr_cp);

        int shouldBreak;

        /* GB3: CR × LF — keep together. */
        if (prev == GCB_CR && curr == GCB_LF) {
            shouldBreak = 0;
        }
        /* GB4: (Control | CR | LF) ÷ */
        else if (prev == GCB_Control || prev == GCB_CR || prev == GCB_LF) {
            shouldBreak = 1;
        }
        /* GB5: ÷ (Control | CR | LF) */
        else if (curr == GCB_Control || curr == GCB_CR || curr == GCB_LF) {
            shouldBreak = 1;
        }
        /* GB6: L × (L | V | LV | LVT) */
        else if (prev == GCB_L && (curr == GCB_L || curr == GCB_V ||
                                   curr == GCB_LV || curr == GCB_LVT)) {
            shouldBreak = 0;
        }
        /* GB7: (LV | V) × (V | T) */
        else if ((prev == GCB_LV || prev == GCB_V) &&
                 (curr == GCB_V || curr == GCB_T)) {
            shouldBreak = 0;
        }
        /* GB8: (LVT | T) × T */
        else if ((prev == GCB_LVT || prev == GCB_T) && curr == GCB_T) {
            shouldBreak = 0;
        }
        /* GB9: × (Extend | ZWJ) */
        else if (curr == GCB_Extend || curr == GCB_ZWJ) {
            shouldBreak = 0;
        }
        /* GB9a: × SpacingMark */
        else if (curr == GCB_SpacingMark) {
            shouldBreak = 0;
        }
        /* GB9b: Prepend × */
        else if (prev == GCB_Prepend) {
            shouldBreak = 0;
        }
        /* GB9c: \p{InCB=Consonant} (Linker | Extend)* Linker × \p{InCB=Consonant} */
        else if (incbConsonantSeen && incbLinkerSeen && curr_incb == UC_INCB_Consonant) {
            shouldBreak = 0;
        }
        /* GB11: Extended_Pictographic Extend* ZWJ × Extended_Pictographic */
        else if (chainZwjAfterPict && currIsPict) {
            shouldBreak = 0;
        }
        /* GB12 / GB13: RI × RI when leading RI run is odd. */
        else if (prev == GCB_RegionalIndicator && curr == GCB_RegionalIndicator) {
            shouldBreak = (riRun % 2) == 0;
        }
        /* GB999: otherwise break. */
        else {
            shouldBreak = 1;
        }

        if (shouldBreak) {
            b.indices[b.count++] = i;
            /* Reset RI run and emoji chain on break (GB12/13/11). */
            riRun = (curr == GCB_RegionalIndicator) ? 1 : 0;
            chainPict = currIsPict ? 1 : 0;
            chainZwjAfterPict = 0;
            /* Reset GB9c state on break — but if curr starts a Consonant, seed it. */
            incbConsonantSeen = (curr_incb == UC_INCB_Consonant) ? 1 : 0;
            incbLinkerSeen    = 0;
        } else {
            /* Update RI run within cluster. */
            if (curr == GCB_RegionalIndicator) {
                riRun++;
            } else {
                riRun = 0;
            }
            /* Update emoji chain within cluster (GB11). */
            if (currIsPict) {
                chainPict = 1;
                chainZwjAfterPict = 0;
            } else if (curr == GCB_Extend && chainPict) {
                /* Extend keeps Pict chain alive */
            } else if (curr == GCB_ZWJ && chainPict) {
                chainZwjAfterPict = 1;
            } else {
                chainPict = 0;
                chainZwjAfterPict = 0;
            }
            /* Update GB9c InCB state within cluster. */
            if (curr_incb == UC_INCB_Consonant) {
                incbConsonantSeen = 1;
                incbLinkerSeen    = 0;
            } else if (incbConsonantSeen && (curr_incb == UC_INCB_Linker || curr_incb == UC_INCB_Extend)) {
                if (curr_incb == UC_INCB_Linker) {
                    incbLinkerSeen = 1;
                }
            } else {
                /* Some other property — InCB state breaks. */
                incbConsonantSeen = 0;
                incbLinkerSeen    = 0;
            }
        }
    }

    return b;
}

/* ═════════════════════════════════════════════════════════════════════
 * (5) UAX #29 word boundaries — WB1..WB16 + WB999.
 *
 * Implements the Format/Extend/ZWJ ignore rule (WB4) by tracking the
 * "previous significant" codepoint (last non-Extend/Format/ZWJ) for
 * comparisons in WB5–WB13. WB3 (CR × LF) and WB3a/WB3b/WB3c/WB3d apply
 * to LITERAL adjacent codepoints — those use the actual prev cp.
 *
 * Returns: int array of word boundary START indices (codepoint-stream
 * indices). word_kinds[i] is the WordKind of word i.
 * ═════════════════════════════════════════════════════════════════════ */

typedef enum {
    WK_Other = 0,
    WK_AlphaNumeric,
    WK_Numeric,
    WK_Katakana,
    WK_RegionalIndicator,
    WK_ExtendNumLet,
    WK_Pictograph
} WordKind;

typedef struct {
    int32_t*  indices;
    WordKind* kinds;
    int32_t   count;
} WordArray;

static WordKind word_kind_for(WordBreak wb, int isPict)
{
    if (wb == WB_ALetter || wb == WB_HebrewLetter) return WK_AlphaNumeric;
    if (wb == WB_Numeric)                          return WK_Numeric;
    if (wb == WB_Katakana)                         return WK_Katakana;
    if (wb == WB_RegionalIndicator)                return WK_RegionalIndicator;
    if (wb == WB_ExtendNumLet)                     return WK_ExtendNumLet;
    if (isPict)                                    return WK_Pictograph;
    return WK_Other;
}

static WordArray word_boundaries(const DecodedCodepoints* d)
{
    WordArray w;
    WordBreak prev_literal;
    WordBreak prevSig;
    WordBreak prev2Sig;
    int prevSigPict;
    int riRun;
    int32_t i;

    w.indices = (int32_t*) palloc(sizeof(int32_t) * (d->count + 1));
    w.kinds   = (WordKind*) palloc(sizeof(WordKind) * (d->count + 1));
    w.count = 0;

    if (d->count == 0) return w;

    /* WB1: sot ÷ */
    w.indices[w.count] = 0;
    {
        WordBreak wb0 = wb_for(d->codepoints[0]);
        int picto0 = is_extended_pictographic(d->codepoints[0]);
        w.kinds[w.count] = word_kind_for(wb0, picto0);
    }
    w.count++;

    /* "Previous significant" tracking for WB4 ignore-rule:
     *   prevSig:    the WB class of the most recent NON-(Extend/Format/ZWJ) cp
     *   prev2Sig:   the WB class one before that — needed for WB6/7
     *   prevSigPict: was the most recent significant cp Extended_Pictographic?
     * Special cases:
     *   - WB3 (CR×LF) uses LITERAL prev/curr.
     *   - WB3a/WB3b/WB3c/WB3d apply to LITERAL adjacent codepoints.
     *   - The very first codepoint is always considered significant.
     */
    prev_literal = wb_for(d->codepoints[0]);
    prevSig = wb_for(d->codepoints[0]);
    prev2Sig = WB_Other;
    prevSigPict = is_extended_pictographic(d->codepoints[0]);
    riRun = (prevSig == WB_RegionalIndicator) ? 1 : 0;

    for (i = 1; i < d->count; i++) {
        int32_t curr_cp = d->codepoints[i];
        WordBreak prev_lit = prev_literal;
        WordBreak curr     = wb_for(curr_cp);
        int currPict       = is_extended_pictographic(curr_cp);

        int shouldBreak;

        /* WB3: CR × LF — literal adjacency. */
        if (prev_lit == WB_CR && curr == WB_LF) {
            shouldBreak = 0;
        }
        /* WB3a: (Newline | CR | LF) ÷ */
        else if (prev_lit == WB_Newline || prev_lit == WB_CR || prev_lit == WB_LF) {
            shouldBreak = 1;
        }
        /* WB3b: ÷ (Newline | CR | LF) */
        else if (curr == WB_Newline || curr == WB_CR || curr == WB_LF) {
            shouldBreak = 1;
        }
        /* WB3c: ZWJ × Extended_Pictographic — literal adjacency. */
        else if (prev_lit == WB_ZWJ && currPict) {
            shouldBreak = 0;
        }
        /* WB3d: WSegSpace × WSegSpace — literal adjacency. */
        else if (prev_lit == WB_WSegSpace && curr == WB_WSegSpace) {
            shouldBreak = 0;
        }
        /* WB4: × (Extend | Format | ZWJ) — ignore these in the stream. */
        else if (curr == WB_Extend || curr == WB_Format || curr == WB_ZWJ) {
            shouldBreak = 0;
        }
        /* WB5: AHLetter × AHLetter (using significant-prev). */
        else if ((prevSig == WB_ALetter || prevSig == WB_HebrewLetter) &&
                 (curr    == WB_ALetter || curr    == WB_HebrewLetter)) {
            shouldBreak = 0;
        }
        /* WB6: AHLetter × (MidLetter | MidNumLetQ) AHLetter — needs lookahead. */
        else if ((prevSig == WB_ALetter || prevSig == WB_HebrewLetter) &&
                 (curr == WB_MidLetter || curr == WB_MidNumLet ||
                  curr == WB_SingleQuote)) {
            /* Look ahead past Extend/Format/ZWJ for an AHLetter. */
            int32_t k = i + 1;
            int aheadIsLetter;
            while (k < d->count) {
                WordBreak wbk = wb_for(d->codepoints[k]);
                if (wbk == WB_Extend || wbk == WB_Format || wbk == WB_ZWJ) { k++; continue; }
                break;
            }
            aheadIsLetter = (k < d->count) &&
                (wb_for(d->codepoints[k]) == WB_ALetter ||
                 wb_for(d->codepoints[k]) == WB_HebrewLetter);
            shouldBreak = aheadIsLetter ? 0 : 1;
        }
        /* WB7: AHLetter (MidLetter | MidNumLetQ) × AHLetter — uses prev2Sig. */
        else if ((prev2Sig == WB_ALetter || prev2Sig == WB_HebrewLetter) &&
                 (prevSig == WB_MidLetter || prevSig == WB_MidNumLet ||
                  prevSig == WB_SingleQuote) &&
                 (curr == WB_ALetter || curr == WB_HebrewLetter)) {
            shouldBreak = 0;
        }
        /* WB7a: Hebrew_Letter × Single_Quote */
        else if (prevSig == WB_HebrewLetter && curr == WB_SingleQuote) {
            shouldBreak = 0;
        }
        /* WB7b: Hebrew_Letter × Double_Quote Hebrew_Letter */
        else if (prevSig == WB_HebrewLetter && curr == WB_DoubleQuote) {
            int32_t k = i + 1;
            int aheadHeb;
            while (k < d->count) {
                WordBreak wbk = wb_for(d->codepoints[k]);
                if (wbk == WB_Extend || wbk == WB_Format || wbk == WB_ZWJ) { k++; continue; }
                break;
            }
            aheadHeb = (k < d->count) && wb_for(d->codepoints[k]) == WB_HebrewLetter;
            shouldBreak = aheadHeb ? 0 : 1;
        }
        /* WB7c: Hebrew_Letter Double_Quote × Hebrew_Letter */
        else if (prev2Sig == WB_HebrewLetter && prevSig == WB_DoubleQuote && curr == WB_HebrewLetter) {
            shouldBreak = 0;
        }
        /* WB8: Numeric × Numeric */
        else if (prevSig == WB_Numeric && curr == WB_Numeric) {
            shouldBreak = 0;
        }
        /* WB9: AHLetter × Numeric */
        else if ((prevSig == WB_ALetter || prevSig == WB_HebrewLetter) && curr == WB_Numeric) {
            shouldBreak = 0;
        }
        /* WB10: Numeric × AHLetter */
        else if (prevSig == WB_Numeric && (curr == WB_ALetter || curr == WB_HebrewLetter)) {
            shouldBreak = 0;
        }
        /* WB11: Numeric (MidNum | MidNumLetQ) × Numeric */
        else if (prev2Sig == WB_Numeric &&
                 (prevSig == WB_MidNum || prevSig == WB_MidNumLet ||
                  prevSig == WB_SingleQuote) &&
                 curr == WB_Numeric) {
            shouldBreak = 0;
        }
        /* WB12: Numeric × (MidNum | MidNumLetQ) Numeric */
        else if (prevSig == WB_Numeric &&
                 (curr == WB_MidNum || curr == WB_MidNumLet || curr == WB_SingleQuote)) {
            int32_t k = i + 1;
            int aheadNum;
            while (k < d->count) {
                WordBreak wbk = wb_for(d->codepoints[k]);
                if (wbk == WB_Extend || wbk == WB_Format || wbk == WB_ZWJ) { k++; continue; }
                break;
            }
            aheadNum = (k < d->count) && wb_for(d->codepoints[k]) == WB_Numeric;
            shouldBreak = aheadNum ? 0 : 1;
        }
        /* WB13: Katakana × Katakana */
        else if (prevSig == WB_Katakana && curr == WB_Katakana) {
            shouldBreak = 0;
        }
        /* WB13a: (AHLetter | Numeric | Katakana | ExtendNumLet) × ExtendNumLet */
        else if ((prevSig == WB_ALetter || prevSig == WB_HebrewLetter ||
                  prevSig == WB_Numeric || prevSig == WB_Katakana ||
                  prevSig == WB_ExtendNumLet) && curr == WB_ExtendNumLet) {
            shouldBreak = 0;
        }
        /* WB13b: ExtendNumLet × (AHLetter | Numeric | Katakana) */
        else if (prevSig == WB_ExtendNumLet &&
                 (curr == WB_ALetter || curr == WB_HebrewLetter ||
                  curr == WB_Numeric || curr == WB_Katakana)) {
            shouldBreak = 0;
        }
        /* WB15 / WB16: RI × RI (odd-count). */
        else if (prevSig == WB_RegionalIndicator && curr == WB_RegionalIndicator) {
            shouldBreak = (riRun % 2) == 0;
        }
        /* WB999: otherwise break. */
        else {
            shouldBreak = 1;
        }

        if (shouldBreak) {
            w.indices[w.count] = i;
            w.kinds[w.count] = word_kind_for(curr, currPict);
            w.count++;
            if (curr == WB_RegionalIndicator) {
                riRun = 1;
            } else {
                riRun = 0;
            }
        }

        /* Advance "literal prev". */
        prev_literal = curr;

        /* Advance significant-prev tracking — skip Extend/Format/ZWJ. */
        if (curr != WB_Extend && curr != WB_Format && curr != WB_ZWJ) {
            prev2Sig = prevSig;
            prevSig  = curr;
            prevSigPict = currPict;
            if (!shouldBreak && curr == WB_RegionalIndicator) {
                riRun++;
            }
        }
    }

    (void) prevSigPict;
    return w;
}

/* ═════════════════════════════════════════════════════════════════════
 * (6) BLAKE3 chain hashing + 4D centroids
 * ═════════════════════════════════════════════════════════════════════ */
#define HASH_LEN 32

/* Per-codepoint hash: lazy-mmap'd block file, O(1) memcpy. The native
 * BLAKE3 path is no longer hit — huc_cp_hash_at(cp) returns the precomputed
 * answer from the relevant per-block file (loaded once per backend). */
static void hash_codepoints(const DecodedCodepoints* d, uint8_t* out_hashes)
{
    int32_t n = d->count;
    for (int32_t i = 0; i < n; i++) {
        int32_t cp = d->codepoints[i];
        const uint8_t* h = (cp >= 0 && cp < UNICODE_CODEPOINT_MAX)
                         ? huc_cp_hash_at(cp) : NULL;
        if (h) memcpy(out_hashes + (size_t) i * HASH_LEN, h, HASH_LEN);
        else   memset(out_hashes + (size_t) i * HASH_LEN, 0, HASH_LEN);
    }
}

static void merkle_chain(const uint8_t* child_hashes_concat, size_t child_count, uint8_t* out_hash32)
{
    int rc = hartonomous_blake3_merkle(child_hashes_concat, child_count, out_hash32);
    if (rc != 0) elog(ERROR, "pg_text_decompose: hartonomous_blake3_merkle returned %d", rc);
}

/* Per-codepoint S^3 centroid: lazy-mmap'd block file, O(1) load. */
static void compute_codepoint_centroids(const DecodedCodepoints* d, double* out)
{
    int32_t n = d->count;
    for (int32_t i = 0; i < n; i++) {
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

static void mean_centroid(const double* in, int32_t k, double* out)
{
    double sx;
    double sy;
    double sz;
    double sm;
    double inv;
    int32_t i;

    if (k <= 0) { out[0]=out[1]=out[2]=out[3]=0.0; return; }
    sx = 0;
    sy = 0;
    sz = 0;
    sm = 0;
    for (i = 0; i < k; i++) {
        sx += in[i*4+0]; sy += in[i*4+1]; sz += in[i*4+2]; sm += in[i*4+3];
    }
    inv = 1.0 / (double)k;
    out[0] = sx*inv; out[1] = sy*inv; out[2] = sz*inv; out[3] = sm*inv;
}

/* ═════════════════════════════════════════════════════════════════════
 * (7) Reference-table id resolution (cached per backend)
 * ═════════════════════════════════════════════════════════════════════ */

typedef struct {
    int provenance_id;
    int entity_type_codepoint;
    int entity_type_grapheme_cluster;
    int entity_type_word_form;
    int entity_type_text_composition;  /* and root */
    int physicality_type_s3_position;
    int physicality_type_contour;
    int significance_context_source_authority;
    int loaded;
} RefIds;

static int spi_lookup_id(const char* sql, const char* code)
{
    Oid argtype[1] = { TEXTOID };
    Datum args[1] = { CStringGetTextDatum(code) };
    bool isnull;
    Datum d;
    int rc = SPI_execute_with_args(sql, 1, argtype, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed != 1) {
        return 0;
    }
    d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
    return isnull ? 0 : DatumGetInt32(d);
}

static void resolve_ref_ids(RefIds* r, const char* provenance_code)
{
    r->provenance_id = spi_lookup_id(
        "SELECT id FROM substrate.provenance WHERE code = $1", provenance_code);
    r->entity_type_codepoint = spi_lookup_id(
        "SELECT id FROM substrate.entity_type WHERE code = $1", "codepoint");
    r->entity_type_grapheme_cluster = spi_lookup_id(
        "SELECT id FROM substrate.entity_type WHERE code = $1", "grapheme_cluster");
    r->entity_type_word_form = spi_lookup_id(
        "SELECT id FROM substrate.entity_type WHERE code = $1", "word_form");
    r->entity_type_text_composition = spi_lookup_id(
        "SELECT id FROM substrate.entity_type WHERE code = $1", "text_composition");
    r->physicality_type_s3_position = spi_lookup_id(
        "SELECT id FROM substrate.physicality_type WHERE code = $1", "s3_position");
    r->physicality_type_contour = spi_lookup_id(
        "SELECT id FROM substrate.physicality_type WHERE code = $1", "contour");
    r->significance_context_source_authority = spi_lookup_id(
        "SELECT id FROM substrate.significance_context WHERE code = $1", "source_authority");
    r->loaded = 1;
}

/* ═════════════════════════════════════════════════════════════════════
 * (8) Staging-row buffers
 *
 * Per-call accumulators populated as the decomposer walks codepoints,
 * grapheme clusters, words, and the root composition. A single bulk
 * INSERT per staging table flushes them at end of call.
 * ═════════════════════════════════════════════════════════════════════ */

typedef struct {
    bytea**  hashes;
    int      count;
    int      cap;
} HashList;

typedef struct {
    bytea**  entity_hashes;
    int*     entity_type_ids;
    int      count;
    int      cap;
} ClassList;

typedef struct {
    int*     phys_type_ids;
    bytea**  entity_hashes;
    bytea**  content_hashes;
    bytea**  geometries;
    int      count;
    int      cap;
} PhysList;

typedef struct {
    bytea**  parent_hashes;
    int*     ordinals;
    bytea**  child_hashes;
    int*     rle_counts;
    int      count;
    int      cap;
} SeqList;

typedef struct {
    int*     context_type_ids;
    bytea**  entity_hashes;
    double*  mus;
    int      count;
    int      cap;
} SigList;

#define LIST_INIT(L, INIT_CAP) do { \
    (L).cap = (INIT_CAP); (L).count = 0; \
} while (0)

static bytea* hash_to_bytea(const uint8_t* h32)
{
    bytea* b = (bytea*) palloc(VARHDRSZ + HASH_LEN);
    SET_VARSIZE(b, VARHDRSZ + HASH_LEN);
    memcpy(VARDATA(b), h32, HASH_LEN);
    return b;
}

static bytea* point4d_to_geometry(double x, double y, double z, double m)
{
    bytea* b = (bytea*) palloc(VARHDRSZ + 33);
    uint8_t* p;
    SET_VARSIZE(b, VARHDRSZ + 33);
    p = (uint8_t*) VARDATA(b);
    p[0] = 1;
    memcpy(p + 1,  &x, 8);
    memcpy(p + 9,  &y, 8);
    memcpy(p + 17, &z, 8);
    memcpy(p + 25, &m, 8);
    return b;
}

static void pg_text_decompose_keep_future_symbols(void)
{
    if (false)
    {
        const uint8_t empty[1] = { 0 };
        DecodedCodepoints d;
        BoundaryArray g;
        WordArray w;
        uint8_t hashbuf[HASH_LEN];
        double cents[4];
        bytea* point_geometry;

        d = decode_utf8_buf(empty, 0);
        g = grapheme_boundaries(&d);
        w = word_boundaries(&d);
        hash_codepoints(&d, hashbuf);
        merkle_chain(hashbuf, 0, hashbuf);
        compute_codepoint_centroids(&d, cents);
        mean_centroid(cents, 0, cents);
        point_geometry = point4d_to_geometry(0.0, 0.0, 0.0, 0.0);
        (void) g;
        (void) w;
        (void) point_geometry;
    }
}

static bytea* linestring4d_to_geometry(const double* verts /* k * 4 */, int k)
{
    size_t sz = 1 + 4 + (size_t)k * 32;
    bytea* b = (bytea*) palloc(VARHDRSZ + sz);
    uint8_t* p;
    uint32_t n;
    uint8_t* vp;
    int i;

    SET_VARSIZE(b, VARHDRSZ + sz);
    p = (uint8_t*) VARDATA(b);
    p[0] = 2;
    n = (uint32_t) k;
    memcpy(p + 1, &n, 4);
    vp = p + 5;
    for (i = 0; i < k; i++) {
        memcpy(vp + 0,  &verts[i*4+0], 8);
        memcpy(vp + 8,  &verts[i*4+1], 8);
        memcpy(vp + 16, &verts[i*4+2], 8);
        memcpy(vp + 24, &verts[i*4+3], 8);
        vp += 32;
    }
    return b;
}

/* ═════════════════════════════════════════════════════════════════════
 * (8b) Public native constructor — substrate.ls4d_from_centroids
 * ═════════════════════════════════════════════════════════════════════ */
PG_FUNCTION_INFO_V1(pg_ls4d_from_centroids_geometry);

Datum pg_ls4d_from_centroids_geometry(PG_FUNCTION_ARGS)
{
    ArrayType* arr = PG_GETARG_ARRAYTYPE_P(0);
    int n = ArrayGetNItems(ARR_NDIM(arr), ARR_DIMS(arr));
    Oid elem_type;
    int16 typlen;
    bool  typbyval;
    char  typalign;
    Datum* elems;
    bool*  nulls;
    int    nelems;
    double* verts;
    bytea* geometry;
    int i;

    if (n < 1) {
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                        errmsg("ls4d_from_centroids: at least 1 vertex required (got %d)", n)));
    }

    /* point4d on-disk layout: 4 × float8, alignment double, plain storage,
     * 32 bytes per element. The element OID is point4d's typoid. We use
     * deconstruct_array with -1 typlen and pass-by-reference because point4d
     * is a fixed-size composite stored as a varlena-like blob with alignment
     * 'd'. Per pg_point4d.c, INTERNALLENGTH = 32 and ALIGNMENT = double. */
    elem_type = ARR_ELEMTYPE(arr);
    get_typlenbyvalalign(elem_type, &typlen, &typbyval, &typalign);

    deconstruct_array(arr, elem_type, typlen, typbyval, typalign, &elems, &nulls, &nelems);

    verts = (double*) palloc(sizeof(double) * 4 * nelems);
    for (i = 0; i < nelems; i++) {
        const double* p;
        if (nulls[i]) {
            ereport(ERROR, (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                            errmsg("ls4d_from_centroids: NULL element at index %d", i)));
        }
        /* point4d is INTERNALLENGTH=32, no varlena header — Datum points
         * directly at the 4 × double payload. */
        p = (const double*) DatumGetPointer(elems[i]);
        verts[i*4+0] = p[0];
        verts[i*4+1] = p[1];
        verts[i*4+2] = p[2];
        verts[i*4+3] = p[3];
    }

    geometry = linestring4d_to_geometry(verts, nelems);
    PG_RETURN_BYTEA_P(geometry);
}

/* ═════════════════════════════════════════════════════════════════════
 * (9) Bulk INSERT helpers — flush each accumulator DIRECTLY into the
 *     substrate core tables. No staging detour. Each insert uses
 *     ON CONFLICT DO NOTHING so re-emission of the same content
 *     collapses to a single substrate row (Law #6: same content =
 *     same hash = same row).
 *
 *     Partition routing for substrate.{edge, edge_member, physicality,
 *     entity_significance, edge_significance} is handled automatically
 *     by PostgreSQL — the LIST partition declarations make INSERT into
 *     the parent table route to the correct partition.
 * ═════════════════════════════════════════════════════════════════════ */

static ArrayType* build_bytea_array(bytea** items, int n)
{
    Datum* datums = (Datum*) palloc(sizeof(Datum) * n);
    int dims[1];
    int lbs[1];
    int i;
    for (i = 0; i < n; i++) datums[i] = PointerGetDatum(items[i]);
    dims[0] = n;
    lbs[0] = 1;
    return construct_md_array(datums, NULL, 1, dims, lbs, BYTEAOID, -1, false, TYPALIGN_INT);
}

static ArrayType* build_int4_array(int* items, int n)
{
    Datum* datums = (Datum*) palloc(sizeof(Datum) * n);
    int dims[1];
    int lbs[1];
    int i;
    for (i = 0; i < n; i++) datums[i] = Int32GetDatum(items[i]);
    dims[0] = n;
    lbs[0] = 1;
    return construct_md_array(datums, NULL, 1, dims, lbs, INT4OID, sizeof(int32), true, TYPALIGN_INT);
}

static ArrayType* build_float8_array(double* items, int n)
{
    Datum* datums = (Datum*) palloc(sizeof(Datum) * n);
    int dims[1];
    int lbs[1];
    int i;
    for (i = 0; i < n; i++) datums[i] = Float8GetDatum(items[i]);
    dims[0] = n;
    lbs[0] = 1;
    return construct_md_array(datums, NULL, 1, dims, lbs, FLOAT8OID, sizeof(float8), FLOAT8PASSBYVAL, TYPALIGN_DOUBLE);
}

static void flush_entities(HashList* L)
{
    Oid types[1];
    Datum vals[1];
    int rc;
    if (L->count == 0) return;
    types[0] = BYTEAARRAYOID;
    vals[0] = PointerGetDatum(build_bytea_array(L->hashes, L->count));
    rc = SPI_execute_with_args(
        "INSERT INTO substrate.entity (hash) "
        "SELECT DISTINCT h FROM unnest($1::bytea[]) AS h "
        "ON CONFLICT (hash) DO NOTHING",
        1, types, vals, NULL, false, 0);
    if (rc != SPI_OK_INSERT) elog(ERROR, "flush_entities: SPI_execute (%d)", rc);
}

static void flush_classifications(ClassList* L, int provenance_id)
{
    Oid types[3];
    Datum vals[3];
    int rc;
    if (L->count == 0) return;
    types[0] = BYTEAARRAYOID;
    types[1] = INT4ARRAYOID;
    types[2] = INT4OID;
    vals[0] = PointerGetDatum(build_bytea_array(L->entity_hashes, L->count));
    vals[1] = PointerGetDatum(build_int4_array(L->entity_type_ids, L->count));
    vals[2] = Int32GetDatum(provenance_id);
    rc = SPI_execute_with_args(
        "INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id) "
        "SELECT DISTINCT h, t, $3 FROM unnest($1::bytea[], $2::int[]) AS u(h, t) "
        "ON CONFLICT (entity_hash, entity_type_id, provenance_id) DO NOTHING",
        3, types, vals, NULL, false, 0);
    if (rc != SPI_OK_INSERT) elog(ERROR, "flush_classifications: SPI_execute (%d)", rc);
}

static void flush_physicalities(PhysList* L, SeqList* S)
{
    Oid types[8];
    Datum vals[8];
    int rc;
    if (L->count == 0) return;
    types[0] = INT4ARRAYOID;
    types[1] = BYTEAARRAYOID;
    types[2] = BYTEAARRAYOID;
    types[3] = BYTEAARRAYOID;
    types[4] = BYTEAARRAYOID;
    types[5] = INT4ARRAYOID;
    types[6] = BYTEAARRAYOID;
    types[7] = INT4ARRAYOID;
    vals[0] = PointerGetDatum(build_int4_array(L->phys_type_ids, L->count));
    vals[1] = PointerGetDatum(build_bytea_array(L->entity_hashes, L->count));
    vals[2] = PointerGetDatum(build_bytea_array(L->content_hashes, L->count));
    vals[3] = PointerGetDatum(build_bytea_array(L->geometries, L->count));
    vals[4] = PointerGetDatum(build_bytea_array(S->parent_hashes, S->count));
    vals[5] = PointerGetDatum(build_int4_array(S->ordinals, S->count));
    vals[6] = PointerGetDatum(build_bytea_array(S->child_hashes, S->count));
    vals[7] = PointerGetDatum(build_int4_array(S->rle_counts, S->count));
    rc = SPI_execute_with_args(
        "WITH phys AS ("
        "    SELECT DISTINCT ON (pt, eh, ch) pt, eh, ch, geometry_payload "
        "      FROM unnest($1::int[], $2::bytea[], $3::bytea[], $4::bytea[]) AS u(pt, eh, ch, geometry_payload) "
        "     ORDER BY pt, eh, ch, geometry_payload"
        "), child_rows AS ("
        "    SELECT parent_hash, ordinal, child_hash, rle_count "
        "      FROM unnest($5::bytea[], $6::int[], $7::bytea[], $8::int[]) AS u(parent_hash, ordinal, child_hash, rle_count)"
        "), child_meta AS ("
        "    SELECT parent_hash, "
        "           array_agg(child_hash ORDER BY ordinal)::substrate.hash_value[] AS child_hashes, "
        "           array_agg(ordinal ORDER BY ordinal)::int[] AS ordinal_starts, "
        "           array_agg(rle_count ORDER BY ordinal)::int[] AS rle_counts "
        "      FROM child_rows "
        "     GROUP BY parent_hash"
        ") "
        "INSERT INTO substrate.physicality ("
        "    physicality_type_id, entity_hash, content_hash, geom, "
        "    child_hashes, ordinal_starts, rle_counts) "
        "SELECT phys.pt, phys.eh, phys.ch, bytea_to_geometry4d(phys.geometry_payload), "
        "       child_meta.child_hashes, child_meta.ordinal_starts, child_meta.rle_counts "
        "  FROM phys "
        "  LEFT JOIN child_meta ON child_meta.parent_hash = phys.eh "
        "ON CONFLICT (physicality_type_id, entity_hash, content_hash) DO NOTHING",
        8, types, vals, NULL, false, 0);
    if (rc != SPI_OK_INSERT) elog(ERROR, "flush_physicalities: SPI_execute (%d)", rc);
}

static bool hash_bytea_equals(bytea* left, const uint8_t* right)
{
    return left != NULL &&
           right != NULL &&
           VARSIZE_ANY_EXHDR(left) == HASH_LEN &&
           memcmp(VARDATA_ANY(left), right, HASH_LEN) == 0;
}

static bool sequence_tail_extends_run(SeqList* L, const hartonomous_text_record_t* rec)
{
    int tail;
    if (L->count == 0 || rec->hash_a == NULL || rec->hash_b == NULL)
    {
        return false;
    }

    tail = L->count - 1;
    return L->ordinals[tail] + L->rle_counts[tail] == rec->int_param &&
           hash_bytea_equals(L->parent_hashes[tail], rec->hash_a) &&
           hash_bytea_equals(L->child_hashes[tail], rec->hash_b);
}

static void flush_significance(SigList* L)
{
    Oid types[3];
    Datum vals[3];
    int rc;
    if (L->count == 0) return;
    types[0] = INT4ARRAYOID;
    types[1] = BYTEAARRAYOID;
    types[2] = FLOAT8ARRAYOID;
    vals[0] = PointerGetDatum(build_int4_array(L->context_type_ids, L->count));
    vals[1] = PointerGetDatum(build_bytea_array(L->entity_hashes, L->count));
    vals[2] = PointerGetDatum(build_float8_array(L->mus, L->count));
    /* Native text_decompose ships ingestion-time priors. attestation_type
     * 'provenance_authority_corroboration' is resolved once via subquery —
     * it's the canonical kind-of-evidence for source-authority priming. */
    rc = SPI_execute_with_args(
        "INSERT INTO substrate.entity_significance (context_type_id, entity_hash, attestation_type_id, mu) "
        "SELECT DISTINCT ON (c, h, a) c, h, a, m "
        "  FROM unnest($1::int[], $2::bytea[], $3::float8[]) AS u(c, h, m), "
        "       (SELECT id AS a FROM substrate.attestation_type WHERE code = 'provenance_authority_corroboration') att "
        "ON CONFLICT (context_type_id, entity_hash, attestation_type_id) DO NOTHING",
        3, types, vals, NULL, false, 0);
    if (rc != SPI_OK_INSERT) elog(ERROR, "flush_significance: SPI_execute (%d)", rc);
}

/* When p_model_source_id is supplied, the root composition entity gets
 * linked to the model source. Single-row insert; no accumulator needed. */
static void flush_model_source(bytea* root_hash, int model_source_id)
{
    Oid types[2] = { BYTEAOID, INT4OID };
    Datum vals[2] = {
        PointerGetDatum(root_hash),
        Int32GetDatum(model_source_id)
    };
    int rc = SPI_execute_with_args(
        "INSERT INTO substrate.entity_model_source (entity_hash, model_source_id) "
        "VALUES ($1, $2) "
        "ON CONFLICT (entity_hash, model_source_id) DO NOTHING",
        2, types, vals, NULL, false, 0);
    if (rc != SPI_OK_INSERT) elog(ERROR, "flush_model_source: SPI_execute (%d)", rc);
}

static void ensure_hash_capacity(HashList* L)
{
    if (L->count < L->cap) return;
    L->cap = L->cap > 0 ? L->cap * 2 : 64;
    L->hashes = (bytea**) repalloc(L->hashes, sizeof(bytea*) * L->cap);
}

static void ensure_class_capacity(ClassList* L)
{
    if (L->count < L->cap) return;
    L->cap = L->cap > 0 ? L->cap * 2 : 64;
    L->entity_hashes = (bytea**) repalloc(L->entity_hashes, sizeof(bytea*) * L->cap);
    L->entity_type_ids = (int*) repalloc(L->entity_type_ids, sizeof(int) * L->cap);
}

static void ensure_phys_capacity(PhysList* L)
{
    if (L->count < L->cap) return;
    L->cap = L->cap > 0 ? L->cap * 2 : 64;
    L->phys_type_ids = (int*) repalloc(L->phys_type_ids, sizeof(int) * L->cap);
    L->entity_hashes = (bytea**) repalloc(L->entity_hashes, sizeof(bytea*) * L->cap);
    L->content_hashes = (bytea**) repalloc(L->content_hashes, sizeof(bytea*) * L->cap);
    L->geometries = (bytea**) repalloc(L->geometries, sizeof(bytea*) * L->cap);
}

static void ensure_seq_capacity(SeqList* L)
{
    if (L->count < L->cap) return;
    L->cap = L->cap > 0 ? L->cap * 2 : 64;
    L->parent_hashes = (bytea**) repalloc(L->parent_hashes, sizeof(bytea*) * L->cap);
    L->ordinals = (int*) repalloc(L->ordinals, sizeof(int) * L->cap);
    L->child_hashes = (bytea**) repalloc(L->child_hashes, sizeof(bytea*) * L->cap);
    L->rle_counts = (int*) repalloc(L->rle_counts, sizeof(int) * L->cap);
}

static void ensure_sig_capacity(SigList* L)
{
    if (L->count < L->cap) return;
    L->cap = L->cap > 0 ? L->cap * 2 : 64;
    L->context_type_ids = (int*) repalloc(L->context_type_ids, sizeof(int) * L->cap);
    L->entity_hashes = (bytea**) repalloc(L->entity_hashes, sizeof(bytea*) * L->cap);
    L->mus = (double*) repalloc(L->mus, sizeof(double) * L->cap);
}

static int native_entity_type_id_for(const RefIds* ids, int native_kind)
{
    switch (native_kind) {
        case HARTONOMOUS_KIND_CODEPOINT:         return ids->entity_type_codepoint;
        case HARTONOMOUS_KIND_GRAPHEME_CLUSTER:  return ids->entity_type_grapheme_cluster;
        case HARTONOMOUS_KIND_WORD_FORM:         return ids->entity_type_word_form;
        case HARTONOMOUS_KIND_TEXT_COMPOSITION:  return ids->entity_type_text_composition;
        default:                                 return ids->entity_type_text_composition;
    }
}

static int native_physicality_type_id_for(const RefIds* ids, int native_kind)
{
    switch (native_kind) {
        case HARTONOMOUS_PHYS_S3_POSITION: return ids->physicality_type_s3_position;
        case HARTONOMOUS_PHYS_CONTOUR:     return ids->physicality_type_contour;
        default:                           return ids->physicality_type_contour;
    }
}

static int native_context_type_id_for(const RefIds* ids, int native_kind)
{
    switch (native_kind) {
        case HARTONOMOUS_SIG_SOURCE_AUTHORITY: return ids->significance_context_source_authority;
        default:                               return ids->significance_context_source_authority;
    }
}

static int native_top_kind_for(const char* top_entity_type)
{
    if (strcmp(top_entity_type, "codepoint") == 0) return HARTONOMOUS_KIND_CODEPOINT;
    if (strcmp(top_entity_type, "grapheme_cluster") == 0) return HARTONOMOUS_KIND_GRAPHEME_CLUSTER;
    if (strcmp(top_entity_type, "word_form") == 0) return HARTONOMOUS_KIND_WORD_FORM;
    return HARTONOMOUS_KIND_TEXT_COMPOSITION;
}

static int top_entity_type_id_for(const RefIds* ids, const char* top_entity_type)
{
    if (strcmp(top_entity_type, "text_composition") == 0) return ids->entity_type_text_composition;
    if (strcmp(top_entity_type, "word_form") == 0)        return ids->entity_type_word_form;
    if (strcmp(top_entity_type, "grapheme_cluster") == 0) return ids->entity_type_grapheme_cluster;
    if (strcmp(top_entity_type, "codepoint") == 0)        return ids->entity_type_codepoint;
    if (strcmp(top_entity_type, "lemma") == 0)            return spi_lookup_id("SELECT id FROM substrate.entity_type WHERE code = $1", "lemma");
    return ids->entity_type_text_composition;
}

typedef struct {
    const RefIds* ids;
    HashList ent;
    ClassList cls;
    PhysList phys;
    SeqList seq;
    SigList sig;
} PgTextEmitContext;

static void init_emit_context(PgTextEmitContext* ctx, const RefIds* ids)
{
    memset(ctx, 0, sizeof(*ctx));
    ctx->ids = ids;

    LIST_INIT(ctx->ent, 64);
    ctx->ent.hashes = (bytea**) palloc(sizeof(bytea*) * ctx->ent.cap);

    LIST_INIT(ctx->cls, 64);
    ctx->cls.entity_hashes = (bytea**) palloc(sizeof(bytea*) * ctx->cls.cap);
    ctx->cls.entity_type_ids = (int*) palloc(sizeof(int) * ctx->cls.cap);

    LIST_INIT(ctx->phys, 64);
    ctx->phys.phys_type_ids = (int*) palloc(sizeof(int) * ctx->phys.cap);
    ctx->phys.entity_hashes = (bytea**) palloc(sizeof(bytea*) * ctx->phys.cap);
    ctx->phys.content_hashes = (bytea**) palloc(sizeof(bytea*) * ctx->phys.cap);
    ctx->phys.geometries = (bytea**) palloc(sizeof(bytea*) * ctx->phys.cap);

    LIST_INIT(ctx->seq, 64);
    ctx->seq.parent_hashes = (bytea**) palloc(sizeof(bytea*) * ctx->seq.cap);
    ctx->seq.ordinals = (int*) palloc(sizeof(int) * ctx->seq.cap);
    ctx->seq.child_hashes = (bytea**) palloc(sizeof(bytea*) * ctx->seq.cap);
    ctx->seq.rle_counts = (int*) palloc(sizeof(int) * ctx->seq.cap);

    LIST_INIT(ctx->sig, 64);
    ctx->sig.context_type_ids = (int*) palloc(sizeof(int) * ctx->sig.cap);
    ctx->sig.entity_hashes = (bytea**) palloc(sizeof(bytea*) * ctx->sig.cap);
    ctx->sig.mus = (double*) palloc(sizeof(double) * ctx->sig.cap);
}

static bytea* bytes_to_bytea(const uint8_t* bytes, size_t len)
{
    bytea* b = (bytea*) palloc(VARHDRSZ + len);
    SET_VARSIZE(b, VARHDRSZ + len);
    if (len > 0) memcpy(VARDATA(b), bytes, len);
    return b;
}

static int pg_text_emit_callback(void* callback_ctx, const hartonomous_text_record_t* rec)
{
    PgTextEmitContext* ctx = (PgTextEmitContext*) callback_ctx;
    if (ctx == NULL || rec == NULL || rec->hash_a == NULL) return 1;

    switch (rec->kind) {
        case HARTONOMOUS_REC_ENTITY:
            ensure_hash_capacity(&ctx->ent);
            ctx->ent.hashes[ctx->ent.count++] = hash_to_bytea(rec->hash_a);
            return 0;

        case HARTONOMOUS_REC_CLASSIFICATION:
            ensure_class_capacity(&ctx->cls);
            ctx->cls.entity_hashes[ctx->cls.count] = hash_to_bytea(rec->hash_a);
            ctx->cls.entity_type_ids[ctx->cls.count] = native_entity_type_id_for(ctx->ids, rec->subkind);
            ctx->cls.count++;
            return 0;

        case HARTONOMOUS_REC_PHYSICALITY:
            if (rec->hash_b == NULL || rec->geometry == NULL || rec->geometry_len == 0) return 1;
            ensure_phys_capacity(&ctx->phys);
            ctx->phys.phys_type_ids[ctx->phys.count] = native_physicality_type_id_for(ctx->ids, rec->subkind);
            ctx->phys.entity_hashes[ctx->phys.count] = hash_to_bytea(rec->hash_a);
            ctx->phys.content_hashes[ctx->phys.count] = hash_to_bytea(rec->hash_b);
            ctx->phys.geometries[ctx->phys.count] = bytes_to_bytea(rec->geometry, rec->geometry_len);
            ctx->phys.count++;
            return 0;

        case HARTONOMOUS_REC_SEQUENCE:
            if (rec->hash_b == NULL) return 1;
            if (sequence_tail_extends_run(&ctx->seq, rec)) {
                ctx->seq.rle_counts[ctx->seq.count - 1]++;
                return 0;
            }
            ensure_seq_capacity(&ctx->seq);
            ctx->seq.parent_hashes[ctx->seq.count] = hash_to_bytea(rec->hash_a);
            ctx->seq.ordinals[ctx->seq.count] = rec->int_param;
            ctx->seq.child_hashes[ctx->seq.count] = hash_to_bytea(rec->hash_b);
            ctx->seq.rle_counts[ctx->seq.count] = 1;
            ctx->seq.count++;
            return 0;

        case HARTONOMOUS_REC_SIGNIFICANCE:
            ensure_sig_capacity(&ctx->sig);
            ctx->sig.context_type_ids[ctx->sig.count] = native_context_type_id_for(ctx->ids, rec->subkind);
            ctx->sig.entity_hashes[ctx->sig.count] = hash_to_bytea(rec->hash_a);
            ctx->sig.mus[ctx->sig.count] = rec->double_param;
            ctx->sig.count++;
            return 0;

        default:
            return 1;
    }
}

static int ensure_libhartonomous_ucd_loaded(void)
{
    char dir[1024];
    const char* env;

    if (hartonomous_ucd_loaded_state() == 1) return 0;

    env = getenv("HARTONOMOUS_UCD_BLOB_DIR");
    if (env && *env) {
        snprintf(dir, sizeof(dir), "%s", env);
    } else {
        char share[MAXPGPATH];
        get_share_path(my_exec_path, share);
        snprintf(dir, sizeof(dir), "%s/extension/hartonomous-ucd", share);
    }
    return hartonomous_ucd_load(dir);
}

static Datum make_text_decompose_summary(
    FunctionCallInfo fcinfo,
    const TextDecomposeSummary* summary,
    bytea* root_hash,
    int root_entity_type_id)
{
    TupleDesc tupdesc;
    Datum values[9];
    bool  nulls[9] = { false, false, false, false, false, false, false, false, false };
    HeapTuple tup;

    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE) {
        ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                        errmsg("pg_text_decompose: composite type expected")));
    }
    BlessTupleDesc(tupdesc);

    values[0] = Int64GetDatum(summary->entity_count);
    values[1] = Int64GetDatum(summary->edge_count);
    values[2] = Int64GetDatum(summary->edge_member_count);
    values[3] = Int64GetDatum(summary->physicality_count);
    values[4] = Int64GetDatum(summary->composition_child_count);
    values[5] = Int64GetDatum(summary->significance_count);
    values[6] = Int64GetDatum(summary->classification_count);

    if (root_hash != NULL) {
        values[7] = PointerGetDatum(root_hash);
        values[8] = Int32GetDatum(root_entity_type_id);
    } else {
        values[7] = (Datum) 0;
        values[8] = (Datum) 0;
        nulls[7] = true;
        nulls[8] = true;
    }

    tup = heap_form_tuple(tupdesc, values, nulls);
    return HeapTupleGetDatum(tup);
}

/* ═════════════════════════════════════════════════════════════════════
 * (10) Main entry point — substrate.text_decompose
 * ═════════════════════════════════════════════════════════════════════ */
PG_FUNCTION_INFO_V1(pg_text_decompose);

Datum pg_text_decompose(PG_FUNCTION_ARGS)
{
    bytea* utf8_arg;
    text*  top_entity_type_arg;
    double trust_mu;
    text*  provenance_arg;
    bool   has_model_source;
    int    model_source_id;
    const uint8_t* utf8;
    size_t utf8_len;
    char* top_entity_type;
    char* provenance_code;
    int spi_owned;
    RefIds ids;
    int top_entity_type_id;
    int native_top_kind;
    int native_root_entity_type_id;
    TextDecomposeSummary summary;
    PgTextEmitContext emit_ctx;
    uint8_t root_hash[HASH_LEN];
    int rc;
    bytea* root_hash_out;

    utf8_arg = PG_GETARG_BYTEA_PP(0);
    top_entity_type_arg = PG_GETARG_TEXT_PP(1);
    trust_mu = PG_GETARG_FLOAT8(2);
    provenance_arg = PG_GETARG_TEXT_PP(3);
    /* p_model_source_id (5th param) is OPTIONAL with default NULL; when
     * non-NULL we emit substrate.entity_model_source linking the root
     * composition entity to that model_source row. Per AP-9 the model
     * source is placement metadata — never enters the entity hash. */
    has_model_source = !PG_ARGISNULL(4);
    model_source_id = has_model_source ? PG_GETARG_INT32(4) : 0;

    utf8 = (const uint8_t*) VARDATA_ANY(utf8_arg);
    utf8_len = VARSIZE_ANY_EXHDR(utf8_arg);
    top_entity_type = text_to_cstring(top_entity_type_arg);
    provenance_code = text_to_cstring(provenance_arg);

    spi_owned = (SPI_connect() == SPI_OK_CONNECT);

    memset(&ids, 0, sizeof(ids));
    resolve_ref_ids(&ids, provenance_code);
    top_entity_type_id = top_entity_type_id_for(&ids, top_entity_type);
    native_top_kind = native_top_kind_for(top_entity_type);
    native_root_entity_type_id = native_entity_type_id_for(&ids, native_top_kind);

    memset(&summary, 0, sizeof(summary));

    if (utf8_len == 0) {
        if (spi_owned) SPI_finish();
        PG_RETURN_DATUM(make_text_decompose_summary(fcinfo, &summary, NULL, 0));
    }

    if (ensure_libhartonomous_ucd_loaded() != 0) {
        ereport(ERROR,
                (errcode(ERRCODE_EXTERNAL_ROUTINE_EXCEPTION),
                 errmsg("pg_text_decompose: libhartonomous UCD blob is not loaded"),
                 errhint("Install hartonomous-ucd beside the extension or set HARTONOMOUS_UCD_BLOB_DIR.")));
    }

    init_emit_context(&emit_ctx, &ids);

    rc = hartonomous_text_decompose(
        utf8,
        utf8_len,
        native_top_kind,
        trust_mu,
        pg_text_emit_callback,
        &emit_ctx,
        root_hash,
        NULL,
        NULL);

    if (rc == -3) {
        if (spi_owned) SPI_finish();
        PG_RETURN_DATUM(make_text_decompose_summary(fcinfo, &summary, NULL, 0));
    }
    if (rc != 0) {
        ereport(ERROR,
                (errcode(ERRCODE_EXTERNAL_ROUTINE_EXCEPTION),
                 errmsg("pg_text_decompose: hartonomous_text_decompose returned %d", rc)));
    }

    root_hash_out = hash_to_bytea(root_hash);
    if (top_entity_type_id != native_root_entity_type_id) {
        ensure_class_capacity(&emit_ctx.cls);
        emit_ctx.cls.entity_hashes[emit_ctx.cls.count] = root_hash_out;
        emit_ctx.cls.entity_type_ids[emit_ctx.cls.count] = top_entity_type_id;
        emit_ctx.cls.count++;
    }

    /* ── Bulk flush directly into substrate core tables ─────────── */
    flush_entities(&emit_ctx.ent);
    flush_classifications(&emit_ctx.cls, ids.provenance_id);
    flush_physicalities(&emit_ctx.phys, &emit_ctx.seq);
    flush_significance(&emit_ctx.sig);
    /* Optional model_source linkage for the root composition (e.g.,
     * Safetensors config.json text artifacts get linked to their model). */
    if (has_model_source && root_hash_out != NULL) {
        flush_model_source(root_hash_out, model_source_id);
    }

    summary.entity_count        = emit_ctx.ent.count;
    summary.classification_count = emit_ctx.cls.count;
    summary.physicality_count    = emit_ctx.phys.count;
    summary.composition_child_count = emit_ctx.seq.count;
    summary.significance_count   = emit_ctx.sig.count;
    summary.edge_count           = 0;
    summary.edge_member_count    = 0;

    if (spi_owned) SPI_finish();
    PG_RETURN_DATUM(make_text_decompose_summary(fcinfo, &summary, root_hash_out, top_entity_type_id));
}

/* ═════════════════════════════════════════════════════════════════════
 * (11) Batch entry point — substrate.text_decompose_batch
 *
 * Iterates the input arrays calling pg_text_decompose-equivalent logic
 * per text. For now serialized (PG SPI is not thread-safe — OpenMP
 * across SPI calls inside one backend is unsafe). The batched form
 * exists to amortize the SPI roundtrip overhead across many texts when
 * called from a single SQL invocation; further parallelism is the
 * background flush worker / multiple PG backends.
 * ═════════════════════════════════════════════════════════════════════ */
PG_FUNCTION_INFO_V1(pg_text_decompose_batch);

Datum pg_text_decompose_batch(PG_FUNCTION_ARGS)
{
    ArrayType* utf8s_arr;
    ArrayType* types_arr;
    ArrayType* mus_arr;
    ArrayType* provs_arr;
    bool       has_model_sources;
    ArrayType* msrc_arr;
    int n;
    Datum* utf8_d;
    bool* utf8_n;
    Datum* type_d;
    bool* type_n;
    Datum* mu_d;
    bool* mu_n;
    Datum* prov_d;
    bool* prov_n;
    Datum* msrc_d;
    bool* msrc_n;
    int dummy;
    TextDecomposeSummary total;
    int i;
    TupleDesc tupdesc;
    Datum values[9];
    bool  nulls[9] = { false,false,false,false,false,false,false,true,true };
    HeapTuple tup;

    utf8s_arr = PG_GETARG_ARRAYTYPE_P(0);
    types_arr = PG_GETARG_ARRAYTYPE_P(1);
    mus_arr = PG_GETARG_ARRAYTYPE_P(2);
    provs_arr = PG_GETARG_ARRAYTYPE_P(3);
    /* p_model_source_ids (5th param, OPTIONAL int[]). NULL or omitted →
     * no model-source linkage. Per-row NULL elements skip linkage for
     * that row only. */
    has_model_sources = !PG_ARGISNULL(4);
    msrc_arr = has_model_sources ? PG_GETARG_ARRAYTYPE_P(4) : NULL;

    n = ArrayGetNItems(ARR_NDIM(utf8s_arr), ARR_DIMS(utf8s_arr));
    if (n != ArrayGetNItems(ARR_NDIM(types_arr), ARR_DIMS(types_arr)) ||
        n != ArrayGetNItems(ARR_NDIM(mus_arr),   ARR_DIMS(mus_arr))   ||
        n != ArrayGetNItems(ARR_NDIM(provs_arr), ARR_DIMS(provs_arr))) {
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                        errmsg("pg_text_decompose_batch: input arrays must have matching length")));
    }
    if (has_model_sources &&
        n != ArrayGetNItems(ARR_NDIM(msrc_arr), ARR_DIMS(msrc_arr))) {
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                        errmsg("pg_text_decompose_batch: model_source_ids array length mismatch")));
    }

    /* Iterate elements; recurse into pg_text_decompose for each.
     * A single backend can't safely fan out SPI across threads, so this
     * is sequential. Wall-time win comes from amortizing the function-
     * call overhead and from using shared cached property tables. */
    msrc_d = NULL;
    msrc_n = NULL;
    deconstruct_array(utf8s_arr, BYTEAOID, -1, false, TYPALIGN_INT, &utf8_d, &utf8_n, &dummy);
    deconstruct_array(types_arr, TEXTOID, -1, false, TYPALIGN_INT, &type_d, &type_n, &dummy);
    deconstruct_array(mus_arr,   FLOAT8OID, sizeof(float8), FLOAT8PASSBYVAL, TYPALIGN_DOUBLE, &mu_d, &mu_n, &dummy);
    deconstruct_array(provs_arr, TEXTOID, -1, false, TYPALIGN_INT, &prov_d, &prov_n, &dummy);
    if (has_model_sources) {
        deconstruct_array(msrc_arr, INT4OID, sizeof(int32), true, TYPALIGN_INT, &msrc_d, &msrc_n, &dummy);
    }

    memset(&total, 0, sizeof(total));

    for (i = 0; i < n; i++) {
        LOCAL_FCINFO(inner, 5);
        Datum result;
        bool isnull;

        if (utf8_n[i] || type_n[i] || mu_n[i] || prov_n[i]) continue;
        InitFunctionCallInfoData(*inner, NULL, 5, InvalidOid, NULL, NULL);
        inner->args[0].value = utf8_d[i]; inner->args[0].isnull = false;
        inner->args[1].value = type_d[i]; inner->args[1].isnull = false;
        inner->args[2].value = mu_d[i];   inner->args[2].isnull = false;
        inner->args[3].value = prov_d[i]; inner->args[3].isnull = false;
        if (msrc_d != NULL && !msrc_n[i]) {
            inner->args[4].value  = msrc_d[i];
            inner->args[4].isnull = false;
        } else {
            inner->args[4].value  = (Datum) 0;
            inner->args[4].isnull = true;
        }
        result = pg_text_decompose(inner);
        if (!inner->isnull) {
            HeapTupleHeader hth = DatumGetHeapTupleHeader(result);
            HeapTupleData htd;
            htd.t_len = HeapTupleHeaderGetDatumLength(hth);
            ItemPointerSetInvalid(&(htd.t_self));
            htd.t_tableOid = InvalidOid;
            htd.t_data = hth;
            total.entity_count        += DatumGetInt64(GetAttributeByNum(hth, 1, &isnull));
            total.edge_count          += DatumGetInt64(GetAttributeByNum(hth, 2, &isnull));
            total.edge_member_count   += DatumGetInt64(GetAttributeByNum(hth, 3, &isnull));
            total.physicality_count   += DatumGetInt64(GetAttributeByNum(hth, 4, &isnull));
            total.composition_child_count += DatumGetInt64(GetAttributeByNum(hth, 5, &isnull));
            total.significance_count  += DatumGetInt64(GetAttributeByNum(hth, 6, &isnull));
            total.classification_count += DatumGetInt64(GetAttributeByNum(hth, 7, &isnull));
        }
    }

    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE) {
        ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                        errmsg("pg_text_decompose_batch: composite type expected")));
    }
    BlessTupleDesc(tupdesc);

    /* 9-field summary. Batch never returns a single root — root_hash and
     * root_entity_type_id are always NULL. Callers that need per-row
     * roots should iterate text_decompose() one row at a time. */
    values[0] = Int64GetDatum(total.entity_count);
    values[1] = Int64GetDatum(total.edge_count);
    values[2] = Int64GetDatum(total.edge_member_count);
    values[3] = Int64GetDatum(total.physicality_count);
    values[4] = Int64GetDatum(total.composition_child_count);
    values[5] = Int64GetDatum(total.significance_count);
    values[6] = Int64GetDatum(total.classification_count);
    values[7] = (Datum) 0;
    values[8] = (Datum) 0;
    tup = heap_form_tuple(tupdesc, values, nulls);
    PG_RETURN_DATUM(HeapTupleGetDatum(tup));
}
