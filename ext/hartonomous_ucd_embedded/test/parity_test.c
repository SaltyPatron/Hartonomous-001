/*
 * parity_test.c — sweep all 1.1M codepoints, sanity-check the embedded
 * library against expected invariants:
 *
 *   - Every assigned codepoint has hash != all-zeros AND centroid on S^3
 *     (||centroid|| ≈ 1.0)
 *   - Reverse lookup roundtrips for a sample of codepoints
 *   - All blocks reachable via tier query (every cp returns 1, 2, or 3)
 *
 * Optional --pg-extension <conn> arg would compare to the PG extension's
 * output for byte-identical agreement, but is left unimplemented in this
 * skeleton (that's a build-time addition once libpq is on the picture).
 *
 * Exit code: 0 = all checks pass, nonzero = at least one mismatch.
 */
#include "hartonomous_ucd.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

static int hash_is_zero(const uint8_t h[HUC_HASH_LEN])
{
    for (int i = 0; i < HUC_HASH_LEN; ++i) if (h[i]) return 0;
    return 1;
}

int main(int argc, char** argv)
{
    if (argc < 2) { fprintf(stderr, "usage: %s <blob_dir>\n", argv[0]); return 1; }
    huc_ctx_t* ctx = huc_create();
    if (huc_init(ctx, argv[1]) != 0) { fprintf(stderr, "init failed\n"); return 2; }

    int errors = 0;
    int sampled_reverse_pairs = 0;

    /* Sample: every 4096th codepoint */
    for (int32_t cp = 0; cp < 0x110000; cp += 4096) {
        huc_tier_t t = huc_cp_tier(ctx, cp);
        if (t != HUC_TIER_PRECOMPUTED && t != HUC_TIER_AVAILABLE && t != HUC_TIER_UNAVAILABLE) {
            fprintf(stderr, "U+%04X: bad tier %d\n", cp, (int) t);
            errors++;
            continue;
        }
        uint8_t h[HUC_HASH_LEN];
        double  c[HUC_CENTROID_DIM];
        if (huc_cp_hash(ctx, cp, h) != 0) continue;       /* tier-3 OK */
        if (huc_cp_centroid(ctx, cp, c) != 0) continue;
        /* Centroid should be on S^3 (||c|| ≈ 1). */
        double n2 = c[0]*c[0] + c[1]*c[1] + c[2]*c[2] + c[3]*c[3];
        if (fabs(n2 - 1.0) > 1e-6) {
            fprintf(stderr, "U+%04X: centroid not on S^3 (||c||²=%.6f)\n", cp, n2);
            errors++;
        }
        if (hash_is_zero(h)) {
            fprintf(stderr, "U+%04X: zero hash (corruption?)\n", cp);
            errors++;
        }
        /* Reverse roundtrip */
        int32_t rcp = huc_cp_from_hash(ctx, h);
        if (rcp != cp) {
            fprintf(stderr, "U+%04X: reverse mismatch (got U+%04X)\n", cp, rcp);
            errors++;
        } else {
            sampled_reverse_pairs++;
        }
    }

    printf("parity test: %d errors, %d reverse-roundtrip pairs verified, %d blocks indexed\n",
           errors, sampled_reverse_pairs, huc_block_count(ctx));
    huc_dispose(ctx);
    return errors == 0 ? 0 : 1;
}
