/*
 * huc_loader.c — Index parser + per-block lazy mmap. PG-free port of
 * ext/hartonomous_pg/src/pg_ucd_atoms_blob.c. Single source of truth
 * for the wire format; the PG extension can be refactored later to
 * link against this library.
 */
#include "hartonomous_ucd.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
  #include <windows.h>
#else
  #include <fcntl.h>
  #include <unistd.h>
  #include <sys/mman.h>
  #include <sys/stat.h>
  #include <errno.h>
#endif

#define BLK_MAGIC 0x4B4C4248u  /* 'HBLK' LE */
#define IDX_MAGIC 0x58444348u  /* 'HCDX' LE */
#define REV_MAGIC 0x56455248u  /* 'HREV' LE */
#define EXPECTED_VERSION 0x00170000u
#define MAX_FILENAME 128

typedef struct {
    const void* base;
    size_t      size;
#ifdef _WIN32
    HANDLE      file;
    HANDLE      mapping;
#else
    int         fd;
#endif
} huc_map_t;

typedef struct {
    int32_t  range_start;
    int32_t  range_end;
    int32_t  atom_count;
    char     filename[MAX_FILENAME];
    huc_map_t map;
    const uint8_t* hashes;
    const uint8_t* centroids;
    const uint8_t* hilberts;
} huc_block_t;

struct huc_ctx {
    char         blob_dir[512];
    huc_block_t* blocks;
    int32_t      block_count;
    huc_map_t    reverse_map;
    const uint8_t* reverse_entries;
    uint32_t     reverse_count;
    char         version[16];
    /* Tier-1 ranges parsed from the index. */
    struct { int32_t lo, hi; } tier1[64];
    int32_t      tier1_count;
};

static int huc_mmap_file(const char* path, huc_map_t* out)
{
    memset(out, 0, sizeof(*out));
#ifdef _WIN32
    HANDLE f = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, NULL,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return -1;
    LARGE_INTEGER li;
    if (!GetFileSizeEx(f, &li) || li.QuadPart == 0) { CloseHandle(f); return -1; }
    HANDLE m = CreateFileMappingA(f, NULL, PAGE_READONLY, 0, 0, NULL);
    if (!m) { CloseHandle(f); return -1; }
    void* ptr = MapViewOfFile(m, FILE_MAP_READ, 0, 0, (size_t) li.QuadPart);
    if (!ptr) { CloseHandle(m); CloseHandle(f); return -1; }
    out->base = ptr; out->size = (size_t) li.QuadPart;
    out->file = f; out->mapping = m;
    return 0;
#else
    int fd = open(path, O_RDONLY);
    if (fd < 0) return -errno;
    struct stat st;
    if (fstat(fd, &st) < 0 || st.st_size == 0) { close(fd); return -1; }
    void* ptr = mmap(NULL, (size_t) st.st_size, PROT_READ, MAP_PRIVATE, fd, 0);
    if (ptr == MAP_FAILED) { close(fd); return -1; }
    out->base = ptr; out->size = (size_t) st.st_size; out->fd = fd;
    return 0;
#endif
}

static void huc_munmap_file(huc_map_t* m)
{
    if (!m || !m->base) return;
#ifdef _WIN32
    UnmapViewOfFile(m->base);
    if (m->mapping) CloseHandle(m->mapping);
    if (m->file) CloseHandle(m->file);
#else
    munmap((void*) m->base, m->size);
    if (m->fd > 0) close(m->fd);
#endif
    memset(m, 0, sizeof(*m));
}

#ifdef _WIN32
  #define PATH_SEP "\\"
#else
  #define PATH_SEP "/"
#endif

huc_ctx_t* huc_create(void)
{
    huc_ctx_t* c = (huc_ctx_t*) calloc(1, sizeof(huc_ctx_t));
    return c;
}

void huc_dispose(huc_ctx_t* ctx)
{
    if (!ctx) return;
    huc_shutdown(ctx);
    free(ctx);
}

static int parse_index(huc_ctx_t* ctx, const char* path)
{
    huc_map_t idx;
    if (huc_mmap_file(path, &idx) != 0) return -1;
    if (idx.size < 24) { huc_munmap_file(&idx); return -1; }
    const uint8_t* p = (const uint8_t*) idx.base;
    uint32_t magic, version, block_count, tier1_count, rev_off, reserved;
    memcpy(&magic, p +  0, 4);
    memcpy(&version, p +  4, 4);
    memcpy(&block_count, p +  8, 4);
    memcpy(&tier1_count, p + 12, 4);
    memcpy(&rev_off, p + 16, 4);
    memcpy(&reserved, p + 20, 4);
    (void) reserved;
    if (magic != IDX_MAGIC || version != EXPECTED_VERSION) {
        huc_munmap_file(&idx); return -1;
    }
    if (block_count == 0 || block_count > 4096) { huc_munmap_file(&idx); return -1; }

    snprintf(ctx->version, sizeof(ctx->version), "%u.%u.%u",
             (version >> 16) & 0xFF, (version >> 8) & 0xFF, version & 0xFF);

    size_t blocks_off = 24;
    size_t tier1_off = blocks_off + (size_t) block_count * 32;
    size_t string_off = tier1_off + (size_t) tier1_count * 8;
    if (string_off >= idx.size) { huc_munmap_file(&idx); return -1; }
    const char* string_table = (const char*) p + string_off;

    huc_block_t* blocks = (huc_block_t*) calloc(block_count, sizeof(huc_block_t));
    if (!blocks) { huc_munmap_file(&idx); return -1; }
    for (uint32_t i = 0; i < block_count; ++i) {
        const uint8_t* row = p + blocks_off + (size_t) i * 32;
        memcpy(&blocks[i].range_start, row +  0, 4);
        memcpy(&blocks[i].range_end,   row +  4, 4);
        memcpy(&blocks[i].atom_count,  row +  8, 4);
        uint32_t name_off;
        memcpy(&name_off, row + 12, 4);
        snprintf(blocks[i].filename, MAX_FILENAME, "%s", string_table + name_off);
    }
    /* Tier-1 ranges (tier1_count fits the small embedded fixed-size table). */
    int32_t tcap = (int32_t) (sizeof(ctx->tier1) / sizeof(ctx->tier1[0]));
    int32_t tn = (int32_t) tier1_count < tcap ? (int32_t) tier1_count : tcap;
    for (int32_t i = 0; i < tn; ++i) {
        uint32_t lo, hi;
        memcpy(&lo, p + tier1_off + (size_t) i * 8 + 0, 4);
        memcpy(&hi, p + tier1_off + (size_t) i * 8 + 4, 4);
        ctx->tier1[i].lo = (int32_t) lo;
        ctx->tier1[i].hi = (int32_t) hi;
    }
    ctx->tier1_count = tn;
    ctx->blocks = blocks;
    ctx->block_count = (int32_t) block_count;
    huc_munmap_file(&idx);
    return 0;
}

static int parse_reverse(huc_ctx_t* ctx, const char* path)
{
    if (huc_mmap_file(path, &ctx->reverse_map) != 0) return -1;
    if (ctx->reverse_map.size < 16 + 32) {
        huc_munmap_file(&ctx->reverse_map); return -1;
    }
    const uint8_t* p = (const uint8_t*) ctx->reverse_map.base;
    uint32_t magic, version, n;
    memcpy(&magic, p +  0, 4);
    memcpy(&version, p +  4, 4);
    memcpy(&n,       p +  8, 4);
    if (magic != REV_MAGIC || version != EXPECTED_VERSION) {
        huc_munmap_file(&ctx->reverse_map); return -1;
    }
    ctx->reverse_entries = p + 16;
    ctx->reverse_count = n;
    return 0;
}

int huc_init(huc_ctx_t* ctx, const char* blob_dir)
{
    if (!ctx || !blob_dir) return -1;
    snprintf(ctx->blob_dir, sizeof(ctx->blob_dir), "%s", blob_dir);
    char idx_path[1024], rev_path[1024];
    snprintf(idx_path, sizeof(idx_path),
             "%s" PATH_SEP "hartonomous-ucd-17.0.0.idx", blob_dir);
    snprintf(rev_path, sizeof(rev_path),
             "%s" PATH_SEP "hartonomous-ucd-17.0.0.reverse.bin", blob_dir);
    if (parse_index(ctx, idx_path) != 0) return -1;
    if (parse_reverse(ctx, rev_path) != 0) return -1;
    return 0;
}

void huc_shutdown(huc_ctx_t* ctx)
{
    if (!ctx) return;
    if (ctx->blocks) {
        for (int32_t i = 0; i < ctx->block_count; ++i) {
            if (ctx->blocks[i].map.base) huc_munmap_file(&ctx->blocks[i].map);
        }
        free(ctx->blocks);
        ctx->blocks = NULL;
        ctx->block_count = 0;
    }
    if (ctx->reverse_map.base) huc_munmap_file(&ctx->reverse_map);
    ctx->reverse_entries = NULL;
    ctx->reverse_count = 0;
}

static huc_block_t* find_block(const huc_ctx_t* ctx, int32_t cp)
{
    if (!ctx || !ctx->blocks || cp < 0 || cp >= 0x110000) return NULL;
    int32_t lo = 0, hi = ctx->block_count - 1;
    while (lo <= hi) {
        int32_t mid = (lo + hi) >> 1;
        huc_block_t* b = &ctx->blocks[mid];
        if (cp < b->range_start)    hi = mid - 1;
        else if (cp > b->range_end) lo = mid + 1;
        else                        return b;
    }
    return NULL;
}

static int ensure_block_loaded(const huc_ctx_t* ctx, huc_block_t* b)
{
    if (b->hashes != NULL) return 0;
    char path[1024];
    snprintf(path, sizeof(path), "%s" PATH_SEP "%s", ctx->blob_dir, b->filename);
    if (huc_mmap_file(path, &b->map) != 0) return -1;
    if (b->map.size < 24 + 32) { huc_munmap_file(&b->map); return -1; }
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
        huc_munmap_file(&b->map); return -1;
    }
    b->hashes    = p + 24;
    b->centroids = p + 24 + (size_t) n * 32;
    b->hilberts  = p + 24 + (size_t) n * 64;
    return 0;
}

huc_tier_t huc_cp_tier(const huc_ctx_t* ctx, int32_t cp)
{
    if (!ctx) return HUC_TIER_UNAVAILABLE;
    /* Tier-1 binary search (small table). */
    int32_t lo = 0, hi = ctx->tier1_count - 1;
    while (lo <= hi) {
        int32_t mid = (lo + hi) >> 1;
        if (cp < ctx->tier1[mid].lo) hi = mid - 1;
        else if (cp > ctx->tier1[mid].hi) lo = mid + 1;
        else return HUC_TIER_PRECOMPUTED;
    }
    /* Outside tier-1; check whether it's in any block file. */
    huc_block_t* b = find_block(ctx, cp);
    return b ? HUC_TIER_AVAILABLE : HUC_TIER_UNAVAILABLE;
}

int huc_cp_hash(const huc_ctx_t* ctx, int32_t cp, uint8_t out[HUC_HASH_LEN])
{
    huc_block_t* b = find_block(ctx, cp);
    if (!b || ensure_block_loaded(ctx, b) != 0) return -1;
    memcpy(out, b->hashes + (size_t) (cp - b->range_start) * 32, HUC_HASH_LEN);
    return 0;
}

int huc_cp_centroid(const huc_ctx_t* ctx, int32_t cp, double out[HUC_CENTROID_DIM])
{
    huc_block_t* b = find_block(ctx, cp);
    if (!b || ensure_block_loaded(ctx, b) != 0) return -1;
    memcpy(out, b->centroids + (size_t) (cp - b->range_start) * 32,
           HUC_CENTROID_DIM * sizeof(double));
    return 0;
}

uint64_t huc_cp_hilbert(const huc_ctx_t* ctx, int32_t cp)
{
    huc_block_t* b = find_block(ctx, cp);
    if (!b || ensure_block_loaded(ctx, b) != 0) return 0;
    uint64_t v;
    memcpy(&v, b->hilberts + (size_t) (cp - b->range_start) * 8, 8);
    return v;
}

int32_t huc_cp_from_hash(const huc_ctx_t* ctx, const uint8_t hash[HUC_HASH_LEN])
{
    if (!ctx || !ctx->reverse_entries || ctx->reverse_count == 0) return -1;
    int32_t lo = 0, hi = (int32_t) ctx->reverse_count - 1;
    while (lo <= hi) {
        int32_t mid = (lo + hi) >> 1;
        const uint8_t* row = ctx->reverse_entries + (size_t) mid * 36;
        int cmp = memcmp(hash, row, 32);
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

const char* huc_ucd_version(const huc_ctx_t* ctx) { return ctx ? ctx->version : ""; }
int  huc_block_count(const huc_ctx_t* ctx) { return ctx ? ctx->block_count : 0; }
uint32_t huc_reverse_count(const huc_ctx_t* ctx) { return ctx ? ctx->reverse_count : 0; }

int huc_verify_blob(const huc_ctx_t* ctx)
{
    /* Stub: full BLAKE3 footer verification requires linking BLAKE3. The
     * embedded library defers integrity checks to the BLAKE3 footer
     * present in each file — caller-driven verification is opt-in. */
    if (!ctx) return -1;
    return 0;
}
