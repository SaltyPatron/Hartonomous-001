/* libhartonomous — text_decompose.h
 *
 * In-process UTF-8 → substrate-DAG decomposition. UAX #29 + BLAKE3 +
 * S^3 centroid against the embedded UCD tables. Replaces the per-text
 * round-trip to substrate.text_decompose with a callback-driven walk.
 *
 * Determinism: Law #6. Same UTF-8 input + same UCD blob = byte-identical
 * hash output.
 *
 * Atom blob layout under `dir`:
 *   hartonomous-ucd-<ver>.idx
 *   hartonomous-ucd-<ver>.reverse.bin
 *   blocks/<startHex>-<name>.bin
 */

#ifndef HARTONOMOUS_TEXT_DECOMPOSE_H
#define HARTONOMOUS_TEXT_DECOMPOSE_H

#include <stddef.h>
#include <stdint.h>

#include "hartonomous/version.h"
#include "hartonomous/hash.h"

#ifdef __cplusplus
extern "C" {
#endif

/* ── Record kinds ──────────────────────────────────────────── */
#define HARTONOMOUS_REC_ENTITY        1
#define HARTONOMOUS_REC_CLASSIFICATION 2
#define HARTONOMOUS_REC_PHYSICALITY   3
#define HARTONOMOUS_REC_SEQUENCE      4
#define HARTONOMOUS_REC_SIGNIFICANCE  5

/* ── Entity-kind tags ──────────────────────────────────────── */
#define HARTONOMOUS_KIND_CODEPOINT         1
#define HARTONOMOUS_KIND_GRAPHEME_CLUSTER  2
#define HARTONOMOUS_KIND_WORD_FORM         3
#define HARTONOMOUS_KIND_TEXT_COMPOSITION  9

/* ── Physicality-kind tags ─────────────────────────────────── */
#define HARTONOMOUS_PHYS_S3_POSITION  1  /* entity-tier atom POINTZM */
#define HARTONOMOUS_PHYS_CONTOUR      2  /* entity-tier composition LINESTRINGZM (grapheme_cluster, word_form) */
#define HARTONOMOUS_PHYS_CONTENT      3  /* content-tier trajectory LINESTRINGZM (text_composition, paragraph, document) */

/* ── Significance-kind tags ────────────────────────────────── */
#define HARTONOMOUS_SIG_SOURCE_AUTHORITY  1

typedef struct hartonomous_text_record {
    int             kind;
    int             subkind;
    const uint8_t*  hash_a;
    const uint8_t*  hash_b;
    int             int_param;
    double          double_param;
    const uint8_t*  geometry;
    size_t          geometry_len;
    double          centroid[4];
} hartonomous_text_record_t;

typedef int (*hartonomous_text_emit_cb)(
    void* ctx,
    const hartonomous_text_record_t* rec
);

/* ── UCD blob load/unload ──────────────────────────────────── */

HARTONOMOUS_API int  hartonomous_ucd_load(const char* dir);
HARTONOMOUS_API void hartonomous_ucd_unload(void);
HARTONOMOUS_API int  hartonomous_ucd_loaded_state(void);
HARTONOMOUS_API int  hartonomous_ucd_catalog_ready(void);
HARTONOMOUS_API int  hartonomous_ucd_tables_ready(void);

/* ── Per-codepoint atom accessors ──────────────────────────── */

HARTONOMOUS_API int hartonomous_ucd_cp_centroid(int32_t cp, double out[4]);
HARTONOMOUS_API int hartonomous_ucd_cp_hash(int32_t cp, uint8_t out[32]);
HARTONOMOUS_API int hartonomous_ucd_cp_hilbert(int32_t cp, uint64_t* out);
HARTONOMOUS_API int32_t hartonomous_ucd_cp_from_hash(const uint8_t hash32[32]);

/* ── Per-codepoint property accessors ──────────────────────── */

/* UAX-#29 / UAX-#14 break properties. Returns 0 (Other) for any
 * out-of-range codepoint. Values are stable byte codes matching the
 * substrate's break_property reference table for the corresponding
 * category. */
HARTONOMOUS_API uint8_t hartonomous_ucd_cp_gcb(int32_t cp);
HARTONOMOUS_API uint8_t hartonomous_ucd_cp_wb(int32_t cp);
HARTONOMOUS_API uint8_t hartonomous_ucd_cp_sb(int32_t cp);
HARTONOMOUS_API uint8_t hartonomous_ucd_cp_lb(int32_t cp);
HARTONOMOUS_API uint8_t hartonomous_ucd_cp_incb(int32_t cp);

HARTONOMOUS_API int hartonomous_ucd_cp_extended_pictographic(int32_t cp);

/* Simple (single-codepoint) case mappings. Return the codepoint itself if
 * the table has no entry. */
HARTONOMOUS_API int32_t hartonomous_ucd_cp_simple_case_fold(int32_t cp);
HARTONOMOUS_API int32_t hartonomous_ucd_cp_simple_lowercase(int32_t cp);
HARTONOMOUS_API int32_t hartonomous_ucd_cp_simple_uppercase(int32_t cp);
HARTONOMOUS_API int32_t hartonomous_ucd_cp_simple_titlecase(int32_t cp);

/* Full (possibly multi-codepoint) case fold. Writes the expansion into
 * out[0..return-1] in canonical UCD order. Returns the codepoint count on
 * success (>= 1); returns -1 on null/oversize/out-of-range. Callers should
 * size the buffer for at least 4 entries to cover the worst case in
 * Unicode 17.0. */
HARTONOMOUS_API int hartonomous_ucd_cp_full_case_fold(
    int32_t cp,
    int32_t* out,
    int out_max);

/* UCA primary-weight collation rank (the same UCA index the S^3
 * Super-Fibonacci projection orders by). */
HARTONOMOUS_API int32_t hartonomous_ucd_cp_uca_index(int32_t cp);

/* UAX #44 classification properties. All values are stable byte/uint16
 * codes matching the substrate's reference-vocabulary id enumerations
 * baked at build time. Out-of-range codepoints return the property's
 * "Other" / "None" / "Unknown" sentinel. */
HARTONOMOUS_API uint8_t  hartonomous_ucd_cp_gc(int32_t cp);
HARTONOMOUS_API uint8_t  hartonomous_ucd_cp_ccc(int32_t cp);
HARTONOMOUS_API uint16_t hartonomous_ucd_cp_script(int32_t cp);
HARTONOMOUS_API uint16_t hartonomous_ucd_cp_block(int32_t cp);
HARTONOMOUS_API uint8_t  hartonomous_ucd_cp_bidi(int32_t cp);
HARTONOMOUS_API uint8_t  hartonomous_ucd_cp_eaw(int32_t cp);
HARTONOMOUS_API uint8_t  hartonomous_ucd_cp_hsy(int32_t cp);
HARTONOMOUS_API uint8_t  hartonomous_ucd_cp_num_type(int32_t cp);

/* UAX #44 decomposition. decomp_type returns the UC_DECOMP_TYPE_* enum
 * code (0 = None, 1 = Canonical, 2..17 = Compat variants per UAX #44
 * §5.7.3). decomp_mapping copies the decomposed codepoint sequence into
 * `out` (caller-allocated; size at least uc_decomp_len[cp]). Returns
 * codepoint count on success (>= 0; 0 = no decomposition); -1 on null/
 * out-of-range/insufficient-buffer. Hangul algorithmic decomposition is
 * NOT expanded; callers handle Hangul L/V/T separately per UAX #15 §3. */
HARTONOMOUS_API uint8_t hartonomous_ucd_cp_decomp_type(int32_t cp);
HARTONOMOUS_API int hartonomous_ucd_cp_decomp_mapping(
    int32_t cp,
    int32_t* out,
    int out_max);

/* ── Decomposition ─────────────────────────────────────────── */

HARTONOMOUS_API int hartonomous_text_decompose(
    const uint8_t* utf8,
    size_t utf8_len,
    int top_kind,
    double trust_mu,
    hartonomous_text_emit_cb emit,
    void* ctx,
    uint8_t out_root_hash[HARTONOMOUS_HASH_LEN],
    int* out_root_kind,
    double out_root_centroid[4]
);

/* ── Boundary extraction (lightweight, no record emission) ─────
 *
 * Run UAX-29 / UAX-14 segmentation on UTF-8 input. Return per-segment
 * START codepoint indices in `out_indices`. Final index `count` is the
 * codepoint count (one past the last segment start) — same convention as
 * the UCD test files.
 *
 * Caller-owned buffer; size required ≤ codepoint_count + 1. Pass
 * `out_capacity` to bound writes; the function returns the *required*
 * count even if the buffer is too small (callers can retry with a larger
 * buffer). Returns 0 on success, -1 on null arg, -2 on UCD not loaded.
 *
 * These functions are the substrate's SINGLE source of UAX-29 segmentation
 * truth. Per Law #6 / compute-facade rule, C# callers must P/Invoke into
 * these rather than reimplementing the state machine. */

HARTONOMOUS_API int hartonomous_text_grapheme_boundaries(
    const uint8_t* utf8,
    size_t utf8_len,
    int32_t* out_indices,
    int out_capacity,
    int* out_count
);

HARTONOMOUS_API int hartonomous_text_word_boundaries(
    const uint8_t* utf8,
    size_t utf8_len,
    int32_t* out_indices,
    int out_capacity,
    int* out_count
);

HARTONOMOUS_API int hartonomous_text_sentence_boundaries(
    const uint8_t* utf8,
    size_t utf8_len,
    int32_t* out_indices,
    int out_capacity,
    int* out_count
);

/* Codepoint count for a UTF-8 buffer (NFC-normalized internally). Useful
 * for sizing boundary buffers. Returns -1 on null arg, -2 on UCD not loaded. */
HARTONOMOUS_API int hartonomous_text_codepoint_count(
    const uint8_t* utf8,
    size_t utf8_len,
    int* out_count
);

#ifdef __cplusplus
}
#endif

#endif /* HARTONOMOUS_TEXT_DECOMPOSE_H */
