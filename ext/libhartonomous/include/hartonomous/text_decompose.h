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
#define HARTONOMOUS_PHYS_S3_POSITION  1
#define HARTONOMOUS_PHYS_CONTOUR      2

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

#ifdef __cplusplus
}
#endif

#endif /* HARTONOMOUS_TEXT_DECOMPOSE_H */
