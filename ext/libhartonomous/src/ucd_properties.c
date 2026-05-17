/*
 * libhartonomous — public per-codepoint property accessors.
 *
 * Exposes the UAX-#14 / UAX-#29 break properties and the UCD case-folding
 * mappings as C ABI functions over the embedded UCD tables. Backing data
 * lives in the generated pg_ucd_*.c sources (linked into libhartonomous via
 * CMakeLists.txt) — this file is the thin public façade so C# callers
 * (BlobUcdPropertyAccessor) get one-call codepoint property reads without
 * any DB round trip.
 *
 * All accessors are pure (depend only on the codepoint integer and the
 * baked-in tables), bounds-checked, and ABI-stable. Out-of-range codepoints
 * return the property's "Other" / no-mapping value rather than failing —
 * callers that need to distinguish "valid but Other" from "out of range"
 * should range-check ahead of time.
 */

#include "hartonomous.h"

#include <stdint.h>
#include <string.h>

#include "generated/pg_unicode_version.h"
#include "generated/pg_ucd_segmentation.h"
#include "generated/pg_ucd_pictographic.h"
#include "generated/pg_ucd_casing.h"
#include "generated/pg_ucd_fcf.h"
#include "generated/pg_ucd_classification.h"
#include "generated/pg_ucd_decomp.h"

/* ── UAX-#29 / UAX-#14 break properties ─────────────────────────────── */

HARTONOMOUS_API uint8_t hartonomous_ucd_cp_gcb(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return 0; /* Other */
    }
    return uc_gcb[cp];
}

HARTONOMOUS_API uint8_t hartonomous_ucd_cp_wb(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return 0;
    }
    return uc_wb[cp];
}

HARTONOMOUS_API uint8_t hartonomous_ucd_cp_sb(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return 0;
    }
    return uc_sb[cp];
}

HARTONOMOUS_API uint8_t hartonomous_ucd_cp_lb(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return 0;
    }
    return uc_lb[cp];
}

HARTONOMOUS_API uint8_t hartonomous_ucd_cp_incb(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return 0; /* None */
    }
    return uc_incb[cp];
}

HARTONOMOUS_API int hartonomous_ucd_cp_extended_pictographic(int32_t cp)
{
    return uc_extended_pictographic(cp);
}

/* ── Case folding / case mapping ─────────────────────────────────────── */

HARTONOMOUS_API int32_t hartonomous_ucd_cp_simple_case_fold(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return cp;
    }
    return uc_simple_case_fold[cp];
}

HARTONOMOUS_API int32_t hartonomous_ucd_cp_simple_lowercase(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return cp;
    }
    return uc_simple_lowercase[cp];
}

HARTONOMOUS_API int32_t hartonomous_ucd_cp_simple_uppercase(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return cp;
    }
    return uc_simple_uppercase[cp];
}

HARTONOMOUS_API int32_t hartonomous_ucd_cp_simple_titlecase(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return cp;
    }
    return uc_simple_titlecase[cp];
}

HARTONOMOUS_API int32_t hartonomous_ucd_cp_uca_index(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return 0;
    }
    return uc_uca_index[cp];
}

/*
 * Copies the full case-fold expansion for `cp` into `out` (which must hold
 * at least `out_max` int32_t entries). Returns the number of codepoints
 * written (>=1 on success; 1 = no expansion / fold-to-self). Returns -1 if
 * `out` is NULL, the codepoint is out of range, or the caller buffer is too
 * small to hold the expansion. Callers can probe the required length by
 * passing out_max = 0 — the function then writes nothing and returns the
 * required length as a positive count via the negated return code is not
 * used; instead callers should size for the worst case (most expansions are
 * 1–3 codepoints; the longest in Unicode 17.0 is 4).
 */
HARTONOMOUS_API int hartonomous_ucd_cp_full_case_fold(
    int32_t cp,
    int32_t* out,
    int out_max)
{
    if (out == NULL || cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return -1;
    }
    uint16_t n = uc_fcf_len[cp];
    if (n == 0) {
        /* No expansion table entry: fold-to-self via simple_case_fold. */
        if (out_max < 1) {
            return -1;
        }
        out[0] = uc_simple_case_fold[cp];
        return 1;
    }
    if (out_max < (int)n) {
        return -1;
    }
    uint32_t off = uc_fcf_off[cp];
    if ((size_t)off + (size_t)n > (size_t)UC_FCF_DATA_LEN) {
        return -1;
    }
    memcpy(out, &uc_fcf_data[off], n * sizeof(int32_t));
    return (int)n;
}

/* ── UAX #44 classification properties (per-codepoint enum lookups) ───── */

HARTONOMOUS_API uint8_t hartonomous_ucd_cp_gc(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return 0; /* Cn */
    }
    return uc_gc[cp];
}

HARTONOMOUS_API uint8_t hartonomous_ucd_cp_ccc(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return 0;
    }
    return uc_ccc[cp];
}

HARTONOMOUS_API uint16_t hartonomous_ucd_cp_script(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return 0; /* Zzzz / Unknown */
    }
    return uc_script[cp];
}

HARTONOMOUS_API uint16_t hartonomous_ucd_cp_block(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return 0; /* No_Block */
    }
    return uc_block[cp];
}

HARTONOMOUS_API uint8_t hartonomous_ucd_cp_bidi(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return 0;
    }
    return uc_bidi[cp];
}

HARTONOMOUS_API uint8_t hartonomous_ucd_cp_eaw(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return 0; /* N */
    }
    return uc_eaw[cp];
}

HARTONOMOUS_API uint8_t hartonomous_ucd_cp_hsy(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return 0; /* Not_Applicable */
    }
    return uc_hsy[cp];
}

HARTONOMOUS_API uint8_t hartonomous_ucd_cp_num_type(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return 0; /* None */
    }
    return uc_num_type[cp];
}

/* ── UAX #44 decomposition properties ─────────────────────────────────── */

HARTONOMOUS_API uint8_t hartonomous_ucd_cp_decomp_type(int32_t cp)
{
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return 0; /* None */
    }
    return uc_decomp_type[cp];
}

/*
 * Copies the canonical/compatibility decomposition mapping for `cp` into
 * `out` (which must hold at least `out_max` int32_t entries). Returns the
 * number of codepoints written (0 if no decomposition; positive on success).
 * Returns -1 on null buffer, out-of-range codepoint, or insufficient buffer.
 * Hangul algorithmic decompositions are NOT expanded here — callers that
 * need Hangul L/V/T expansion must do it themselves (UAX #15 §3).
 */
HARTONOMOUS_API int hartonomous_ucd_cp_decomp_mapping(
    int32_t cp,
    int32_t* out,
    int out_max)
{
    if (out == NULL || cp < 0 || cp >= UNICODE_CODEPOINT_MAX) {
        return -1;
    }
    uint16_t n = uc_decomp_len[cp];
    if (n == 0) {
        return 0;
    }
    if (out_max < (int)n) {
        return -1;
    }
    uint32_t off = uc_decomp_off[cp];
    if ((size_t)off + (size_t)n > (size_t)UC_DECOMP_DATA_LEN) {
        return -1;
    }
    memcpy(out, &uc_decomp_data[off], n * sizeof(int32_t));
    return (int)n;
}

