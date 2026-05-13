/*
 * pg_text_decompose.h — native text decomposition for the hartonomous PG extension.
 *
 * Architecture (Tier 1 — the only place this logic lives):
 *
 *   substrate.text_decompose(p_utf8 bytea, p_top_entity_type_code text,
 *                            p_trust_mu double precision, p_provenance_code text)
 *     RETURNS substrate.text_decompose_summary
 *
 *   Inside, in compiled C:
 *     1. UTF-8 decode → codepoint stream (utf8_decode.c)
 *     2. Codepoint property lookup via generated embedded UCD tables
 *     3. UAX #29 grapheme cluster boundary detection (table-driven)
 *     4. UAX #29 word boundary detection (same)
 *     5. BLAKE3 chain hashing per codepoint, grapheme, word_form, composition
 *        (uses libhartonomous's batched primitives — Blake3Many)
 *     6. 4D centroid math: per-codepoint via super_fibonacci_4d (S^3 anchor),
 *        per-composition via mean_4d aggregate of constituents
 *     7. SPI bulk INSERTs directly into substrate.entity,
 *        substrate.entity_classification, substrate.physicality,
 *        substrate.physicality composition metadata and substrate.entity_significance with
 *        ON CONFLICT DO NOTHING. No staging surface and no round-trip back to .NET.
 *
 *   Returns a summary record (counts) so the caller can report progress.
 *
 *   OpenMP fan-out: when called via the batch variant (text_decompose_batch),
 *   multiple texts are processed concurrently across CPU cores via
 *   #pragma omp parallel for. Each worker has its own SPI snapshot — staging
 *   inserts use bulk binary-encoded buffers stitched together at flush time.
 *
 * Determinism (Law #6):
 *   - Property lookups come from generated UCD 17.0.0 tables.
 *   - UAX #29 boundary rules are table-driven from the same generated source
 *     inventory used by the native in-process decomposer.
 *   - Determinism gate: a separate test (test_text_decompose_determinism.cc)
 *     asserts byte-identical hash output for a corpus of UCD-supplied
 *     boundary tests + Wiktionary fixture entries vs the C# reference, before
 *     this function replaces CanonicalTextDecomposer.Emit on the hot path.
 */

#ifndef PG_TEXT_DECOMPOSE_H
#define PG_TEXT_DECOMPOSE_H

#include "postgres.h"
#include "fmgr.h"
#include <stdint.h>

/* ── UAX #29 Grapheme_Cluster_Break property (mirrors C# GraphemeBreak) ── */
typedef enum {
    GCB_Other = 0,
    GCB_CR,
    GCB_LF,
    GCB_Control,
    GCB_Extend,
    GCB_ZWJ,
    GCB_RegionalIndicator,
    GCB_Prepend,
    GCB_SpacingMark,
    GCB_L,
    GCB_V,
    GCB_T,
    GCB_LV,
    GCB_LVT
} GraphemeBreak;

/* ── UAX #29 Word_Break property (mirrors C# WordBreak) ── */
typedef enum {
    WB_Other = 0,
    WB_CR,
    WB_LF,
    WB_Newline,
    WB_Extend,
    WB_ZWJ,
    WB_Format,
    WB_Katakana,
    WB_HebrewLetter,
    WB_ALetter,
    WB_SingleQuote,
    WB_DoubleQuote,
    WB_MidNumLet,
    WB_MidLetter,
    WB_MidNum,
    WB_Numeric,
    WB_ExtendNumLet,
    WB_RegionalIndicator,
    WB_WSegSpace,
    WB_Extended_Pictographic
} WordBreak;

/* Per-codepoint properties pulled from substrate.codepoint_property at first
 * call and cached for the life of the backend. Compact — 8 bytes per row. */
typedef struct {
    uint8_t  gcb;                 /* GraphemeBreak */
    uint8_t  wb;                  /* WordBreak */
    uint8_t  is_extended_picto;   /* 0 / 1 */
    uint8_t  reserved;            /* alignment padding */
    int32_t  block_id;            /* substrate.block.id (for downstream) */
} CodepointProps;

/* Per-text decomposition summary returned to caller. */
typedef struct {
    int64_t entity_count;
    int64_t edge_count;
    int64_t edge_member_count;
    int64_t physicality_count;
    int64_t composition_child_count;
    int64_t significance_count;
    int64_t classification_count;
} TextDecomposeSummary;

/* ── Public PG_FUNCTION_INFO_V1 entry points (declared in pg_text_decompose.c) ── */
extern PGDLLEXPORT Datum pg_text_decompose(PG_FUNCTION_ARGS);
extern PGDLLEXPORT Datum pg_text_decompose_batch(PG_FUNCTION_ARGS);

/* ── Per-backend codepoint property cache lifecycle ── */
void pg_text_decompose_cache_load(void);   /* Lazy: SPI-loads on first call. */
void pg_text_decompose_cache_clear(void);  /* For tests / on schema reload. */

/* Lookup helpers for UAX #29 logic. Return Other/0 for any codepoint not in
 * the cache (which shouldn't happen if substrate.codepoint_property is fully
 * populated by the UCD seed). */
GraphemeBreak gcb_for(int32_t codepoint);
WordBreak     wb_for(int32_t codepoint);
int           is_extended_pictographic(int32_t codepoint);

/* UTF-8 decode primitive — returns number of bytes consumed for one codepoint,
 * writes the decoded codepoint to *out. Returns 0 on invalid sequence. */
size_t utf8_decode_one(const uint8_t* p, size_t len, int32_t* out);

#endif /* PG_TEXT_DECOMPOSE_H */
