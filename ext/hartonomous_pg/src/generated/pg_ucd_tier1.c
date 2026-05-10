/* GENERATED. */
#include "pg_ucd_tier1.h"

const UcTier1Range uc_tier1_ranges[UC_TIER1_RANGE_COUNT] = { {0x0,0x7FF}, {0x900,0xFFF}, {0x1000,0x13FF}, {0x1D00,0x1FFF}, {0x2000,0x2BFF}, {0x3000,0x33FF}, {0x3400,0x9FFF}, {0xA000,0xD7AF}, {0xF900,0xFFEF}, {0x1D400,0x1D7FF}, {0x1F300,0x1FAFF} };

int uc_cp_in_tier1(int32_t cp)
{
    int lo = 0, hi = UC_TIER1_RANGE_COUNT - 1;
    while (lo <= hi) {
        int mid = (lo + hi) >> 1;
        if (cp <  uc_tier1_ranges[mid].lo) hi = mid - 1;
        else if (cp > uc_tier1_ranges[mid].hi) lo = mid + 1;
        else return 1;
    }
    return 0;
}
