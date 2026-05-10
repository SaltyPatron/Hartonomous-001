/* GENERATED — UCD canonical/compat decomposition mappings. */
#ifndef PG_UCD_DECOMP_H
#define PG_UCD_DECOMP_H
#include <stdint.h>
#include "pg_unicode_version.h"

#define UC_DECOMP_TYPE_None  0
#define UC_DECOMP_TYPE_canonical  1
#define UC_DECOMP_TYPE_compat  2
#define UC_DECOMP_TYPE_circle  3
#define UC_DECOMP_TYPE_final  4
#define UC_DECOMP_TYPE_font  5
#define UC_DECOMP_TYPE_fraction  6
#define UC_DECOMP_TYPE_initial  7
#define UC_DECOMP_TYPE_isolated  8
#define UC_DECOMP_TYPE_medial  9
#define UC_DECOMP_TYPE_narrow  10
#define UC_DECOMP_TYPE_noBreak  11
#define UC_DECOMP_TYPE_small  12
#define UC_DECOMP_TYPE_square  13
#define UC_DECOMP_TYPE_sub  14
#define UC_DECOMP_TYPE_super  15
#define UC_DECOMP_TYPE_vertical  16
#define UC_DECOMP_TYPE_wide  17

extern const uint8_t  uc_decomp_type[UNICODE_CODEPOINT_MAX];
extern const uint32_t uc_decomp_off [UNICODE_CODEPOINT_MAX];
extern const uint16_t uc_decomp_len [UNICODE_CODEPOINT_MAX];
#define UC_DECOMP_DATA_LEN  8740
extern const int32_t  uc_decomp_data[UC_DECOMP_DATA_LEN];
#endif
