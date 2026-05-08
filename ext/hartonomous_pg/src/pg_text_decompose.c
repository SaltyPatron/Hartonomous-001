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

/* ═════════════════════════════════════════════════════════════════════
 * (1) UTF-8 decode
 * ═════════════════════════════════════════════════════════════════════ */
size_t utf8_decode_one(const uint8_t* p, size_t len, int32_t* out)
{
    if (len == 0 || p == NULL || out == NULL) return 0;
    uint8_t b0 = p[0];

    if (b0 < 0x80) { *out = (int32_t)b0; return 1; }
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

/* ═════════════════════════════════════════════════════════════════════
 * (2) Codepoint property lookups — pure array loads from generated tables.
 *
 * No SPI, no DB round-trip, no per-backend cache load. The generated
 * tables (uc_gcb / uc_wb / uc_extended_pictographic / uc_incb) are baked
 * into the extension at build time from UCD 17.0.0; lookup is one array
 * dereference. Identical determinism guarantees as the SPI-fed cache,
 * with extension version pinning UCD version.
 * ═════════════════════════════════════════════════════════════════════ */

void pg_text_decompose_cache_load(void)  { /* no-op — embedded */ }
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
    d.codepoints   = (int32_t*) palloc(sizeof(int32_t) * cap);
    d.byte_offsets = (int32_t*) palloc(sizeof(int32_t) * cap);
    d.byte_widths  = (int32_t*) palloc(sizeof(int32_t) * cap);
    d.count        = 0;

    size_t pos = 0;
    while (pos < utf8_len) {
        int32_t cp;
        size_t consumed = utf8_decode_one(utf8 + pos, utf8_len - pos, &cp);
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
    b.indices = (int32_t*) palloc(sizeof(int32_t) * (d->count + 1));
    b.count = 0;

    if (d->count == 0) return b;

    /* GB1: sot ÷ — first cluster always starts at 0. */
    b.indices[b.count++] = 0;

    /* State for cross-cluster rules: */
    int riRun = 0;                        /* GB12/13 — trailing RI run length */
    int chainPict = 0;                    /* GB11 — saw Extended_Pictographic */
    int chainZwjAfterPict = 0;            /* GB11 — saw ZWJ after Pict (with optional Extends) */
    /* GB9c InCB state: tracks "Consonant (Linker|Extend)*" with at least one Linker */
    int incbConsonantSeen = 0;
    int incbLinkerSeen    = 0;

    for (int32_t i = 1; i < d->count; i++) {
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
    WordBreak prev_literal = wb_for(d->codepoints[0]);
    WordBreak prevSig      = wb_for(d->codepoints[0]);
    WordBreak prev2Sig     = WB_Other;
    int       prevSigPict  = is_extended_pictographic(d->codepoints[0]);
    int       riRun        = (prevSig == WB_RegionalIndicator) ? 1 : 0;

    for (int32_t i = 1; i < d->count; i++) {
        int32_t prev_cp = d->codepoints[i - 1];
        int32_t curr_cp = d->codepoints[i];
        WordBreak prev_lit = wb_for(prev_cp);
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
            while (k < d->count) {
                WordBreak wbk = wb_for(d->codepoints[k]);
                if (wbk == WB_Extend || wbk == WB_Format || wbk == WB_ZWJ) { k++; continue; }
                break;
            }
            int aheadIsLetter = (k < d->count) &&
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
            while (k < d->count) {
                WordBreak wbk = wb_for(d->codepoints[k]);
                if (wbk == WB_Extend || wbk == WB_Format || wbk == WB_ZWJ) { k++; continue; }
                break;
            }
            int aheadHeb = (k < d->count) && wb_for(d->codepoints[k]) == WB_HebrewLetter;
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
            while (k < d->count) {
                WordBreak wbk = wb_for(d->codepoints[k]);
                if (wbk == WB_Extend || wbk == WB_Format || wbk == WB_ZWJ) { k++; continue; }
                break;
            }
            int aheadNum = (k < d->count) && wb_for(d->codepoints[k]) == WB_Numeric;
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
    if (k <= 0) { out[0]=out[1]=out[2]=out[3]=0.0; return; }
    double sx=0, sy=0, sz=0, sm=0;
    for (int32_t i=0; i<k; i++) {
        sx += in[i*4+0]; sy += in[i*4+1]; sz += in[i*4+2]; sm += in[i*4+3];
    }
    double inv = 1.0 / (double)k;
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
    int rc = SPI_execute_with_args(sql, 1, argtype, args, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed != 1) {
        return 0;
    }
    bool isnull;
    Datum d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
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
    bytea**  wkbs;
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

/* Encode a POINTZM as PostGIS WKB (little-endian, EWKB with M flag set). */
static bytea* point4d_to_wkb(double x, double y, double z, double m)
{
    /* EWKB POINTZM: byte order(1) + type(4 with Z|M flags) + x(8) + y(8) + z(8) + m(8) = 37 bytes */
    bytea* b = (bytea*) palloc(VARHDRSZ + 37);
    SET_VARSIZE(b, VARHDRSZ + 37);
    uint8_t* p = (uint8_t*) VARDATA(b);
    p[0] = 0x01;  /* little-endian */
    /* Type: 1 (Point) | 0x80000000 (Z) | 0x40000000 (M) = 0xC0000001 */
    uint32_t type = 0xC0000001;
    memcpy(p + 1, &type, 4);
    memcpy(p + 5,  &x, 8);
    memcpy(p + 13, &y, 8);
    memcpy(p + 21, &z, 8);
    memcpy(p + 29, &m, 8);
    return b;
}

/* Encode a LINESTRINGZM with k vertices. */
static bytea* linestring4d_to_wkb(const double* verts /* k * 4 */, int k)
{
    /* EWKB LineString ZM: byte order(1) + type(4) + numpoints(4) + k * 32 */
    size_t sz = 1 + 4 + 4 + (size_t)k * 32;
    bytea* b = (bytea*) palloc(VARHDRSZ + sz);
    SET_VARSIZE(b, VARHDRSZ + sz);
    uint8_t* p = (uint8_t*) VARDATA(b);
    p[0] = 0x01;
    uint32_t type = 0xC0000002;  /* LineString | Z | M */
    memcpy(p + 1, &type, 4);
    uint32_t n = (uint32_t) k;
    memcpy(p + 5, &n, 4);
    uint8_t* vp = p + 9;
    for (int i = 0; i < k; i++) {
        memcpy(vp + 0,  &verts[i*4+0], 8);
        memcpy(vp + 8,  &verts[i*4+1], 8);
        memcpy(vp + 16, &verts[i*4+2], 8);
        memcpy(vp + 24, &verts[i*4+3], 8);
        vp += 32;
    }
    return b;
}

/* ═════════════════════════════════════════════════════════════════════
 * (8b) Public WKB constructor — substrate.ls4d_from_centroids
 *
 * Lifts the in-process LINESTRINGZM EWKB writer to a SQL-callable
 * function. Producers building edges from participant centroids can call
 * this once per edge; recompose / inference paths use it when only
 * hashes are known and centroids must be joined from substrate.physicality.
 *
 * Returns bytea (EWKB). The SQL declaration wraps with
 * ST_GeomFromWKB(..., 0)::geometry(LINESTRINGZM) so callers receive a
 * proper PostGIS geometry without re-encoding.
 * ═════════════════════════════════════════════════════════════════════ */
PG_FUNCTION_INFO_V1(pg_ls4d_from_centroids_wkb);

Datum pg_ls4d_from_centroids_wkb(PG_FUNCTION_ARGS)
{
    ArrayType* arr = PG_GETARG_ARRAYTYPE_P(0);
    int n = ArrayGetNItems(ARR_NDIM(arr), ARR_DIMS(arr));
    if (n < 2) {
        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                        errmsg("ls4d_from_centroids: at least 2 vertices required (got %d)", n)));
    }

    /* point4d on-disk layout: 4 × float8, alignment double, plain storage,
     * 32 bytes per element. The element OID is point4d's typoid. We use
     * deconstruct_array with -1 typlen and pass-by-reference because point4d
     * is a fixed-size composite stored as a varlena-like blob with alignment
     * 'd'. Per pg_point4d.c, INTERNALLENGTH = 32 and ALIGNMENT = double. */
    Oid elem_type = ARR_ELEMTYPE(arr);
    int16 typlen;
    bool  typbyval;
    char  typalign;
    get_typlenbyvalalign(elem_type, &typlen, &typbyval, &typalign);

    Datum* elems;
    bool*  nulls;
    int    nelems;
    deconstruct_array(arr, elem_type, typlen, typbyval, typalign, &elems, &nulls, &nelems);

    double* verts = (double*) palloc(sizeof(double) * 4 * nelems);
    for (int i = 0; i < nelems; i++) {
        if (nulls[i]) {
            ereport(ERROR, (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                            errmsg("ls4d_from_centroids: NULL element at index %d", i)));
        }
        /* point4d is INTERNALLENGTH=32, no varlena header — Datum points
         * directly at the 4 × double payload. */
        const double* p = (const double*) DatumGetPointer(elems[i]);
        verts[i*4+0] = p[0];
        verts[i*4+1] = p[1];
        verts[i*4+2] = p[2];
        verts[i*4+3] = p[3];
    }

    bytea* wkb = linestring4d_to_wkb(verts, nelems);
    PG_RETURN_BYTEA_P(wkb);
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
    for (int i = 0; i < n; i++) datums[i] = PointerGetDatum(items[i]);
    int dims[1] = { n };
    int lbs[1] = { 1 };
    return construct_md_array(datums, NULL, 1, dims, lbs, BYTEAOID, -1, false, TYPALIGN_INT);
}

static ArrayType* build_int4_array(int* items, int n)
{
    Datum* datums = (Datum*) palloc(sizeof(Datum) * n);
    for (int i = 0; i < n; i++) datums[i] = Int32GetDatum(items[i]);
    int dims[1] = { n };
    int lbs[1] = { 1 };
    return construct_md_array(datums, NULL, 1, dims, lbs, INT4OID, sizeof(int32), true, TYPALIGN_INT);
}

static ArrayType* build_float8_array(double* items, int n)
{
    Datum* datums = (Datum*) palloc(sizeof(Datum) * n);
    for (int i = 0; i < n; i++) datums[i] = Float8GetDatum(items[i]);
    int dims[1] = { n };
    int lbs[1] = { 1 };
    return construct_md_array(datums, NULL, 1, dims, lbs, FLOAT8OID, sizeof(float8), FLOAT8PASSBYVAL, TYPALIGN_DOUBLE);
}

static void flush_entities(HashList* L)
{
    if (L->count == 0) return;
    Oid types[1] = { BYTEAARRAYOID };
    Datum vals[1] = { PointerGetDatum(build_bytea_array(L->hashes, L->count)) };
    int rc = SPI_execute_with_args(
        "INSERT INTO substrate.entity (hash) "
        "SELECT DISTINCT h FROM unnest($1::bytea[]) AS h "
        "ON CONFLICT (hash) DO NOTHING",
        1, types, vals, NULL, false, 0);
    if (rc != SPI_OK_INSERT) elog(ERROR, "flush_entities: SPI_execute (%d)", rc);
}

static void flush_classifications(ClassList* L, int provenance_id)
{
    if (L->count == 0) return;
    Oid types[3] = { BYTEAARRAYOID, INT4ARRAYOID, INT4OID };
    Datum vals[3] = {
        PointerGetDatum(build_bytea_array(L->entity_hashes, L->count)),
        PointerGetDatum(build_int4_array(L->entity_type_ids, L->count)),
        Int32GetDatum(provenance_id)
    };
    int rc = SPI_execute_with_args(
        "INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id) "
        "SELECT DISTINCT h, t, $3 FROM unnest($1::bytea[], $2::int[]) AS u(h, t) "
        "ON CONFLICT (entity_hash, entity_type_id, provenance_id) DO NOTHING",
        3, types, vals, NULL, false, 0);
    if (rc != SPI_OK_INSERT) elog(ERROR, "flush_classifications: SPI_execute (%d)", rc);
}

static void flush_physicalities(PhysList* L)
{
    if (L->count == 0) return;
    Oid types[4] = { INT4ARRAYOID, BYTEAARRAYOID, BYTEAARRAYOID, BYTEAARRAYOID };
    Datum vals[4] = {
        PointerGetDatum(build_int4_array(L->phys_type_ids, L->count)),
        PointerGetDatum(build_bytea_array(L->entity_hashes, L->count)),
        PointerGetDatum(build_bytea_array(L->content_hashes, L->count)),
        PointerGetDatum(build_bytea_array(L->wkbs, L->count))
    };
    int rc = SPI_execute_with_args(
        "INSERT INTO substrate.physicality (physicality_type_id, entity_hash, content_hash, geom) "
        "SELECT DISTINCT ON (pt, eh, ch) pt, eh, ch, ST_GeomFromWKB(wkb, 0) "
        "  FROM unnest($1::int[], $2::bytea[], $3::bytea[], $4::bytea[]) AS u(pt, eh, ch, wkb) "
        "ON CONFLICT (physicality_type_id, entity_hash, content_hash) DO NOTHING",
        4, types, vals, NULL, false, 0);
    if (rc != SPI_OK_INSERT) elog(ERROR, "flush_physicalities: SPI_execute (%d)", rc);
}

static void flush_sequences(SeqList* L)
{
    if (L->count == 0) return;
    Oid types[4] = { BYTEAARRAYOID, INT4ARRAYOID, BYTEAARRAYOID, INT4ARRAYOID };
    Datum vals[4] = {
        PointerGetDatum(build_bytea_array(L->parent_hashes, L->count)),
        PointerGetDatum(build_int4_array(L->ordinals, L->count)),
        PointerGetDatum(build_bytea_array(L->child_hashes, L->count)),
        PointerGetDatum(build_int4_array(L->rle_counts, L->count))
    };
    int rc = SPI_execute_with_args(
        "INSERT INTO substrate.sequence (parent_hash, ordinal, child_hash, rle_count) "
        "SELECT DISTINCT ON (p, o) p, o, c, r "
        "  FROM unnest($1::bytea[], $2::int[], $3::bytea[], $4::int[]) AS u(p, o, c, r) "
        "ON CONFLICT (parent_hash, ordinal) DO NOTHING",
        4, types, vals, NULL, false, 0);
    if (rc != SPI_OK_INSERT) elog(ERROR, "flush_sequences: SPI_execute (%d)", rc);
}

static void flush_significance(SigList* L)
{
    if (L->count == 0) return;
    Oid types[3] = { INT4ARRAYOID, BYTEAARRAYOID, FLOAT8ARRAYOID };
    Datum vals[3] = {
        PointerGetDatum(build_int4_array(L->context_type_ids, L->count)),
        PointerGetDatum(build_bytea_array(L->entity_hashes, L->count)),
        PointerGetDatum(build_float8_array(L->mus, L->count))
    };
    int rc = SPI_execute_with_args(
        "INSERT INTO substrate.entity_significance (context_type_id, entity_hash, mu) "
        "SELECT DISTINCT ON (c, h) c, h, m "
        "  FROM unnest($1::int[], $2::bytea[], $3::float8[]) AS u(c, h, m) "
        "ON CONFLICT (context_type_id, entity_hash) DO NOTHING",
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

/* ═════════════════════════════════════════════════════════════════════
 * (10) Main entry point — substrate.text_decompose
 * ═════════════════════════════════════════════════════════════════════ */
PG_FUNCTION_INFO_V1(pg_text_decompose);

Datum pg_text_decompose(PG_FUNCTION_ARGS)
{
    bytea* utf8_arg            = PG_GETARG_BYTEA_PP(0);
    text*  top_entity_type_arg = PG_GETARG_TEXT_PP(1);
    double trust_mu            = PG_GETARG_FLOAT8(2);
    text*  provenance_arg      = PG_GETARG_TEXT_PP(3);
    /* p_model_source_id (5th param) is OPTIONAL with default NULL; when
     * non-NULL we emit substrate.entity_model_source linking the root
     * composition entity to that model_source row. Per AP-9 the model
     * source is placement metadata — never enters the entity hash. */
    bool   has_model_source    = !PG_ARGISNULL(4);
    int    model_source_id     = has_model_source ? PG_GETARG_INT32(4) : 0;

    const uint8_t* utf8 = (const uint8_t*) VARDATA_ANY(utf8_arg);
    size_t utf8_len     = VARSIZE_ANY_EXHDR(utf8_arg);
    char* top_entity_type = text_to_cstring(top_entity_type_arg);
    char* provenance_code = text_to_cstring(provenance_arg);

    int spi_owned = (SPI_connect() == SPI_OK_CONNECT);

    /* Codepoint cache is no longer SPI-loaded — atoms come from the
     * embedded extension's mmap'd blob, populated lazily per block on
     * first access (huc_cp_hash_at / huc_cp_centroid_at). No-op kept
     * for ABI compatibility. */
    pg_text_decompose_cache_load();

    RefIds ids;
    memset(&ids, 0, sizeof(ids));
    resolve_ref_ids(&ids, provenance_code);
    int top_entity_type_id =
        (strcmp(top_entity_type, "text_composition") == 0) ? ids.entity_type_text_composition :
        (strcmp(top_entity_type, "word_form") == 0)        ? ids.entity_type_word_form :
        (strcmp(top_entity_type, "lemma") == 0)            ? spi_lookup_id("SELECT id FROM substrate.entity_type WHERE code = $1", "lemma") :
        ids.entity_type_text_composition;

    DecodedCodepoints d  = decode_utf8_buf(utf8, utf8_len);

    TextDecomposeSummary summary;
    memset(&summary, 0, sizeof(summary));

    if (d.count == 0) {
        if (spi_owned) SPI_finish();
        TupleDesc tupdesc;
        if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE) {
            ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                            errmsg("pg_text_decompose: composite type expected")));
        }
        BlessTupleDesc(tupdesc);
        /* 9-field summary: 7 counts + root_hash + root_entity_type_id.
         * Empty input → counts at 0, root fields NULL. */
        Datum values[9] = {0,0,0,0,0,0,0,0,0};
        bool  nulls[9]  = { false,false,false,false,false,false,false, true, true };
        for (int i=0;i<7;i++) values[i] = Int64GetDatum(0);
        HeapTuple tup = heap_form_tuple(tupdesc, values, nulls);
        PG_RETURN_DATUM(HeapTupleGetDatum(tup));
    }

    /* Stage 1: codepoints — hashes and centroids in batch. */
    uint8_t* cp_hashes = (uint8_t*) palloc((size_t)d.count * HASH_LEN);
    hash_codepoints(&d, cp_hashes);

    double* cp_centroids = (double*) palloc(sizeof(double) * 4 * d.count);
    compute_codepoint_centroids(&d, cp_centroids);

    /* Stage 2: grapheme cluster boundaries. */
    BoundaryArray graphemes = grapheme_boundaries(&d);

    /* Stage 3: word boundaries. */
    WordArray words = word_boundaries(&d);

    /* Allocate per-grapheme + per-word + composition hashes/centroids. */
    int gN = graphemes.count;
    int wN = words.count;

    uint8_t* gc_hashes    = (uint8_t*) palloc((size_t)gN * HASH_LEN);
    double*  gc_centroids = (double*)  palloc(sizeof(double) * 4 * gN);
    uint8_t* w_hashes     = (uint8_t*) palloc((size_t)wN * HASH_LEN);
    double*  w_centroids  = (double*)  palloc(sizeof(double) * 4 * wN);

    /* Stage 2b: grapheme hashes via batched merkle, centroids via mean. */
    for (int gi = 0; gi < gN; gi++) {
        int firstCp = graphemes.indices[gi];
        int endCp   = (gi + 1 < gN) ? graphemes.indices[gi + 1] : d.count;
        int cpCount = endCp - firstCp;

        merkle_chain(cp_hashes + (size_t)firstCp * HASH_LEN, (size_t)cpCount,
                     gc_hashes + (size_t)gi * HASH_LEN);
        mean_centroid(cp_centroids + firstCp * 4, cpCount, gc_centroids + gi * 4);
    }

    /* Word hashes: Merkle of constituent grapheme hashes; centroid mean of theirs. */
    for (int wi = 0; wi < wN; wi++) {
        int firstCpW = words.indices[wi];
        int endCpW   = (wi + 1 < wN) ? words.indices[wi + 1] : d.count;
        /* Find grapheme cluster bounds covering [firstCpW, endCpW). */
        int firstGc = 0, endGc = gN;
        for (int gi = 0; gi < gN; gi++) {
            if (graphemes.indices[gi] == firstCpW) { firstGc = gi; break; }
        }
        for (int gi = firstGc; gi < gN; gi++) {
            int gStart = graphemes.indices[gi];
            if (gStart >= endCpW) { endGc = gi; break; }
        }
        int gcCount = endGc - firstGc;
        if (gcCount <= 0) gcCount = 1;
        merkle_chain(gc_hashes + (size_t)firstGc * HASH_LEN, (size_t)gcCount,
                     w_hashes + (size_t)wi * HASH_LEN);
        mean_centroid(gc_centroids + firstGc * 4, gcCount, w_centroids + wi * 4);
    }

    /* Composition: Merkle of word hashes, centroid mean of word centroids. */
    uint8_t comp_hash[HASH_LEN];
    double  comp_centroid[4];
    if (wN > 0) {
        merkle_chain(w_hashes, (size_t)wN, comp_hash);
        mean_centroid(w_centroids, wN, comp_centroid);
    } else {
        merkle_chain(NULL, 0, comp_hash);
        comp_centroid[0]=comp_centroid[1]=comp_centroid[2]=comp_centroid[3]=0.0;
    }

    /* ── Build staging accumulators ─────────────────────────────── */
    /* Capacity = N codepoints + M graphemes + K words + 1 composition. */
    int totalEnts = d.count + gN + wN + 1;

    HashList ent;       LIST_INIT(ent, totalEnts);
    ent.hashes = (bytea**) palloc(sizeof(bytea*) * ent.cap);

    ClassList cls;      LIST_INIT(cls, totalEnts);
    cls.entity_hashes = (bytea**) palloc(sizeof(bytea*) * cls.cap);
    cls.entity_type_ids = (int*) palloc(sizeof(int) * cls.cap);

    PhysList phys;      LIST_INIT(phys, totalEnts);
    phys.phys_type_ids = (int*) palloc(sizeof(int) * phys.cap);
    phys.entity_hashes = (bytea**) palloc(sizeof(bytea*) * phys.cap);
    phys.content_hashes = (bytea**) palloc(sizeof(bytea*) * phys.cap);
    phys.wkbs = (bytea**) palloc(sizeof(bytea*) * phys.cap);

    /* Sequence rows: parent → ordered children. Capacity bound = total
     * grapheme.count + word.count + composition.count = N + M + K children. */
    int seqCap = d.count + gN + wN + 16;
    SeqList seq;        LIST_INIT(seq, seqCap);
    seq.parent_hashes = (bytea**) palloc(sizeof(bytea*) * seq.cap);
    seq.ordinals = (int*) palloc(sizeof(int) * seq.cap);
    seq.child_hashes = (bytea**) palloc(sizeof(bytea*) * seq.cap);
    seq.rle_counts = (int*) palloc(sizeof(int) * seq.cap);

    SigList sig;        LIST_INIT(sig, totalEnts);
    sig.context_type_ids = (int*) palloc(sizeof(int) * sig.cap);
    sig.entity_hashes = (bytea**) palloc(sizeof(bytea*) * sig.cap);
    sig.mus = (double*) palloc(sizeof(double) * sig.cap);

    /* Codepoints: entity + classification + s3_position physicality + significance. */
    for (int i = 0; i < d.count; i++) {
        bytea* h = hash_to_bytea(cp_hashes + (size_t)i * HASH_LEN);
        ent.hashes[ent.count++] = h;
        cls.entity_hashes[cls.count] = h;
        cls.entity_type_ids[cls.count] = ids.entity_type_codepoint;
        cls.count++;
        phys.phys_type_ids[phys.count] = ids.physicality_type_s3_position;
        phys.entity_hashes[phys.count] = h;
        phys.content_hashes[phys.count] = h;
        phys.wkbs[phys.count] = point4d_to_wkb(
            cp_centroids[i*4+0], cp_centroids[i*4+1],
            cp_centroids[i*4+2], cp_centroids[i*4+3]);
        phys.count++;
        sig.context_type_ids[sig.count] = ids.significance_context_source_authority;
        sig.entity_hashes[sig.count] = h;
        sig.mus[sig.count] = trust_mu;
        sig.count++;
    }

    /* Grapheme clusters. */
    for (int gi = 0; gi < gN; gi++) {
        int firstCp = graphemes.indices[gi];
        int endCp   = (gi + 1 < gN) ? graphemes.indices[gi + 1] : d.count;
        int cpCount = endCp - firstCp;

        bytea* gh = hash_to_bytea(gc_hashes + (size_t)gi * HASH_LEN);
        ent.hashes[ent.count++] = gh;
        cls.entity_hashes[cls.count] = gh;
        cls.entity_type_ids[cls.count] = ids.entity_type_grapheme_cluster;
        cls.count++;
        sig.context_type_ids[sig.count] = ids.significance_context_source_authority;
        sig.entity_hashes[sig.count] = gh;
        sig.mus[sig.count] = trust_mu;
        sig.count++;

        if (cpCount == 1) {
            phys.phys_type_ids[phys.count] = ids.physicality_type_s3_position;
            phys.entity_hashes[phys.count] = gh;
            phys.content_hashes[phys.count] = gh;
            phys.wkbs[phys.count] = point4d_to_wkb(
                gc_centroids[gi*4+0], gc_centroids[gi*4+1],
                gc_centroids[gi*4+2], gc_centroids[gi*4+3]);
            phys.count++;
        } else if (cpCount > 1) {
            phys.phys_type_ids[phys.count] = ids.physicality_type_contour;
            phys.entity_hashes[phys.count] = gh;
            phys.content_hashes[phys.count] = gh;
            phys.wkbs[phys.count] = linestring4d_to_wkb(cp_centroids + firstCp * 4, cpCount);
            phys.count++;
        }

        /* Sequence rows: grapheme → codepoint child @ ordinal. */
        for (int k = 0; k < cpCount; k++) {
            seq.parent_hashes[seq.count] = gh;
            seq.ordinals[seq.count] = k + 1;
            seq.child_hashes[seq.count] = hash_to_bytea(cp_hashes + (size_t)(firstCp + k) * HASH_LEN);
            seq.rle_counts[seq.count] = 1;
            seq.count++;
        }
    }

    /* Word forms / text_compositions per word range. */
    for (int wi = 0; wi < wN; wi++) {
        int firstCpW = words.indices[wi];
        int endCpW   = (wi + 1 < wN) ? words.indices[wi + 1] : d.count;
        /* Re-find grapheme bounds for sequence emission. */
        int firstGc = 0, endGc = gN;
        for (int gi = 0; gi < gN; gi++) {
            if (graphemes.indices[gi] == firstCpW) { firstGc = gi; break; }
        }
        for (int gi = firstGc; gi < gN; gi++) {
            int gStart = graphemes.indices[gi];
            if (gStart >= endCpW) { endGc = gi; break; }
        }
        int gcCount = endGc - firstGc;
        if (gcCount <= 0) gcCount = 1;

        bytea* wh = hash_to_bytea(w_hashes + (size_t)wi * HASH_LEN);
        ent.hashes[ent.count++] = wh;
        cls.entity_hashes[cls.count] = wh;
        cls.entity_type_ids[cls.count] = (words.kinds[wi] == WK_Other)
            ? ids.entity_type_text_composition
            : ids.entity_type_word_form;
        cls.count++;
        sig.context_type_ids[sig.count] = ids.significance_context_source_authority;
        sig.entity_hashes[sig.count] = wh;
        sig.mus[sig.count] = trust_mu;
        sig.count++;

        if (gcCount == 1) {
            phys.phys_type_ids[phys.count] = ids.physicality_type_s3_position;
            phys.entity_hashes[phys.count] = wh;
            phys.content_hashes[phys.count] = wh;
            phys.wkbs[phys.count] = point4d_to_wkb(
                w_centroids[wi*4+0], w_centroids[wi*4+1],
                w_centroids[wi*4+2], w_centroids[wi*4+3]);
            phys.count++;
        } else if (gcCount > 1) {
            phys.phys_type_ids[phys.count] = ids.physicality_type_contour;
            phys.entity_hashes[phys.count] = wh;
            phys.content_hashes[phys.count] = wh;
            phys.wkbs[phys.count] = linestring4d_to_wkb(gc_centroids + firstGc * 4, gcCount);
            phys.count++;
        }

        /* Sequence rows: word → grapheme child @ ordinal. */
        for (int k = 0; k < gcCount; k++) {
            seq.parent_hashes[seq.count] = wh;
            seq.ordinals[seq.count] = k + 1;
            seq.child_hashes[seq.count] = hash_to_bytea(gc_hashes + (size_t)(firstGc + k) * HASH_LEN);
            seq.rle_counts[seq.count] = 1;
            seq.count++;
        }
    }

    /* Composition entity (root). */
    bytea* root_hash_out = NULL;
    {
        bytea* ch = hash_to_bytea(comp_hash);
        root_hash_out = ch;
        ent.hashes[ent.count++] = ch;
        cls.entity_hashes[cls.count] = ch;
        cls.entity_type_ids[cls.count] = top_entity_type_id;
        cls.count++;
        sig.context_type_ids[sig.count] = ids.significance_context_source_authority;
        sig.entity_hashes[sig.count] = ch;
        sig.mus[sig.count] = trust_mu;
        sig.count++;
        if (wN == 1) {
            phys.phys_type_ids[phys.count] = ids.physicality_type_s3_position;
            phys.entity_hashes[phys.count] = ch;
            phys.content_hashes[phys.count] = ch;
            phys.wkbs[phys.count] = point4d_to_wkb(
                comp_centroid[0], comp_centroid[1],
                comp_centroid[2], comp_centroid[3]);
            phys.count++;
        } else if (wN > 1) {
            phys.phys_type_ids[phys.count] = ids.physicality_type_contour;
            phys.entity_hashes[phys.count] = ch;
            phys.content_hashes[phys.count] = ch;
            phys.wkbs[phys.count] = linestring4d_to_wkb(w_centroids, wN);
            phys.count++;
        }
        /* Sequence rows: composition → word child @ ordinal. */
        for (int k = 0; k < wN; k++) {
            seq.parent_hashes[seq.count] = ch;
            seq.ordinals[seq.count] = k + 1;
            seq.child_hashes[seq.count] = hash_to_bytea(w_hashes + (size_t)k * HASH_LEN);
            seq.rle_counts[seq.count] = 1;
            seq.count++;
        }
    }

    /* ── Bulk flush directly into substrate core tables ─────────── */
    flush_entities(&ent);
    flush_classifications(&cls, ids.provenance_id);
    flush_physicalities(&phys);
    flush_sequences(&seq);
    flush_significance(&sig);
    /* Optional model_source linkage for the root composition (e.g.,
     * Safetensors config.json text artifacts get linked to their model). */
    if (has_model_source && root_hash_out != NULL) {
        flush_model_source(root_hash_out, model_source_id);
    }

    summary.entity_count        = ent.count;
    summary.classification_count = cls.count;
    summary.physicality_count    = phys.count;
    summary.sequence_count       = seq.count;
    summary.significance_count   = sig.count;
    summary.edge_count           = 0;
    summary.edge_member_count    = 0;

    if (spi_owned) SPI_finish();

    TupleDesc tupdesc;
    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE) {
        ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                        errmsg("pg_text_decompose: composite type expected")));
    }
    BlessTupleDesc(tupdesc);

    /* 9-field summary: 7 counts + root_hash (bytea) + root_entity_type_id (int).
     * Callers that don't need the root can ignore the last two fields. */
    Datum values[9];
    bool  nulls[9] = { false, false, false, false, false, false, false, false, false };
    values[0] = Int64GetDatum(summary.entity_count);
    values[1] = Int64GetDatum(summary.edge_count);
    values[2] = Int64GetDatum(summary.edge_member_count);
    values[3] = Int64GetDatum(summary.physicality_count);
    values[4] = Int64GetDatum(summary.sequence_count);
    values[5] = Int64GetDatum(summary.significance_count);
    values[6] = Int64GetDatum(summary.classification_count);
    if (root_hash_out != NULL) {
        values[7] = PointerGetDatum(root_hash_out);
        values[8] = Int32GetDatum(top_entity_type_id);
    } else {
        values[7] = (Datum) 0;
        values[8] = (Datum) 0;
        nulls[7]  = true;
        nulls[8]  = true;
    }

    HeapTuple tup = heap_form_tuple(tupdesc, values, nulls);
    PG_RETURN_DATUM(HeapTupleGetDatum(tup));
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
    ArrayType* utf8s_arr = PG_GETARG_ARRAYTYPE_P(0);
    ArrayType* types_arr = PG_GETARG_ARRAYTYPE_P(1);
    ArrayType* mus_arr   = PG_GETARG_ARRAYTYPE_P(2);
    ArrayType* provs_arr = PG_GETARG_ARRAYTYPE_P(3);
    /* p_model_source_ids (5th param, OPTIONAL int[]). NULL or omitted →
     * no model-source linkage. Per-row NULL elements skip linkage for
     * that row only. */
    bool       has_model_sources = !PG_ARGISNULL(4);
    ArrayType* msrc_arr = has_model_sources ? PG_GETARG_ARRAYTYPE_P(4) : NULL;

    int n = ArrayGetNItems(ARR_NDIM(utf8s_arr), ARR_DIMS(utf8s_arr));
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
    Datum* utf8_d; bool* utf8_n;
    Datum* type_d; bool* type_n;
    Datum* mu_d;   bool* mu_n;
    Datum* prov_d; bool* prov_n;
    Datum* msrc_d = NULL; bool* msrc_n = NULL;
    int dummy;
    deconstruct_array(utf8s_arr, BYTEAOID, -1, false, TYPALIGN_INT, &utf8_d, &utf8_n, &dummy);
    deconstruct_array(types_arr, TEXTOID, -1, false, TYPALIGN_INT, &type_d, &type_n, &dummy);
    deconstruct_array(mus_arr,   FLOAT8OID, sizeof(float8), FLOAT8PASSBYVAL, TYPALIGN_DOUBLE, &mu_d, &mu_n, &dummy);
    deconstruct_array(provs_arr, TEXTOID, -1, false, TYPALIGN_INT, &prov_d, &prov_n, &dummy);
    if (has_model_sources) {
        deconstruct_array(msrc_arr, INT4OID, sizeof(int32), true, TYPALIGN_INT, &msrc_d, &msrc_n, &dummy);
    }

    TextDecomposeSummary total;
    memset(&total, 0, sizeof(total));

    for (int i = 0; i < n; i++) {
        if (utf8_n[i] || type_n[i] || mu_n[i] || prov_n[i]) continue;
        LOCAL_FCINFO(inner, 5);
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
        Datum result = pg_text_decompose(inner);
        if (!inner->isnull) {
            HeapTupleHeader hth = DatumGetHeapTupleHeader(result);
            HeapTupleData htd;
            htd.t_len = HeapTupleHeaderGetDatumLength(hth);
            ItemPointerSetInvalid(&(htd.t_self));
            htd.t_tableOid = InvalidOid;
            htd.t_data = hth;
            bool isnull;
            total.entity_count        += DatumGetInt64(GetAttributeByNum(hth, 1, &isnull));
            total.edge_count          += DatumGetInt64(GetAttributeByNum(hth, 2, &isnull));
            total.edge_member_count   += DatumGetInt64(GetAttributeByNum(hth, 3, &isnull));
            total.physicality_count   += DatumGetInt64(GetAttributeByNum(hth, 4, &isnull));
            total.sequence_count      += DatumGetInt64(GetAttributeByNum(hth, 5, &isnull));
            total.significance_count  += DatumGetInt64(GetAttributeByNum(hth, 6, &isnull));
            total.classification_count += DatumGetInt64(GetAttributeByNum(hth, 7, &isnull));
        }
    }

    TupleDesc tupdesc;
    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE) {
        ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                        errmsg("pg_text_decompose_batch: composite type expected")));
    }
    BlessTupleDesc(tupdesc);

    /* 9-field summary. Batch never returns a single root — root_hash and
     * root_entity_type_id are always NULL. Callers that need per-row
     * roots should iterate text_decompose() one row at a time. */
    Datum values[9];
    bool  nulls[9] = { false,false,false,false,false,false,false,true,true };
    values[0] = Int64GetDatum(total.entity_count);
    values[1] = Int64GetDatum(total.edge_count);
    values[2] = Int64GetDatum(total.edge_member_count);
    values[3] = Int64GetDatum(total.physicality_count);
    values[4] = Int64GetDatum(total.sequence_count);
    values[5] = Int64GetDatum(total.significance_count);
    values[6] = Int64GetDatum(total.classification_count);
    values[7] = (Datum) 0;
    values[8] = (Datum) 0;
    HeapTuple tup = heap_form_tuple(tupdesc, values, nulls);
    PG_RETURN_DATUM(HeapTupleGetDatum(tup));
}
