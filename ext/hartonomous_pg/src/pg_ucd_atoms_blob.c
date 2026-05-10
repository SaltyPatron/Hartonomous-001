/*
 * pg_ucd_atoms_blob.c — thin delegation layer to libhartonomous's UCD blob
 * loader.
 *
 * Previously this file held a SECOND, independent implementation of the
 * UCD blob loader (its own g_blocks, ensure_block_mapped, parse_index,
 * etc.) parallel to the one in libhartonomous.so. That duplication meant
 * two copies of the loaded blob lived in every backend's memory: one
 * populated by huc_load_atoms_blob (called from _PG_init), one populated
 * by hartonomous_ucd_load (called from text_decompose's lazy path). Both
 * had their own state, both had their own bug surfaces, and the lazy/eager
 * load asymmetry meant subtle behavioral drift between the two.
 *
 * This file now retains the same C ABI (huc_load_atoms_blob,
 * huc_unload_atoms_blob, huc_cp_hash_at, huc_cp_centroid_at,
 * huc_cp_hilbert_at, huc_cp_from_hash) so existing PG-extension callers
 * (pg_codepoint_atoms_pg.c, pg_text_decompose.c, hartonomous.c _PG_init)
 * compile and link unchanged. Each function delegates to the corresponding
 * exported libhartonomous API. The blob is loaded into ONE g_blocks
 * (libhartonomous's), used by every consumer.
 *
 * The pointer-returning accessors (huc_cp_hash_at, huc_cp_centroid_at)
 * use thread-local static buffers because the libhartonomous exported APIs
 * are copy-out. Callers in the PG extension always immediately memcpy from
 * the returned pointer, so the buffer's "valid until next call" lifetime
 * matches caller expectations.
 */
#include "postgres.h"

#include "generated/pg_ucd_atoms_blob.h"
#include "hartonomous.h"

#include <stdint.h>
#include <string.h>

/* Dead variable retained for header compatibility; nothing reads it now
 * (uc_cp_reverse_count was set by the prior parse_reverse but never
 * consulted). Kept as a tombstone. */
uint32_t uc_cp_reverse_count = 0;

/* ── Public C ABI: backwards-compatible wrappers ──────────────────────── */

int huc_load_atoms_blob(const char* dir)
{
    return hartonomous_ucd_load(dir);
}

void huc_unload_atoms_blob(void)
{
    hartonomous_ucd_unload();
}

const uint8_t* huc_cp_hash_at(int32_t cp)
{
    static __thread uint8_t buf[32];
    if (hartonomous_ucd_cp_hash(cp, buf) != 0) { return NULL; }
    return buf;
}

const double* huc_cp_centroid_at(int32_t cp)
{
    static __thread double buf[4];
    if (hartonomous_ucd_cp_centroid(cp, buf) != 0) { return NULL; }
    return buf;
}

uint64_t huc_cp_hilbert_at(int32_t cp)
{
    uint64_t v = 0;
    if (hartonomous_ucd_cp_hilbert(cp, &v) != 0) { return 0; }
    return v;
}

int32_t huc_cp_from_hash(const uint8_t* hash32)
{
    if (!hash32) { return -1; }
    return hartonomous_ucd_cp_from_hash(hash32);
}

/* Header (generated/pg_ucd_atoms_blob.h:28) declares the prefix-less
 * variant; pg_codepoint_atoms_pg.c calls it from pg_cp_from_hash and from
 * the with_*_predicate SRFs. Same delegation. */
int32_t uc_cp_from_hash(const uint8_t* hash32)
{
    return huc_cp_from_hash(hash32);
}
