/*
 * pg_ucd_atoms_blob.c — runtime loader for the per-block Hartonomous UCD
 * atom layout. Reads the index file at extension init, lazily mmaps each
 * block file on first access to that block's codepoint range, and exposes
 * accessor functions (huc_cp_hash_at / huc_cp_centroid_at / huc_cp_hilbert
 * / uc_cp_from_hash) over the blob.
 *
 * Layout reference: scripts/build/generate_unicode_tables.py
 *
 *   blob/hartonomous-ucd-17.0.0.idx          range table + tier1 + filenames
 *   blob/hartonomous-ucd-17.0.0.reverse.bin  global hash→cp reverse (40 MB)
 *   blob/blocks/<startHex>-<name>.bin        per-block hash+centroid+hilbert
 *
 * Microsecond access: a backend that touches CJK loads one ~1.5 MB block
 * file (kernel paged on demand) and indexes into it via O(log 397)
 * binary search to find the block + O(1) offset within. Backends that
 * never touch CJK never page in those bytes.
 */
#include "postgres.h"
#include "miscadmin.h"
#include "utils/elog.h"
#include "utils/memutils.h"

#include "generated/pg_ucd_atoms_blob.h"
#include "generated/pg_unicode_version.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <sys/types.h>
#include <sys/stat.h>

#ifdef _WIN32
  #include <windows.h>
#else
  #include <fcntl.h>
  #include <unistd.h>
  #include <sys/mman.h>
#endif

/* ── Constants matching the generator ──────────────────────────────── */
#define BLK_MAGIC 0x4B4C4248u  /* 'HBLK' LE */
#define IDX_MAGIC 0x58444348u  /* 'HCDX' LE */
#define REV_MAGIC 0x56455248u  /* 'HREV' LE */
#define EXPECTED_VERSION 0x00170000u

/* ── Cross-platform mmap ───────────────────────────────────────────── */
typedef struct {
    const void* base;
    size_t      size;
#ifdef _WIN32
    HANDLE      file;
    HANDLE      mapping;
#else
    int         fd;
#endif
} HucMap;

static int huc_mmap(const char* path, HucMap* out)
{
    memset(out, 0, sizeof(*out));
#ifdef _WIN32
    HANDLE f = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, NULL,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return -1;
    LARGE_INTEGER li;
    if (!GetFileSizeEx(f, &li)) { CloseHandle(f); return -1; }
    if (li.QuadPart == 0) { CloseHandle(f); return -1; }
    HANDLE m = CreateFileMappingA(f, NULL, PAGE_READONLY, 0, 0, NULL);
    if (!m) { CloseHandle(f); return -1; }
    void* ptr = MapViewOfFile(m, FILE_MAP_READ, 0, 0, (size_t) li.QuadPart);
    if (!ptr) { CloseHandle(m); CloseHandle(f); return -1; }
    out->base = ptr;
    out->size = (size_t) li.QuadPart;
    out->file = f;
    out->mapping = m;
    return 0;
#else
    int fd = open(path, O_RDONLY);
    if (fd < 0) return -1;
    struct stat st;
    if (fstat(fd, &st) < 0 || st.st_size == 0) { close(fd); return -1; }
    void* ptr = mmap(NULL, (size_t) st.st_size, PROT_READ, MAP_PRIVATE, fd, 0);
    if (ptr == MAP_FAILED) { close(fd); return -1; }
    out->base = ptr;
    out->size = (size_t) st.st_size;
    out->fd = fd;
    return 0;
#endif
}

static void huc_munmap(HucMap* m)
{
    if (!m || !m->base) return;
#ifdef _WIN32
    UnmapViewOfFile(m->base);
    if (m->mapping) CloseHandle(m->mapping);
    if (m->file)    CloseHandle(m->file);
#else
    munmap((void*) m->base, m->size);
    if (m->fd > 0) close(m->fd);
#endif
    memset(m, 0, sizeof(*m));
}

/* ── Per-block range table ─────────────────────────────────────────── */
typedef struct {
    int32_t   range_start;
    int32_t   range_end;
    int32_t   atom_count;
    char      filename[128];
    HucMap    map;        /* lazy: only set after first touch */
    /* Cached pointers into map.base after parsing block header. */
    const uint8_t*  hashes;   /* atom_count × 32 bytes */
    const uint8_t*  centroids;/* atom_count × 32 bytes (4 doubles) */
    const uint8_t*  hilberts; /* atom_count × 8 bytes */
} BlockEntry;

static char       g_blob_dir[512] = {0};
static BlockEntry* g_blocks = NULL;
static int32_t    g_block_count = 0;
static HucMap     g_reverse_map = {0};
static const uint8_t* g_reverse_entries = NULL;
static uint32_t   g_reverse_count = 0;

/* Public mirror of the loader state — only set after a successful load. */
uint32_t uc_cp_reverse_count = 0;

/* These four pointers stay NULL with the per-block layout — callers must
 * use the accessor functions below. We expose the symbols so the existing
 * pg_codepoint_atoms_pg.c keeps linking; callsites get refactored to use
 * huc_cp_*_at(). */
const uint8_t*  uc_cp_hash          = NULL;
const double*   uc_cp_centroid      = NULL;
const uint64_t* uc_cp_hilbert       = NULL;
const uint8_t*  uc_cp_hash_to_value = NULL;

/* ── Index parser ──────────────────────────────────────────────────── */
static int parse_index(const char* path)
{
    HucMap idx;
    if (huc_mmap(path, &idx) != 0) {
        ereport(WARNING,
                (errcode_for_file_access(),
                 errmsg("hartonomous: failed to mmap UCD index %s", path)));
        return -1;
    }
    if (idx.size < 24) { huc_munmap(&idx); return -1; }
    const uint8_t* p = (const uint8_t*) idx.base;
    uint32_t magic, version, block_count, tier1_count, rev_off, reserved;
    memcpy(&magic,       p +  0, 4);
    memcpy(&version,     p +  4, 4);
    memcpy(&block_count, p +  8, 4);
    memcpy(&tier1_count, p + 12, 4);
    memcpy(&rev_off,     p + 16, 4);
    memcpy(&reserved,    p + 20, 4);
    (void) reserved;
    if (magic != IDX_MAGIC || version != EXPECTED_VERSION) {
        ereport(WARNING,
                (errmsg("hartonomous: UCD index magic/version mismatch (got 0x%08x v0x%08x)",
                        magic, version)));
        huc_munmap(&idx);
        return -1;
    }
    if (block_count == 0 || block_count > 4096) { huc_munmap(&idx); return -1; }

    size_t blocks_off = 24;
    size_t tier1_off = blocks_off + (size_t) block_count * 32;
    size_t string_off = tier1_off + (size_t) tier1_count * 8;
    if (string_off + rev_off >= idx.size) { huc_munmap(&idx); return -1; }
    const char* string_table = (const char*) p + string_off;

    BlockEntry* blocks = (BlockEntry*) MemoryContextAllocZero(
        TopMemoryContext, sizeof(BlockEntry) * block_count);
    for (uint32_t i = 0; i < block_count; ++i) {
        const uint8_t* row = p + blocks_off + (size_t) i * 32;
        memcpy(&blocks[i].range_start, row +  0, 4);
        memcpy(&blocks[i].range_end,   row +  4, 4);
        memcpy(&blocks[i].atom_count,  row +  8, 4);
        uint32_t name_off;
        memcpy(&name_off, row + 12, 4);
        snprintf(blocks[i].filename, sizeof(blocks[i].filename),
                 "%s", string_table + name_off);
    }
    g_blocks = blocks;
    g_block_count = (int32_t) block_count;
    huc_munmap(&idx);
    return 0;
}

/* Binary-search the block table for the entry containing cp. */
static BlockEntry* find_block(int32_t cp)
{
    if (cp < 0 || cp >= 0x110000) return NULL;
    if (!g_blocks) return NULL;
    int32_t lo = 0, hi = g_block_count - 1;
    while (lo <= hi) {
        int32_t mid = (lo + hi) >> 1;
        BlockEntry* b = &g_blocks[mid];
        if (cp < b->range_start)      hi = mid - 1;
        else if (cp > b->range_end)   lo = mid + 1;
        else                          return b;
    }
    return NULL;
}

/* Lazily mmap the block's file on first access; parse and cache pointers. */
static int ensure_block_mapped(BlockEntry* b)
{
    if (b->hashes != NULL) return 0;  /* already mapped */
    char path[1024];
    snprintf(path, sizeof(path), "%s%s%s",
             g_blob_dir,
#ifdef _WIN32
             "\\",
#else
             "/",
#endif
             b->filename);
    if (huc_mmap(path, &b->map) != 0) {
        ereport(DEBUG1,
                (errmsg("hartonomous: block file unavailable: %s (skipping)", path)));
        return -1;
    }
    if (b->map.size < 24 + 32) { huc_munmap(&b->map); return -1; }
    const uint8_t* p = (const uint8_t*) b->map.base;
    uint32_t magic, version, rs, re_, n;
    memcpy(&magic,    p +  0, 4);
    memcpy(&version,  p +  4, 4);
    memcpy(&rs,       p +  8, 4);
    memcpy(&re_,      p + 12, 4);
    memcpy(&n,        p + 16, 4);
    if (magic != BLK_MAGIC || version != EXPECTED_VERSION
        || (int32_t) rs != b->range_start || (int32_t) re_ != b->range_end
        || (int32_t) n  != b->atom_count) {
        ereport(WARNING,
                (errmsg("hartonomous: block file header mismatch %s", b->filename)));
        huc_munmap(&b->map);
        return -1;
    }
    /* Header is 24 bytes; data follows: hashes (n×32) + centroids (n×32) + hilberts (n×8). */
    b->hashes    = p + 24;
    b->centroids = p + 24 + (size_t) n * 32;
    b->hilberts  = p + 24 + (size_t) n * 64;
    return 0;
}

static int parse_reverse(const char* path)
{
    if (huc_mmap(path, &g_reverse_map) != 0) {
        ereport(WARNING,
                (errcode_for_file_access(),
                 errmsg("hartonomous: failed to mmap UCD reverse table %s", path)));
        return -1;
    }
    if (g_reverse_map.size < 16 + 32) {
        huc_munmap(&g_reverse_map);
        return -1;
    }
    const uint8_t* p = (const uint8_t*) g_reverse_map.base;
    uint32_t magic, version, n;
    memcpy(&magic,   p +  0, 4);
    memcpy(&version, p +  4, 4);
    memcpy(&n,       p +  8, 4);
    if (magic != REV_MAGIC || version != EXPECTED_VERSION) {
        ereport(WARNING,
                (errmsg("hartonomous: UCD reverse magic/version mismatch")));
        huc_munmap(&g_reverse_map);
        return -1;
    }
    g_reverse_entries = p + 16;
    g_reverse_count = n;
    uc_cp_reverse_count = n;
    return 0;
}

/* ── Public loader ─────────────────────────────────────────────────── */
int huc_load_atoms_blob(const char* dir)
{
    if (g_blocks != NULL) return 0;  /* already loaded */
    snprintf(g_blob_dir, sizeof(g_blob_dir), "%s", dir ? dir : "");
    char idx_path[1024], rev_path[1024];
    snprintf(idx_path, sizeof(idx_path), "%s%shartonomous-ucd-17.0.0.idx",
             g_blob_dir,
#ifdef _WIN32
             "\\"
#else
             "/"
#endif
        );
    snprintf(rev_path, sizeof(rev_path), "%s%shartonomous-ucd-17.0.0.reverse.bin",
             g_blob_dir,
#ifdef _WIN32
             "\\"
#else
             "/"
#endif
        );
    if (parse_index(idx_path)   != 0) return -1;
    if (parse_reverse(rev_path) != 0) return -1;
    ereport(LOG,
            (errmsg("hartonomous: UCD atoms blob loaded (%d blocks, %u reverse entries)",
                    g_block_count, g_reverse_count)));
    return 0;
}

void huc_unload_atoms_blob(void)
{
    if (g_blocks) {
        for (int32_t i = 0; i < g_block_count; ++i) {
            if (g_blocks[i].map.base) huc_munmap(&g_blocks[i].map);
        }
        pfree(g_blocks);
        g_blocks = NULL;
        g_block_count = 0;
    }
    if (g_reverse_map.base) huc_munmap(&g_reverse_map);
    g_reverse_entries = NULL;
    g_reverse_count = 0;
    uc_cp_reverse_count = 0;
}

/* ── Public accessors (microsecond hot path) ───────────────────────── */
const uint8_t* huc_cp_hash_at(int32_t cp)
{
    BlockEntry* b = find_block(cp);
    if (!b || ensure_block_mapped(b) != 0) return NULL;
    return b->hashes + (size_t) (cp - b->range_start) * 32;
}

const double* huc_cp_centroid_at(int32_t cp)
{
    BlockEntry* b = find_block(cp);
    if (!b || ensure_block_mapped(b) != 0) return NULL;
    return (const double*) (b->centroids + (size_t) (cp - b->range_start) * 32);
}

uint64_t huc_cp_hilbert_at(int32_t cp)
{
    BlockEntry* b = find_block(cp);
    if (!b || ensure_block_mapped(b) != 0) return 0;
    uint64_t v;
    memcpy(&v, b->hilberts + (size_t) (cp - b->range_start) * 8, 8);
    return v;
}

int32_t uc_cp_from_hash(const uint8_t* hash32)
{
    if (!g_reverse_entries || g_reverse_count == 0) return -1;
    int32_t lo = 0, hi = (int32_t) g_reverse_count - 1;
    while (lo <= hi) {
        int32_t mid = (lo + hi) >> 1;
        const uint8_t* row = g_reverse_entries + (size_t) mid * 36;
        int cmp = memcmp(hash32, row, 32);
        if (cmp < 0) hi = mid - 1;
        else if (cmp > 0) lo = mid + 1;
        else {
            uint32_t cp;
            memcpy(&cp, row + 32, 4);
            return (int32_t) cp;
        }
    }
    return -1;
}
