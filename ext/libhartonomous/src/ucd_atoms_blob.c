/*
 * ucd_atoms_blob.c — runtime loader for the per-block Hartonomous UCD atom
 * layout. Identical wire format to the PG extension's pg_ucd_atoms_blob.c
 * (same blob files, same on-disk header). This version is libc-only — no
 * postgres.h, no ereport — so libhartonomous can host the loader and
 * expose the huc_cp_*_at accessors to both the C# P/Invoke surface and
 * the PG extension (which, post-refactor, will link these instead of
 * shipping its own copy).
 *
 * Microsecond access: a process that touches CJK loads one ~1.5 MB block
 * file (kernel paged on demand) and indexes via O(log 397) binary search to
 * find the block + O(1) offset within. Processes that never touch CJK
 * never page in those bytes.
 */
#include "hartonomous.h"
#include "generated/pg_ucd_casing.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <sys/types.h>
#include <sys/stat.h>

#ifdef _WIN32
  #include <windows.h>
#else
  #include <fcntl.h>
  #include <unistd.h>
  #include <sys/mman.h>
#endif

/* Constants — must match scripts/build/generate_unicode_tables.py output. */
#define BLK_MAGIC 0x4B4C4248u  /* 'HBLK' LE */
#define IDX_MAGIC 0x58444348u  /* 'HCDX' LE */
#define REV_MAGIC 0x56455248u  /* 'HREV' LE */
#define EXPECTED_VERSION 0x00170000u
#define HUC_CP_REVERSE_ENTRY_SIZE 36
#define HUC_HASH_LEN 32
#define HUC_CP_MAX  0x110000

/* ── Cross-platform mmap ──────────────────────────────────────────── */
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

static int huc_file_exists(const char* path)
{
#ifdef _WIN32
    DWORD attrs = GetFileAttributesA(path);
    return attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY) == 0;
#else
    struct stat st;
    return stat(path, &st) == 0 && S_ISREG(st.st_mode);
#endif
}

/* ── Per-block range table ────────────────────────────────────────── */
typedef struct {
    int32_t   range_start;
    int32_t   range_end;
    int32_t   atom_count;
    char      filename[128];
    HucMap    map;        /* lazy: only set after first touch */
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
static void*      g_reverse_heap = NULL;
static int        g_loaded = 0;

typedef struct {
    uint8_t hash[HUC_HASH_LEN];
    uint32_t cp;
} HucReverseEntry;

typedef char huc_reverse_entry_size_check[
    (sizeof(HucReverseEntry) == HUC_CP_REVERSE_ENTRY_SIZE) ? 1 : -1
];

/* ── Index parser ─────────────────────────────────────────────────── */
static int parse_index(const char* path)
{
    HucMap idx;
    if (huc_mmap(path, &idx) != 0) {
        fprintf(stderr, "hartonomous: failed to mmap UCD index %s\n", path);
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
        fprintf(stderr,
                "hartonomous: UCD index magic/version mismatch (got 0x%08x v0x%08x)\n",
                magic, version);
        huc_munmap(&idx);
        return -1;
    }
    if (block_count == 0 || block_count > 4096) { huc_munmap(&idx); return -1; }

    size_t blocks_off = 24;
    size_t tier1_off = blocks_off + (size_t) block_count * 32;
    size_t string_off = tier1_off + (size_t) tier1_count * 8;
    if (string_off + rev_off >= idx.size) { huc_munmap(&idx); return -1; }
    const char* string_table = (const char*) p + string_off;

    BlockEntry* blocks = (BlockEntry*) calloc(block_count, sizeof(BlockEntry));
    if (!blocks) { huc_munmap(&idx); return -1; }
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
    if (cp < 0 || cp >= HUC_CP_MAX) return NULL;
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

/* Lazily map the block's file on first access. Defensive: copies the
 * relevant data sections (hashes, centroids, hilberts) into HEAP memory
 * and immediately unmaps the file. Pointers handed back to callers
 * (huc_cp_hash_at, huc_cp_centroid_at, huc_cp_hilbert_at) point into
 * stable process heap, NOT into a mmap-backed region whose underlying
 * file or page-cache mapping can vanish (Docker/WSL bind-mount races,
 * page eviction, file replacement, etc.). The earlier mmap-only path
 * SEGV'd in libc memcpy when the kernel page-faulted into a no-longer-
 * valid mapping during PG SRF iteration. Cost: ~3 MB heap per loaded
 * block file (hashes 32B + centroids 32B + hilberts 8B = 72B per atom;
 * a typical block has ~1k–32k atoms, so 72KB–2.3MB per block, summed
 * only across blocks that get touched). Memory is process-lifetime —
 * matches the existing semantics of g_blocks. */
static int ensure_block_mapped(BlockEntry* b)
{
    if (b->hashes != NULL) return 0;  /* already loaded */
    char path[1024];
    snprintf(path, sizeof(path), "%s%s%s",
             g_blob_dir,
#ifdef _WIN32
             "\\",
#else
             "/",
#endif
             b->filename);
    HucMap tmp;
    if (huc_mmap(path, &tmp) != 0) {
        return -1;  /* block file unavailable; caller falls back to NULL */
    }
    if (tmp.size < 24 + 32) { huc_munmap(&tmp); return -1; }
    const uint8_t* p = (const uint8_t*) tmp.base;
    uint32_t magic, version, rs, re_, n;
    memcpy(&magic,    p +  0, 4);
    memcpy(&version,  p +  4, 4);
    memcpy(&rs,       p +  8, 4);
    memcpy(&re_,      p + 12, 4);
    memcpy(&n,        p + 16, 4);
    if (magic != BLK_MAGIC || version != EXPECTED_VERSION
        || (int32_t) rs != b->range_start || (int32_t) re_ != b->range_end
        || (int32_t) n  != b->atom_count) {
        fprintf(stderr, "hartonomous: block file header mismatch %s\n", b->filename);
        huc_munmap(&tmp);
        return -1;
    }
    /* Header is 24 bytes; data: hashes (n×32) + centroids (n×32) + hilberts (n×8). */
    size_t need = (size_t) n * (32 + 32 + 8);
    if (tmp.size < 24 + need) {
        fprintf(stderr, "hartonomous: block file truncated %s (size=%zu, need=%zu)\n",
                b->filename, tmp.size, (size_t)(24 + need));
        huc_munmap(&tmp);
        return -1;
    }
    /* Copy each data section into heap. malloc once per section so the
     * pointers we hand out are aligned and stable. */
    uint8_t* heap_hashes    = (uint8_t*) malloc((size_t) n * 32);
    uint8_t* heap_centroids = (uint8_t*) malloc((size_t) n * 32);
    uint8_t* heap_hilberts  = (uint8_t*) malloc((size_t) n * 8);
    if (!heap_hashes || !heap_centroids || !heap_hilberts) {
        free(heap_hashes); free(heap_centroids); free(heap_hilberts);
        fprintf(stderr, "hartonomous: malloc failed for block %s\n", b->filename);
        huc_munmap(&tmp);
        return -1;
    }
    memcpy(heap_hashes,    p + 24,                       (size_t) n * 32);
    memcpy(heap_centroids, p + 24 + (size_t) n * 32,     (size_t) n * 32);
    memcpy(heap_hilberts,  p + 24 + (size_t) n * 64,     (size_t) n *  8);
    /* Unmap immediately. We no longer depend on the file or its mapping. */
    huc_munmap(&tmp);
    /* b->map stays zeroed (it was never assigned to). The unload path
     * checks b->hashes and (now) needs to free() the heap copies; updated
     * in hartonomous_ucd_unload below. */
    b->hashes    = heap_hashes;
    b->centroids = heap_centroids;
    b->hilberts  = heap_hilberts;
    return 0;
}

static int parse_reverse(const char* path)
{
    if (huc_mmap(path, &g_reverse_map) != 0) {
        fprintf(stderr, "hartonomous: failed to mmap UCD reverse table %s\n", path);
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
        fprintf(stderr, "hartonomous: UCD reverse magic/version mismatch\n");
        huc_munmap(&g_reverse_map);
        return -1;
    }
    g_reverse_entries = p + 16;
    g_reverse_count = n;
    return 0;
}

static int compare_reverse_entries(const void* left, const void* right)
{
    const HucReverseEntry* a = (const HucReverseEntry*) left;
    const HucReverseEntry* b = (const HucReverseEntry*) right;
    int cmp = memcmp(a->hash, b->hash, HUC_HASH_LEN);
    if (cmp != 0) return cmp;
    if (a->cp < b->cp) return -1;
    if (a->cp > b->cp) return 1;
    return 0;
}

static void write_codepoint_hash(int32_t cp, uint8_t out[HUC_HASH_LEN])
{
    uint8_t rune_be32[4];
    rune_be32[0] = (uint8_t) (((uint32_t) cp >> 24) & 0xFF);
    rune_be32[1] = (uint8_t) (((uint32_t) cp >> 16) & 0xFF);
    rune_be32[2] = (uint8_t) (((uint32_t) cp >> 8) & 0xFF);
    rune_be32[3] = (uint8_t) ((uint32_t) cp & 0xFF);
    hartonomous_blake3(rune_be32, sizeof(rune_be32), out);
}

static int build_embedded_atoms(void)
{
    static const int HUC_HILBERT_ORDER = 16;
    BlockEntry* blocks = (BlockEntry*) calloc(1, sizeof(BlockEntry));
    uint8_t* hashes = NULL;
    uint8_t* centroids = NULL;
    uint8_t* hilberts = NULL;
    HucReverseEntry* reverse = NULL;
    if (!blocks) return -1;

    hashes = (uint8_t*) malloc((size_t) HUC_CP_MAX * HUC_HASH_LEN);
    centroids = (uint8_t*) malloc((size_t) HUC_CP_MAX * 4 * sizeof(double));
    hilberts = (uint8_t*) malloc((size_t) HUC_CP_MAX * sizeof(uint64_t));
    reverse = (HucReverseEntry*) malloc((size_t) HUC_CP_MAX * sizeof(HucReverseEntry));
    if (!hashes || !centroids || !hilberts || !reverse) {
        free(reverse);
        free(hilberts);
        free(centroids);
        free(hashes);
        free(blocks);
        return -1;
    }

    blocks[0].range_start = 0;
    blocks[0].range_end = HUC_CP_MAX - 1;
    blocks[0].atom_count = HUC_CP_MAX;
    snprintf(blocks[0].filename, sizeof(blocks[0].filename), "%s", "<embedded>");
    blocks[0].hashes = hashes;
    blocks[0].centroids = centroids;
    blocks[0].hilberts = hilberts;

    for (int32_t cp = 0; cp < HUC_CP_MAX; ++cp) {
        uint8_t* hash = hashes + (size_t) cp * HUC_HASH_LEN;
        double centroid[4];
        double sf_params[2];
        uint64_t hilbert;

        write_codepoint_hash(cp, hash);

        sf_params[0] = (double) uc_uca_index[cp];
        sf_params[1] = (double) UC_UCA_TOTAL;
        if (hartonomous_super_fibonacci(sf_params, 2, centroid) != 0) {
            free(reverse);
            free(hilberts);
            free(centroids);
            free(hashes);
            free(blocks);
            return -1;
        }

        memcpy(centroids + (size_t) cp * 4 * sizeof(double), centroid, 4 * sizeof(double));
        hilbert = hartonomous_hilbert_index(centroid, HUC_HILBERT_ORDER);
        memcpy(hilberts + (size_t) cp * sizeof(uint64_t), &hilbert, sizeof(uint64_t));

        memcpy(reverse[cp].hash, hash, HUC_HASH_LEN);
        reverse[cp].cp = (uint32_t) cp;
    }

    qsort(reverse, (size_t) HUC_CP_MAX, sizeof(HucReverseEntry), compare_reverse_entries);

    g_blocks = blocks;
    g_block_count = 1;
    g_reverse_heap = reverse;
    g_reverse_entries = (const uint8_t*) reverse;
    g_reverse_count = HUC_CP_MAX;
    g_loaded = 1;
    return 0;
}

/* ── Public loader (libhartonomous API, declared in hartonomous.h) ── */
int hartonomous_ucd_load(const char* dir)
{
    if (g_loaded) return 0;
    if (!dir) return -1;
    snprintf(g_blob_dir, sizeof(g_blob_dir), "%s", dir);
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

    int have_idx = huc_file_exists(idx_path);
    int have_rev = huc_file_exists(rev_path);
    if (!have_idx && !have_rev) {
        return build_embedded_atoms();
    }
    if (!have_idx || !have_rev) {
        fprintf(stderr,
                "hartonomous: incomplete UCD blob catalog in %s (idx=%d reverse=%d)\n",
                g_blob_dir, have_idx, have_rev);
        return -1;
    }

    if (parse_index(idx_path) == 0 && parse_reverse(rev_path) == 0) {
        /* Eager-load every block file into heap. Same fix as the PG extension's
         * pg_ucd_atoms_blob.c — the lazy-mmap path SEGV'd in libc memcpy when
         * the kernel page-faulted into a no-longer-valid mapping during long
         * SRF iteration. Heap copies stay valid for the process lifetime.
         * Cost: ~80 MB resident; one-time at load. */
        for (int32_t i = 0; i < g_block_count; ++i) {
            if (ensure_block_mapped(&g_blocks[i]) != 0) {
                fprintf(stderr,
                        "hartonomous: incomplete UCD block catalog in %s; refusing partial Unicode atom catalog\n",
                        g_blob_dir);
                hartonomous_ucd_unload();
                return -1;
            }
        }

        g_loaded = 1;
        return 0;
    }

    hartonomous_ucd_unload();
    return -1;
}

void hartonomous_ucd_unload(void)
{
    if (g_blocks) {
        for (int32_t i = 0; i < g_block_count; ++i) {
            /* Heap-copy path (current): hashes/centroids/hilberts are
             * malloc'd; mmap is closed at load time. Free the heap copies. */
            free((void*) g_blocks[i].hashes);
            free((void*) g_blocks[i].centroids);
            free((void*) g_blocks[i].hilberts);
            g_blocks[i].hashes    = NULL;
            g_blocks[i].centroids = NULL;
            g_blocks[i].hilberts  = NULL;
            /* Defensive: in case any future code path sets b->map.base, also
             * unmap. With the heap-copy ensure_block_mapped, b->map is
             * never assigned to, so this is a no-op today. */
            if (g_blocks[i].map.base) huc_munmap(&g_blocks[i].map);
        }
        free(g_blocks);
        g_blocks = NULL;
        g_block_count = 0;
    }
    if (g_reverse_map.base) huc_munmap(&g_reverse_map);
    free(g_reverse_heap);
    g_reverse_heap = NULL;
    g_reverse_entries = NULL;
    g_reverse_count = 0;
    g_loaded = 0;
}

/* ── Internal accessors used by text_decompose.c ─────────────────── */
const uint8_t* huc_cp_hash_at(int32_t cp);
const double*  huc_cp_centroid_at(int32_t cp);
uint64_t       huc_cp_hilbert_at(int32_t cp);
int32_t        huc_cp_from_hash(const uint8_t* hash32);

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

int32_t huc_cp_from_hash(const uint8_t* hash32)
{
    if (!g_reverse_entries || g_reverse_count == 0) return -1;
    int32_t lo = 0, hi = (int32_t) g_reverse_count - 1;
    while (lo <= hi) {
        int32_t mid = (lo + hi) >> 1;
        const uint8_t* row = g_reverse_entries + (size_t) mid * HUC_CP_REVERSE_ENTRY_SIZE;
        int cmp = memcmp(hash32, row, HUC_HASH_LEN);
        if (cmp < 0) hi = mid - 1;
        else if (cmp > 0) lo = mid + 1;
        else {
            uint32_t cp;
            memcpy(&cp, row + HUC_HASH_LEN, 4);
            return (int32_t) cp;
        }
    }
    return -1;
}

int hartonomous_ucd_loaded(void) { return g_loaded; }

/* ── Public C ABI exports for in-process consumers ─────────────────── */

HARTONOMOUS_API int hartonomous_ucd_loaded_state(void) { return g_loaded; }

HARTONOMOUS_API int hartonomous_ucd_catalog_ready(void)
{
    if (!g_loaded || !g_blocks || g_block_count <= 0 || !g_reverse_entries || g_reverse_count == 0) {
        return 0;
    }

    const int32_t samples[] = {0x0000, 0x0041, 0x0301, 0x1F600, 0x10FFFF};
    for (size_t i = 0; i < sizeof(samples) / sizeof(samples[0]); ++i) {
        int32_t cp = samples[i];
        const uint8_t* hash = huc_cp_hash_at(cp);
        const double* centroid = huc_cp_centroid_at(cp);
        if (!hash || !centroid) {
            return 0;
        }

        double norm2 = centroid[0] * centroid[0]
            + centroid[1] * centroid[1]
            + centroid[2] * centroid[2]
            + centroid[3] * centroid[3];
        if (!isfinite(norm2) || norm2 < 0.999999 || norm2 > 1.000001) {
            return 0;
        }

        if (huc_cp_from_hash(hash) != cp) {
            return 0;
        }

        (void) huc_cp_hilbert_at(cp);
    }

    return 1;
}

HARTONOMOUS_API int hartonomous_ucd_cp_centroid(int32_t cp, double out[4])
{
    if (!out) return -1;
    const double* c = huc_cp_centroid_at(cp);
    if (!c) return -1;
    memcpy(out, c, 4 * sizeof(double));
    return 0;
}

HARTONOMOUS_API int hartonomous_ucd_cp_hash(int32_t cp, uint8_t out[32])
{
    if (!out) return -1;
    const uint8_t* h = huc_cp_hash_at(cp);
    if (!h) return -1;
    memcpy(out, h, 32);
    return 0;
}

HARTONOMOUS_API int hartonomous_ucd_cp_hilbert(int32_t cp, uint64_t* out)
{
    if (!out) return -1;
    /* huc_cp_hilbert_at returns 0 both for "not loaded" and for the
       genuine value 0; document that ambiguity in the header. */
    *out = huc_cp_hilbert_at(cp);
    return 0;
}

HARTONOMOUS_API int32_t hartonomous_ucd_cp_from_hash(const uint8_t hash32[32])
{
    if (!hash32) return -1;
    return huc_cp_from_hash(hash32);
}
