/* GENERATED — UCD inventory tables. */
#ifndef PG_UCD_INVENTORY_H
#define PG_UCD_INVENTORY_H
#include <stdint.h>
#include "pg_unicode_version.h"

typedef struct { const char* code; const char* description; const char* group; } GCEntry;
typedef struct { const char* code; } ScriptEntry;
typedef struct { const char* code; int32_t range_start; int32_t range_end; } BlockEntry;
typedef struct { const char* category; const char* code; uint8_t enum_id; } BreakPropEntry;

#define UC_GC_COUNT      30
#define UC_SCRIPT_COUNT  176
#define UC_BLOCK_COUNT   347
#define UC_BREAK_COUNT   101

extern const GCEntry        uc_inv_gc[UC_GC_COUNT];
extern const ScriptEntry    uc_inv_scripts[UC_SCRIPT_COUNT];
extern const BlockEntry     uc_inv_blocks[UC_BLOCK_COUNT];
extern const BreakPropEntry uc_inv_break_props[UC_BREAK_COUNT];
#endif
