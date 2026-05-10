/* GENERATED — UCD classification properties. */
#ifndef PG_UCD_CLASSIFICATION_H
#define PG_UCD_CLASSIFICATION_H
#include <stdint.h>
#include "pg_unicode_version.h"

#define UC_GC_Cn  0
#define UC_GC_Lu  1
#define UC_GC_Ll  2
#define UC_GC_Lt  3
#define UC_GC_Lm  4
#define UC_GC_Lo  5
#define UC_GC_Mn  6
#define UC_GC_Mc  7
#define UC_GC_Me  8
#define UC_GC_Nd  9
#define UC_GC_Nl  10
#define UC_GC_No  11
#define UC_GC_Pc  12
#define UC_GC_Pd  13
#define UC_GC_Ps  14
#define UC_GC_Pe  15
#define UC_GC_Pi  16
#define UC_GC_Pf  17
#define UC_GC_Po  18
#define UC_GC_Sm  19
#define UC_GC_Sc  20
#define UC_GC_Sk  21
#define UC_GC_So  22
#define UC_GC_Zs  23
#define UC_GC_Zl  24
#define UC_GC_Zp  25
#define UC_GC_Cc  26
#define UC_GC_Cf  27
#define UC_GC_Cs  28
#define UC_GC_Co  29
#define UC_BIDI_L  0
#define UC_BIDI_R  1
#define UC_BIDI_AL  2
#define UC_BIDI_EN  3
#define UC_BIDI_ES  4
#define UC_BIDI_ET  5
#define UC_BIDI_AN  6
#define UC_BIDI_CS  7
#define UC_BIDI_NSM  8
#define UC_BIDI_BN  9
#define UC_BIDI_B  10
#define UC_BIDI_S  11
#define UC_BIDI_WS  12
#define UC_BIDI_ON  13
#define UC_BIDI_LRE  14
#define UC_BIDI_LRO  15
#define UC_BIDI_RLE  16
#define UC_BIDI_RLO  17
#define UC_BIDI_PDF  18
#define UC_BIDI_LRI  19
#define UC_BIDI_RLI  20
#define UC_BIDI_FSI  21
#define UC_BIDI_PDI  22
#define UC_EAW_N  0
#define UC_EAW_Na  1
#define UC_EAW_A  2
#define UC_EAW_W  3
#define UC_EAW_F  4
#define UC_EAW_H  5
#define UC_HSY_NA  0
#define UC_HSY_L  1
#define UC_HSY_V  2
#define UC_HSY_T  3
#define UC_HSY_LV  4
#define UC_HSY_LVT  5
#define UC_NUM_TYPE_None  0
#define UC_NUM_TYPE_Decimal  1
#define UC_NUM_TYPE_Digit  2
#define UC_NUM_TYPE_Numeric  3

extern const uint8_t  uc_gc       [UNICODE_CODEPOINT_MAX];
extern const uint8_t  uc_ccc      [UNICODE_CODEPOINT_MAX];
extern const uint16_t uc_script   [UNICODE_CODEPOINT_MAX];
extern const uint16_t uc_block    [UNICODE_CODEPOINT_MAX];
extern const uint8_t  uc_bidi     [UNICODE_CODEPOINT_MAX];
extern const uint8_t  uc_eaw      [UNICODE_CODEPOINT_MAX];
extern const uint8_t  uc_hsy      [UNICODE_CODEPOINT_MAX];
extern const uint8_t  uc_num_type [UNICODE_CODEPOINT_MAX];
#endif
