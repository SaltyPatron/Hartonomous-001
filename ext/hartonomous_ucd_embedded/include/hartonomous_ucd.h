/*
 * hartonomous_ucd.h — Embedded UCD/UCA atom lookup, no PostgreSQL deps.
 *
 * Public C API for consumers that need substrate-compatible per-codepoint
 * BLAKE3 hash, 4D Super-Fibonacci centroid, Hilbert code, and reverse
 * hash lookup, but cannot link against PostgreSQL.
 *
 * Wire format: identical to the per-block layout produced by
 * scripts/build/generate_unicode_tables.py — interoperable byte-for-byte
 * with the PostgreSQL extension's loader.
 *
 * Memory model:
 *   - Caller supplies blob directory path (containing .idx, .reverse.bin,
 *     and blocks/*.bin) at huc_init().
 *   - No malloc inside the library: per-block mmap'd pages are managed
 *     by the OS; lookup state is stored in caller-provided context.
 *   - Reentrant when each thread/process passes its own huc_ctx_t.
 *
 * Determinism guarantee (Law #6):
 *   Same UCD version (e.g. 17.0.0) → byte-identical hashes/centroids/
 *   hilbert codes across platforms, compilers, and architectures.
 *
 * Build profiles (CMake options):
 *   HUCD_TIER          BOTH | TIER1_ONLY     (default BOTH)
 *   HUCD_DECOMPRESS    NONE | ZSTD | LZ4     (default NONE)
 *   HUCD_INCLUDE_NAMES ON | OFF              (default ON)
 *   HUCD_INCLUDE_DECOMP ON | OFF             (default ON)
 *   HUCD_INCLUDE_UCA   ON | OFF              (default ON)
 *
 * Latency target: <1 µs for any cached query (block already mmap'd);
 * ~50 µs first-touch per block (open + mmap).
 */
#ifndef HARTONOMOUS_UCD_H
#define HARTONOMOUS_UCD_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ── Constants ────────────────────────────────────────────────────── */
#define HUC_VERSION_MAJOR 17
#define HUC_VERSION_MINOR 0
#define HUC_VERSION_PATCH 0
#define HUC_HASH_LEN      32
#define HUC_CENTROID_DIM   4

/* ── Tier discrimination ──────────────────────────────────────────── */
typedef enum {
    HUC_TIER_PRECOMPUTED = 1,   /* in tier-1 (modern script) range */
    HUC_TIER_AVAILABLE   = 2,   /* assigned, included in blob */
    HUC_TIER_UNAVAILABLE = 3,   /* unassigned (Cn) — substrate scaffolding only */
} huc_tier_t;

/* ── Opaque context (caller-allocated, library-internal layout) ───── */
typedef struct huc_ctx huc_ctx_t;

/* Heap-allocate an opaque context (caller frees via huc_dispose). */
huc_ctx_t* huc_create(void);
void       huc_dispose(huc_ctx_t* ctx);

/* Initialize from a blob directory containing
 *   hartonomous-ucd-17.0.0.idx
 *   hartonomous-ucd-17.0.0.reverse.bin
 *   blocks/<startHex>-<name>.bin
 * Returns 0 on success, negative errno on failure. */
int  huc_init(huc_ctx_t* ctx, const char* blob_dir);
void huc_shutdown(huc_ctx_t* ctx);

/* Tier query — O(log B) range search over the index. */
huc_tier_t huc_cp_tier(const huc_ctx_t* ctx, int32_t cp);

/* ── Atom accessors (microsecond hot path after first block load) ── */

/* Copy 32-byte BLAKE3 hash into out[32]. Returns 0 on success, -1 if
 * the codepoint is out of range or the relevant block file is absent
 * (allowed for embedded subset deployments — caller should handle). */
int huc_cp_hash(const huc_ctx_t* ctx, int32_t cp, uint8_t out[HUC_HASH_LEN]);

/* Copy 4 doubles (S^3 centroid) into out[4]. Returns 0 / -1. */
int huc_cp_centroid(const huc_ctx_t* ctx, int32_t cp, double out[HUC_CENTROID_DIM]);

/* Hilbert 4D encoded code. Returns the value, or 0 if unavailable.
 * Use huc_cp_tier first to disambiguate "0 means missing" from "0 valid". */
uint64_t huc_cp_hilbert(const huc_ctx_t* ctx, int32_t cp);

/* Reverse hash → codepoint over the global sorted table.
 * Returns the codepoint, or -1 if no match. */
int32_t huc_cp_from_hash(const huc_ctx_t* ctx, const uint8_t hash[HUC_HASH_LEN]);

/* ── Diagnostics + integrity ──────────────────────────────────────── */

/* Returns "17.0.0" or similar — the UCD version baked into the blob. */
const char* huc_ucd_version(const huc_ctx_t* ctx);

/* Check the BLAKE3 footer of every loaded file. 0 = all match;
 * -1 = at least one file has corrupt or mismatched footer.
 * Optional — not required for normal operation. */
int huc_verify_blob(const huc_ctx_t* ctx);

/* Number of blocks indexed (typically ~397 for full UCD 17.0.0). */
int  huc_block_count(const huc_ctx_t* ctx);

/* Number of reverse-table entries (typically 1,114,112 for full blob). */
uint32_t huc_reverse_count(const huc_ctx_t* ctx);

#ifdef __cplusplus
} /* extern "C" */
#endif
#endif /* HARTONOMOUS_UCD_H */
