/*
 * gen_ucd_flat — read ucd.all.flat.xml (UAX #42 flat form) and emit the
 * per-codepoint segmentation tables consumed by libhartonomous (UAX #29
 * grapheme / word / sentence boundaries + UAX #14 line breaks + InCB).
 *
 * Output is written to "<out_dir>/pg_ucd_segmentation.{c,h}" matching the
 * schema the rest of the codebase already consumes:
 *
 *   - UC_GCB_* / UC_WB_* / UC_SB_* / UC_LB_* / UC_INCB_* enum defines.
 *   - extern const uint8_t uc_gcb [UNICODE_CODEPOINT_MAX];
 *   - extern const uint8_t uc_wb  [UNICODE_CODEPOINT_MAX];
 *   - extern const uint8_t uc_sb  [UNICODE_CODEPOINT_MAX];
 *   - extern const uint8_t uc_lb  [UNICODE_CODEPOINT_MAX];
 *   - extern const uint8_t uc_incb[UNICODE_CODEPOINT_MAX];
 *
 * Why flat instead of grouped: the flat XML is self-contained per-char
 * with no group-inheritance state machine — parser simplicity wins over
 * grouped's compressed-size advantage. Per the project rule
 * .claude/rules/00-hartonomous-core.md "XML-flat for per-codepoint UCD
 * pre-gen".
 *
 * Property-value alias resolution: the flat XML uses the short alias
 * codes from PropertyValueAliases.txt for break properties — e.g. WB="LE"
 * means ALetter, SB="UP" means Upper, GCB="RI" means Regional_Indicator.
 * The aliases below are baked into the generator so the C output uses
 * the canonical long names with stable integer codes that match the
 * existing UC_GCB_* / UC_WB_* / UC_SB_* enum.
 *
 * Determinism: same UCD input + same generator version = byte-identical
 * output. Per project Law #6.
 *
 * Usage:
 *   gen_ucd_flat <ucd.all.flat.xml> <out_dir>
 */

#include "lh_emit.h"
#include "lh_input.h"
#include "xml_pull.h"

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define UCD_CP_COUNT 0x110000u

/* ── Enum tables — names match pg_ucd_segmentation.h defines ──────────── */

/* Grapheme_Cluster_Break (GCB) — UAX #29. Index = property value code,
 * value = canonical long name string. The integer codes are STABLE — they
 * encode the UC_GCB_<Name> ordinal. */
static const char *const GCB_NAMES[] = {
    /* 0 */ "Other",
    /* 1 */ "CR",
    /* 2 */ "LF",
    /* 3 */ "Control",
    /* 4 */ "Extend",
    /* 5 */ "ZWJ",
    /* 6 */ "Regional_Indicator",
    /* 7 */ "Prepend",
    /* 8 */ "SpacingMark",
    /* 9 */ "L",
    /* 10 */ "V",
    /* 11 */ "T",
    /* 12 */ "LV",
    /* 13 */ "LVT",
};
#define GCB_COUNT (sizeof(GCB_NAMES) / sizeof(GCB_NAMES[0]))

static const char *const WB_NAMES[] = {
    /* 0 */ "Other",
    /* 1 */ "CR",
    /* 2 */ "LF",
    /* 3 */ "Newline",
    /* 4 */ "Extend",
    /* 5 */ "ZWJ",
    /* 6 */ "Format",
    /* 7 */ "Katakana",
    /* 8 */ "Hebrew_Letter",
    /* 9 */ "ALetter",
    /* 10 */ "Single_Quote",
    /* 11 */ "Double_Quote",
    /* 12 */ "MidNumLet",
    /* 13 */ "MidLetter",
    /* 14 */ "MidNum",
    /* 15 */ "Numeric",
    /* 16 */ "ExtendNumLet",
    /* 17 */ "Regional_Indicator",
    /* 18 */ "WSegSpace",
    /* 19 */ "Extended_Pictographic",
};
#define WB_COUNT (sizeof(WB_NAMES) / sizeof(WB_NAMES[0]))

static const char *const SB_NAMES[] = {
    /* 0 */ "Other",
    /* 1 */ "CR",
    /* 2 */ "LF",
    /* 3 */ "Sep",
    /* 4 */ "Format",
    /* 5 */ "Sp",
    /* 6 */ "Lower",
    /* 7 */ "Upper",
    /* 8 */ "OLetter",
    /* 9 */ "Numeric",
    /* 10 */ "ATerm",
    /* 11 */ "STerm",
    /* 12 */ "Close",
    /* 13 */ "SContinue",
    /* 14 */ "Extend",
};
#define SB_COUNT (sizeof(SB_NAMES) / sizeof(SB_NAMES[0]))

/* Line_Break (LB) — UAX #14. Codes match the SHORT 2-letter alias,
 * which is what the flat XML emits directly (no alias step needed).
 * Order matches the existing pg_ucd_segmentation.h. */
static const char *const LB_NAMES[] = {
    /* 0 */ "XX",
    /* 1 */ "BK",
    /* 2 */ "CR",
    /* 3 */ "LF",
    /* 4 */ "CM",
    /* 5 */ "NL",
    /* 6 */ "SG",
    /* 7 */ "WJ",
    /* 8 */ "ZW",
    /* 9 */ "GL",
    /* 10 */ "SP",
    /* 11 */ "B2",
    /* 12 */ "BA",
    /* 13 */ "BB",
    /* 14 */ "HY",
    /* 15 */ "CB",
    /* 16 */ "CL",
    /* 17 */ "CP",
    /* 18 */ "EX",
    /* 19 */ "IN",
    /* 20 */ "NS",
    /* 21 */ "OP",
    /* 22 */ "QU",
    /* 23 */ "IS",
    /* 24 */ "NU",
    /* 25 */ "PO",
    /* 26 */ "PR",
    /* 27 */ "SY",
    /* 28 */ "AI",
    /* 29 */ "AL",
    /* 30 */ "CJ",
    /* 31 */ "EB",
    /* 32 */ "EM",
    /* 33 */ "H2",
    /* 34 */ "H3",
    /* 35 */ "HL",
    /* 36 */ "ID",
    /* 37 */ "JL",
    /* 38 */ "JV",
    /* 39 */ "JT",
    /* 40 */ "RI",
    /* 41 */ "SA",
    /* 42 */ "ZWJ",
    /* 43 */ "AK",
    /* 44 */ "AP",
    /* 45 */ "AS",
    /* 46 */ "VF",
    /* 47 */ "VI",
};
#define LB_COUNT (sizeof(LB_NAMES) / sizeof(LB_NAMES[0]))

/* Indic_Conjunct_Break (InCB) — UAX #29. */
static const char *const INCB_NAMES[] = {
    /* 0 */ "None",
    /* 1 */ "Linker",
    /* 2 */ "Extend",
    /* 3 */ "Consonant",
};
#define INCB_COUNT (sizeof(INCB_NAMES) / sizeof(INCB_NAMES[0]))

/* ── PropertyValueAliases.txt short→long maps ──────────────────────────
 *
 * The flat XML emits the short property-value alias for break properties.
 * Translate to the canonical long name before enum lookup. These maps are
 * extracted from PropertyValueAliases.txt for UCD 17.0.0. */

typedef struct alias_entry {
    const char *short_name;
    const char *long_name;
} alias_entry;

static const alias_entry GCB_ALIASES[] = {
    {"XX", "Other"},
    {"CN", "Control"},
    {"EX", "Extend"},
    {"PP", "Prepend"},
    {"RI", "Regional_Indicator"},
    {"SM", "SpacingMark"},
    /* CR, LF, ZWJ, L, V, T, LV, LVT are already their own canonical names */
};
#define GCB_ALIASES_COUNT (sizeof(GCB_ALIASES) / sizeof(GCB_ALIASES[0]))

static const alias_entry WB_ALIASES[] = {
    {"XX",  "Other"},
    {"NL",  "Newline"},
    {"FO",  "Format"},
    {"KA",  "Katakana"},
    {"HL",  "Hebrew_Letter"},
    {"LE",  "ALetter"},
    {"SQ",  "Single_Quote"},
    {"DQ",  "Double_Quote"},
    {"MB",  "MidNumLet"},
    {"ML",  "MidLetter"},
    {"MN",  "MidNum"},
    {"NU",  "Numeric"},
    {"EX",  "ExtendNumLet"},
    {"RI",  "Regional_Indicator"},
    {"EB",  "Extended_Pictographic"},
    {"GAZ", "Extended_Pictographic"},
    {"EBG", "Extended_Pictographic"},
    /* CR, LF, Extend, ZWJ, WSegSpace are already their own canonical names */
};
#define WB_ALIASES_COUNT (sizeof(WB_ALIASES) / sizeof(WB_ALIASES[0]))

static const alias_entry SB_ALIASES[] = {
    {"XX", "Other"},
    {"SE", "Sep"},
    {"FO", "Format"},
    {"SP", "Sp"},
    {"LO", "Lower"},
    {"UP", "Upper"},
    {"LE", "OLetter"},
    {"NU", "Numeric"},
    {"AT", "ATerm"},
    {"ST", "STerm"},
    {"CL", "Close"},
    {"SC", "SContinue"},
    {"EX", "Extend"},
    /* CR, LF are already their own canonical names */
};
#define SB_ALIASES_COUNT (sizeof(SB_ALIASES) / sizeof(SB_ALIASES[0]))

/* InCB: flat XML emits the canonical long names directly; no alias table
 * needed (None, Linker, Extend, Consonant match the NAMES table verbatim). */

/* Look up a canonical name from a short alias. If the input is already a
 * canonical long name (in NAMES table), pass it through unchanged. If it's
 * neither, return NULL (caller must default to "Other"-equivalent). */
static const char *resolve_alias(const char *raw,
                                 const alias_entry *aliases,
                                 size_t alias_count)
{
    if (!raw || !*raw) return NULL;
    for (size_t i = 0; i < alias_count; i++) {
        if (strcmp(raw, aliases[i].short_name) == 0) {
            return aliases[i].long_name;
        }
    }
    return raw;
}

/* Map a canonical long name to its enum integer code. Returns 0 for
 * "Other"-default if the name doesn't match. */
static uint8_t name_to_code(const char *name,
                            const char *const *names, size_t count,
                            uint8_t default_code)
{
    if (!name) return default_code;
    for (size_t i = 0; i < count; i++) {
        if (strcmp(name, names[i]) == 0) return (uint8_t)i;
    }
    return default_code;
}

static uint8_t parse_gcb(const char *raw)
{
    const char *canonical = resolve_alias(raw, GCB_ALIASES, GCB_ALIASES_COUNT);
    return name_to_code(canonical, GCB_NAMES, GCB_COUNT, 0 /* Other */);
}

static uint8_t parse_wb(const char *raw)
{
    const char *canonical = resolve_alias(raw, WB_ALIASES, WB_ALIASES_COUNT);
    return name_to_code(canonical, WB_NAMES, WB_COUNT, 0 /* Other */);
}

static uint8_t parse_sb(const char *raw)
{
    const char *canonical = resolve_alias(raw, SB_ALIASES, SB_ALIASES_COUNT);
    return name_to_code(canonical, SB_NAMES, SB_COUNT, 0 /* Other */);
}

static uint8_t parse_lb(const char *raw)
{
    /* LB short codes match the C enum directly; no alias step. */
    return name_to_code(raw, LB_NAMES, LB_COUNT, 0 /* XX */);
}

static uint8_t parse_incb(const char *raw)
{
    return name_to_code(raw, INCB_NAMES, INCB_COUNT, 0 /* None */);
}

static uint32_t parse_hex(const char *s)
{
    if (!s) return 0;
    uint32_t v = 0;
    while (*s) {
        char c = *s++;
        uint32_t d;
        if      (c >= '0' && c <= '9') d = (uint32_t)(c - '0');
        else if (c >= 'a' && c <= 'f') d = (uint32_t)(c - 'a' + 10);
        else if (c >= 'A' && c <= 'F') d = (uint32_t)(c - 'A' + 10);
        else return v;
        v = (v << 4) | d;
    }
    return v;
}

/* ── Storage ──────────────────────────────────────────────────────────── */

static uint8_t *g_gcb;
static uint8_t *g_wb;
static uint8_t *g_sb;
static uint8_t *g_lb;
static uint8_t *g_incb;

static void apply_leaf(xml_pull *p, const char *kind)
{
    (void)kind;
    const char *cp_s   = xml_pull_attr(p, "cp");
    const char *first  = xml_pull_attr(p, "first-cp");
    const char *last   = xml_pull_attr(p, "last-cp");
    const char *gcb_s  = xml_pull_attr(p, "GCB");
    const char *wb_s   = xml_pull_attr(p, "WB");
    const char *sb_s   = xml_pull_attr(p, "SB");
    const char *lb_s   = xml_pull_attr(p, "lb");
    const char *incb_s = xml_pull_attr(p, "InCB");

    uint32_t first_cp, last_cp;
    if (cp_s) {
        first_cp = last_cp = parse_hex(cp_s);
    } else if (first && last) {
        first_cp = parse_hex(first);
        last_cp  = parse_hex(last);
    } else {
        return;
    }
    if (last_cp >= UCD_CP_COUNT) last_cp = UCD_CP_COUNT - 1;

    uint8_t gcb  = parse_gcb(gcb_s);
    uint8_t wb   = parse_wb(wb_s);
    uint8_t sb   = parse_sb(sb_s);
    uint8_t lb   = parse_lb(lb_s);
    uint8_t incb = parse_incb(incb_s);

    for (uint32_t cp = first_cp; cp <= last_cp; cp++) {
        g_gcb[cp]  = gcb;
        g_wb[cp]   = wb;
        g_sb[cp]   = sb;
        g_lb[cp]   = lb;
        g_incb[cp] = incb;
    }
}

static int walk_xml(const char *xml_path)
{
    lh_input in;
    if (lh_input_open(&in, xml_path) != 0) {
        fprintf(stderr, "open %s: %s\n", xml_path, strerror(errno));
        return -1;
    }

    static char scratch[1u << 20];
    xml_pull p;
    xml_pull_init(&p, in.bytes, in.len, scratch, sizeof(scratch));

    int in_repertoire = 0;
    long char_count = 0, range_count = 0;

    for (;;) {
        xml_evt_kind k = xml_pull_next(&p);
        if (k == XML_EVT_EOF) break;
        if (k == XML_EVT_ERROR) {
            fprintf(stderr, "xml error at byte %zu: %s\n",
                    p.err_pos, p.err_msg);
            lh_input_close(&in);
            return -1;
        }
        if (k == XML_EVT_TEXT) continue;

        if (k == XML_EVT_START_ELEM) {
            if (strcmp(p.elem_name, "repertoire") == 0) {
                in_repertoire = 1;
            } else if (in_repertoire && (
                       strcmp(p.elem_name, "char") == 0 ||
                       strcmp(p.elem_name, "reserved") == 0 ||
                       strcmp(p.elem_name, "noncharacter") == 0 ||
                       strcmp(p.elem_name, "surrogate") == 0)) {
                apply_leaf(&p, p.elem_name);
                if (xml_pull_attr(&p, "first-cp")) range_count++;
                else                                char_count++;
            }
        } else if (k == XML_EVT_END_ELEM) {
            if (strcmp(p.elem_name, "repertoire") == 0) in_repertoire = 0;
        }
    }

    fprintf(stderr, "[gen_ucd_flat] %ld single-cp records, %ld range records\n",
            char_count, range_count);

    lh_input_close(&in);
    return 0;
}

static int emit_header(const char *out_dir)
{
    lh_emit e;
    if (lh_emit_open_header(&e, out_dir, "pg_ucd_segmentation") != 0) return -1;
    lh_emit_printf(&e,
        "/* GENERATED by codegen/gen_ucd_flat from ucd.all.flat.xml. DO NOT EDIT. */\n"
        "#ifndef PG_UCD_SEGMENTATION_H\n"
        "#define PG_UCD_SEGMENTATION_H\n"
        "#include <stdint.h>\n"
        "#include \"pg_unicode_version.h\"\n"
        "\n");

    /* GCB defines */
    for (size_t i = 0; i < GCB_COUNT; i++) {
        lh_emit_printf(&e, "#define UC_GCB_%s  %zu\n", GCB_NAMES[i], i);
    }
    /* WB defines */
    for (size_t i = 0; i < WB_COUNT; i++) {
        lh_emit_printf(&e, "#define UC_WB_%s  %zu\n", WB_NAMES[i], i);
    }
    /* SB defines */
    for (size_t i = 0; i < SB_COUNT; i++) {
        lh_emit_printf(&e, "#define UC_SB_%s  %zu\n", SB_NAMES[i], i);
    }
    /* LB defines */
    for (size_t i = 0; i < LB_COUNT; i++) {
        lh_emit_printf(&e, "#define UC_LB_%s  %zu\n", LB_NAMES[i], i);
    }
    /* INCB defines */
    for (size_t i = 0; i < INCB_COUNT; i++) {
        lh_emit_printf(&e, "#define UC_INCB_%s  %zu\n", INCB_NAMES[i], i);
    }

    lh_emit_printf(&e,
        "\n"
        "extern const uint8_t uc_gcb [UNICODE_CODEPOINT_MAX];\n"
        "extern const uint8_t uc_wb  [UNICODE_CODEPOINT_MAX];\n"
        "extern const uint8_t uc_sb  [UNICODE_CODEPOINT_MAX];\n"
        "extern const uint8_t uc_lb  [UNICODE_CODEPOINT_MAX];\n"
        "extern const uint8_t uc_incb[UNICODE_CODEPOINT_MAX];\n"
        "#endif\n");

    return lh_emit_close(&e);
}

static int emit_table(lh_emit *e, const char *name, const uint8_t *data)
{
    lh_emit_printf(e, "const uint8_t %s[%u] = {\n", name, UCD_CP_COUNT);
    for (uint32_t cp = 0; cp < UCD_CP_COUNT; cp++) {
        const char *sep = (cp + 1 == UCD_CP_COUNT) ? "" : ",";
        if ((cp & 0x1F) == 0) lh_emit_printf(e, "    ");
        lh_emit_printf(e, "%4u%s", (unsigned)data[cp], sep);
        if ((cp & 0x1F) == 0x1F) lh_emit_printf(e, "\n");
        else if (cp + 1 != UCD_CP_COUNT) lh_emit_printf(e, " ");
    }
    lh_emit_printf(e, "};\n");
    return 0;
}

static int emit_source(const char *out_dir)
{
    lh_emit e;
    if (lh_emit_open_source(&e, out_dir, "pg_ucd_segmentation") != 0) return -1;
    /* Disable rollover: matches the Python generator's single-TU output. */
    lh_emit_set_max_part_bytes(&e, (size_t)SIZE_MAX / 2);

    lh_emit_printf(&e,
        "/* GENERATED by codegen/gen_ucd_flat from ucd.all.flat.xml. DO NOT EDIT. */\n"
        "#include \"pg_ucd_segmentation.h\"\n\n");

    if (emit_table(&e, "uc_gcb",  g_gcb)  != 0) return -1;
    lh_emit_printf(&e, "\n");
    if (emit_table(&e, "uc_wb",   g_wb)   != 0) return -1;
    lh_emit_printf(&e, "\n");
    if (emit_table(&e, "uc_sb",   g_sb)   != 0) return -1;
    lh_emit_printf(&e, "\n");
    if (emit_table(&e, "uc_lb",   g_lb)   != 0) return -1;
    lh_emit_printf(&e, "\n");
    if (emit_table(&e, "uc_incb", g_incb) != 0) return -1;

    return lh_emit_close(&e);
}

static int run(const char *xml_path, const char *out_dir)
{
    g_gcb  = (uint8_t *)calloc(UCD_CP_COUNT, 1);
    g_wb   = (uint8_t *)calloc(UCD_CP_COUNT, 1);
    g_sb   = (uint8_t *)calloc(UCD_CP_COUNT, 1);
    g_lb   = (uint8_t *)calloc(UCD_CP_COUNT, 1);
    g_incb = (uint8_t *)calloc(UCD_CP_COUNT, 1);
    if (!g_gcb || !g_wb || !g_sb || !g_lb || !g_incb) {
        fprintf(stderr, "calloc failed\n");
        return -1;
    }

    if (walk_xml(xml_path) != 0) goto fail;
    if (emit_header(out_dir) != 0) goto fail;
    if (emit_source(out_dir) != 0) goto fail;

    free(g_gcb); free(g_wb); free(g_sb); free(g_lb); free(g_incb);
    return 0;

fail:
    free(g_gcb); free(g_wb); free(g_sb); free(g_lb); free(g_incb);
    return -1;
}

int main(int argc, char **argv)
{
    if (argc != 3) {
        fprintf(stderr, "usage: %s <ucd.all.flat.xml> <out_dir>\n", argv[0]);
        return 2;
    }
    return run(argv[1], argv[2]) == 0 ? 0 : 1;
}
