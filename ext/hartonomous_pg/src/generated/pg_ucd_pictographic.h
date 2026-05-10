/* GENERATED — Extended_Pictographic bitmap. */
#ifndef PG_UCD_PICTOGRAPHIC_H
#define PG_UCD_PICTOGRAPHIC_H
#include <stdint.h>
#include "pg_unicode_version.h"

#define UC_EXT_PICTOGRAPHIC_BITMAP_LEN  139264
extern const uint8_t uc_ext_pictographic_bitmap[UC_EXT_PICTOGRAPHIC_BITMAP_LEN];
static inline int uc_extended_pictographic(int32_t cp) {
    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) return 0;
    return (uc_ext_pictographic_bitmap[cp >> 3] >> (cp & 7)) & 1;
}
#endif
