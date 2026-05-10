/* GENERATED — tier-1 codepoint range list (~75K cps). */
#ifndef PG_UCD_TIER1_H
#define PG_UCD_TIER1_H
#include <stdint.h>

#define UC_TIER1_RANGE_COUNT  11
typedef struct { int32_t lo, hi; } UcTier1Range;
extern const UcTier1Range uc_tier1_ranges[UC_TIER1_RANGE_COUNT];
/* O(log K) range membership test. K = UC_TIER1_RANGE_COUNT (small). */
int uc_cp_in_tier1(int32_t cp);
#endif
