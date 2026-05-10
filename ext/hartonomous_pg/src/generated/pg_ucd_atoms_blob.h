/* GENERATED — per-block math-derived atom layout.
 *
 * The blob is split across one file per Unicode block (Blocks.txt
 * range or synthesized 'Reserved_NNNN_MMMM' gap). A backend that
 * touches CJK loads ~1.5 MB; ASCII-only loads ~9 KB. mmap is lazy
 * at OS page level; backends that never query a block never
 * page in those bytes. */
#ifndef PG_UCD_ATOMS_BLOB_H
#define PG_UCD_ATOMS_BLOB_H
#include <stdint.h>
#include "pg_unicode_version.h"

#define CP_HASH_LEN 32
#define UC_CP_REVERSE_ENTRY_SIZE 36

/* Loader: dir contains hartonomous-ucd-17.0.0.idx, .reverse.bin, blocks/. */
int  huc_load_atoms_blob(const char* dir);
void huc_unload_atoms_blob(void);

/* O(log B) block lookup + O(1) within-block index = microsecond hot path.
 * Returns NULL when the relevant block file is unavailable (allowed
 * for embedded subset deployments). */
const uint8_t* huc_cp_hash_at    (int32_t cp);
const double*  huc_cp_centroid_at(int32_t cp);  /* 4 doubles */
uint64_t       huc_cp_hilbert_at (int32_t cp);  /* 0 if unmapped */

/* O(log N_total) reverse over the global sorted hash→cp table. */
int32_t uc_cp_from_hash(const uint8_t* hash32);

/* Compatibility stubs — kept NULL-initialized so existing code that
 * declares them as extern const pointers keeps linking. New code should
 * use the huc_cp_*_at() accessors. */
extern const uint8_t*  uc_cp_hash;
extern const double*   uc_cp_centroid;
extern const uint64_t* uc_cp_hilbert;
extern const uint8_t*  uc_cp_hash_to_value;
extern uint32_t        uc_cp_reverse_count;
#endif
