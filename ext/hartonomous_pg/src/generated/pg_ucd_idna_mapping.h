/* GENERATED — UTS #46 IdnaMappingTable.txt. */
#ifndef PG_UCD_IDNA_MAPPING_H
#define PG_UCD_IDNA_MAPPING_H
#include <stdint.h>

#define UC_IDNA_COUNT            9262
#define UC_IDNA_STATUS_TOTAL     56475
#define UC_IDNA_MAP_TOTAL        7950

extern const uint32_t uc_idna_lo        [UC_IDNA_COUNT];
extern const uint32_t uc_idna_hi        [UC_IDNA_COUNT];
extern const uint32_t uc_idna_status_off[UC_IDNA_COUNT];
extern const uint8_t  uc_idna_status_len[UC_IDNA_COUNT];
extern const uint8_t  uc_idna_status    [UC_IDNA_STATUS_TOTAL];
extern const uint32_t uc_idna_map_off   [UC_IDNA_COUNT];
extern const uint8_t  uc_idna_map_len   [UC_IDNA_COUNT];
extern const uint32_t uc_idna_map       [UC_IDNA_MAP_TOTAL];
#endif
