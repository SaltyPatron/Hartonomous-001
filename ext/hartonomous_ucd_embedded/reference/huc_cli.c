/*
 * huc_cli.c — reference CLI for the embedded UCD library.
 *
 * Usage:
 *   huc <blob_dir> hash <cp>           hex-print the BLAKE3 hash for cp
 *   huc <blob_dir> centroid <cp>       print 4 doubles (S^3 centroid)
 *   huc <blob_dir> hilbert <cp>        print Hilbert 4D code
 *   huc <blob_dir> tier <cp>           print tier (1/2/3)
 *   huc <blob_dir> from-hash <hex>     reverse: 64-char hex hash → cp
 *   huc <blob_dir> info                blob version + block/reverse counts
 *
 * Examples:
 *   huc ./blob hash 0x4E2D            # hash for U+4E2D (中)
 *   huc ./blob tier 0x1F600           # tier for emoji 😀
 */
#include "hartonomous_ucd.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>

static int parse_cp(const char* s, int32_t* out)
{
    char* end;
    unsigned long v = strtoul(s, &end, 0);
    if (*end != 0 || v > 0x10FFFF) return -1;
    *out = (int32_t) v;
    return 0;
}

static int parse_hex_hash(const char* s, uint8_t out[HUC_HASH_LEN])
{
    if (strlen(s) != 64) return -1;
    for (int i = 0; i < 32; ++i) {
        unsigned x;
        if (sscanf(s + i * 2, "%2x", &x) != 1) return -1;
        out[i] = (uint8_t) x;
    }
    return 0;
}

static void print_hex(const uint8_t* p, size_t n)
{
    for (size_t i = 0; i < n; ++i) printf("%02x", p[i]);
    printf("\n");
}

int main(int argc, char** argv)
{
    if (argc < 3) {
        fprintf(stderr, "usage: %s <blob_dir> <command> [args...]\n", argv[0]);
        return 1;
    }
    huc_ctx_t* ctx = huc_create();
    if (!ctx) { fprintf(stderr, "huc_create failed\n"); return 2; }
    if (huc_init(ctx, argv[1]) != 0) {
        fprintf(stderr, "huc_init failed for blob_dir=%s\n", argv[1]);
        huc_dispose(ctx);
        return 3;
    }

    const char* cmd = argv[2];
    int rc = 0;

    if (strcmp(cmd, "info") == 0) {
        printf("ucd_version=%s\n", huc_ucd_version(ctx));
        printf("block_count=%d\n", huc_block_count(ctx));
        printf("reverse_count=%u\n", huc_reverse_count(ctx));
    } else if (strcmp(cmd, "hash") == 0 && argc >= 4) {
        int32_t cp;
        if (parse_cp(argv[3], &cp) != 0) { fprintf(stderr, "bad cp\n"); rc = 4; goto done; }
        uint8_t h[HUC_HASH_LEN];
        if (huc_cp_hash(ctx, cp, h) != 0) { fprintf(stderr, "unavailable\n"); rc = 5; goto done; }
        print_hex(h, HUC_HASH_LEN);
    } else if (strcmp(cmd, "centroid") == 0 && argc >= 4) {
        int32_t cp;
        if (parse_cp(argv[3], &cp) != 0) { fprintf(stderr, "bad cp\n"); rc = 4; goto done; }
        double c[HUC_CENTROID_DIM];
        if (huc_cp_centroid(ctx, cp, c) != 0) { fprintf(stderr, "unavailable\n"); rc = 5; goto done; }
        printf("%.17g %.17g %.17g %.17g\n", c[0], c[1], c[2], c[3]);
    } else if (strcmp(cmd, "hilbert") == 0 && argc >= 4) {
        int32_t cp;
        if (parse_cp(argv[3], &cp) != 0) { fprintf(stderr, "bad cp\n"); rc = 4; goto done; }
        uint64_t v = huc_cp_hilbert(ctx, cp);
        printf("%llu (0x%016llx)\n", (unsigned long long) v, (unsigned long long) v);
    } else if (strcmp(cmd, "tier") == 0 && argc >= 4) {
        int32_t cp;
        if (parse_cp(argv[3], &cp) != 0) { fprintf(stderr, "bad cp\n"); rc = 4; goto done; }
        printf("%d\n", (int) huc_cp_tier(ctx, cp));
    } else if (strcmp(cmd, "from-hash") == 0 && argc >= 4) {
        uint8_t h[HUC_HASH_LEN];
        if (parse_hex_hash(argv[3], h) != 0) { fprintf(stderr, "bad hash\n"); rc = 4; goto done; }
        int32_t cp = huc_cp_from_hash(ctx, h);
        if (cp < 0) printf("not found\n");
        else        printf("U+%04X\n", cp);
    } else {
        fprintf(stderr, "unknown command or missing args: %s\n", cmd);
        rc = 6;
    }

done:
    huc_dispose(ctx);
    return rc;
}
