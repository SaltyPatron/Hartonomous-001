/*
 * gen_ucd_grouped — read ucd.all.grouped.xml, emit per-codepoint
 * properties into lh_ucd_props.{h,c}.
 *
 * Initial scope: General Category (gc). The XML is walked once and the
 * `gc` value of every assigned codepoint (and the "Cn" default for every
 * unassigned slot) is stored in a flat uint8_t[0x110000] table indexed
 * by codepoint. Reserved / noncharacter / surrogate ranges fill the
 * appropriate slots with Cn / Cn / Cs respectively, matching the UCD
 * convention.
 *
 * The XML uses the grouped-default form: <group …attrs…> contains
 * <char>/<reserved>/<noncharacter>/<surrogate> children that inherit
 * any attribute not locally overridden. Both single-codepoint
 * (cp="HEX") and range (first-cp="HEX" last-cp="HEX") forms appear.
 *
 * Subsequent expansions will add more attributes to the emitted
 * struct using the same parser harness — the cost of additional
 * properties is one more table per attribute, no parser change.
 *
 * Usage:
 *   gen_ucd_grouped <ucd.all.grouped.xml> <out_dir>
 */

#include "lh_emit.h"
#include "lh_input.h"
#include "xml_pull.h"

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ── General Category enumeration ───────────────────────────────────────
 * Numbering chosen for compactness; values are stable (callers should
 * use the LH_GC_* constants emitted into lh_ucd_props.h, not raw ints).
 * */
typedef enum lh_gc {
    LH_GC_Cn = 0,  /* Unassigned (default) */
    LH_GC_Lu, LH_GC_Ll, LH_GC_Lt, LH_GC_Lm, LH_GC_Lo,
    LH_GC_Mn, LH_GC_Mc, LH_GC_Me,
    LH_GC_Nd, LH_GC_Nl, LH_GC_No,
    LH_GC_Pc, LH_GC_Pd, LH_GC_Ps, LH_GC_Pe, LH_GC_Pi, LH_GC_Pf, LH_GC_Po,
    LH_GC_Sm, LH_GC_Sc, LH_GC_Sk, LH_GC_So,
    LH_GC_Zs, LH_GC_Zl, LH_GC_Zp,
    LH_GC_Cc, LH_GC_Cf, LH_GC_Cs, LH_GC_Co,
    LH_GC_COUNT
} lh_gc;

static const char *const GC_NAMES[LH_GC_COUNT] = {
    "Cn", "Lu","Ll","Lt","Lm","Lo",
    "Mn","Mc","Me", "Nd","Nl","No",
    "Pc","Pd","Ps","Pe","Pi","Pf","Po",
    "Sm","Sc","Sk","So", "Zs","Zl","Zp",
    "Cc","Cf","Cs","Co",
};

static lh_gc parse_gc(const char *s)
{
    if (!s) return LH_GC_Cn;
    for (int i = 0; i < LH_GC_COUNT; i++) {
        if (strcmp(s, GC_NAMES[i]) == 0) return (lh_gc)i;
    }
    /* Fallback to Cn for unknown values (should not occur in valid UCD). */
    fprintf(stderr, "warning: unknown gc=%s\n", s);
    return LH_GC_Cn;
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

/* ── Storage ────────────────────────────────────────────────────────── */

#define LH_CP_COUNT 0x110000u

static uint8_t *g_gc; /* indexed by codepoint */

static void set_gc_range(uint32_t first, uint32_t last, lh_gc gc)
{
    if (last >= LH_CP_COUNT) last = LH_CP_COUNT - 1;
    for (uint32_t cp = first; cp <= last; cp++) {
        g_gc[cp] = (uint8_t)gc;
    }
}

/* ── XML walk ───────────────────────────────────────────────────────── */

/* Group-level default attributes (subset we currently emit). Only `gc` so far.
 * As more properties land, this struct grows. */
typedef struct group_defaults {
    int has_gc;
    lh_gc gc;
} group_defaults;

static void apply_group_defaults(group_defaults *gd, xml_pull *p)
{
    const char *gc = xml_pull_attr(p, "gc");
    if (gc) { gd->has_gc = 1; gd->gc = parse_gc(gc); }
}

/* Apply a leaf record's effective gc to the codepoint range. The leaf may
 * be `<char>`, `<reserved>`, `<noncharacter>`, or `<surrogate>`. Reserved
 * and noncharacter default to Cn; surrogate defaults to Cs. */
static void apply_leaf(const group_defaults *gd, xml_pull *p,
                       const char *kind)
{
    const char *cp_s   = xml_pull_attr(p, "cp");
    const char *first  = xml_pull_attr(p, "first-cp");
    const char *last   = xml_pull_attr(p, "last-cp");
    const char *gc_loc = xml_pull_attr(p, "gc");

    uint32_t first_cp, last_cp;
    if (cp_s) {
        first_cp = last_cp = parse_hex(cp_s);
    } else if (first && last) {
        first_cp = parse_hex(first);
        last_cp  = parse_hex(last);
    } else {
        return;
    }

    lh_gc gc;
    if (gc_loc) {
        gc = parse_gc(gc_loc);
    } else if (gd->has_gc) {
        gc = gd->gc;
    } else if (strcmp(kind, "surrogate") == 0) {
        gc = LH_GC_Cs;
    } else {
        gc = LH_GC_Cn;
    }
    set_gc_range(first_cp, last_cp, gc);
}

static int run(const char *xml_path, const char *out_dir)
{
    lh_input in;
    if (lh_input_open(&in, xml_path) != 0) {
        fprintf(stderr, "open %s: %s\n", xml_path, strerror(errno));
        return -1;
    }

    /* Default every codepoint to Cn before walking; assigned ranges overwrite. */
    g_gc = (uint8_t *)calloc(LH_CP_COUNT, 1);
    if (!g_gc) { lh_input_close(&in); return -1; }

    /* 1 MiB of scratch is plenty for the longest attribute set we see. */
    static char scratch[1u << 20];
    xml_pull p;
    xml_pull_init(&p, in.bytes, in.len, scratch, sizeof(scratch));

    int in_repertoire = 0;
    int group_depth = 0;
    group_defaults gd = {0};
    long char_count = 0, range_count = 0;

    for (;;) {
        xml_evt_kind k = xml_pull_next(&p);
        if (k == XML_EVT_EOF) break;
        if (k == XML_EVT_ERROR) {
            fprintf(stderr, "xml error at byte %zu: %s\n",
                    p.err_pos, p.err_msg);
            free(g_gc);
            lh_input_close(&in);
            return -1;
        }
        if (k == XML_EVT_TEXT) continue;

        if (k == XML_EVT_START_ELEM) {
            if (strcmp(p.elem_name, "repertoire") == 0) {
                in_repertoire = 1;
            } else if (in_repertoire && strcmp(p.elem_name, "group") == 0) {
                group_depth++;
                memset(&gd, 0, sizeof(gd));
                apply_group_defaults(&gd, &p);
            } else if (in_repertoire && (
                       strcmp(p.elem_name, "char") == 0 ||
                       strcmp(p.elem_name, "reserved") == 0 ||
                       strcmp(p.elem_name, "noncharacter") == 0 ||
                       strcmp(p.elem_name, "surrogate") == 0)) {
                apply_leaf(&gd, &p, p.elem_name);
                if (xml_pull_attr(&p, "first-cp")) range_count++;
                else                                char_count++;
            }
        } else if (k == XML_EVT_END_ELEM) {
            if (strcmp(p.elem_name, "repertoire") == 0) in_repertoire = 0;
            else if (strcmp(p.elem_name, "group") == 0) {
                if (group_depth > 0) group_depth--;
            }
        }
    }

    fprintf(stderr, "[gen_ucd_grouped] %ld single-cp records, %ld range records\n",
            char_count, range_count);

    /* ── Emit lh_ucd_props.h ───────────────────────────────────────── */
    {
        lh_emit e;
        if (lh_emit_open_header(&e, out_dir, "lh_ucd_props") != 0) {
            free(g_gc); lh_input_close(&in); return -1;
        }
        lh_emit_printf(&e,
            "/* GENERATED by codegen/gen_ucd_grouped. DO NOT EDIT. */\n"
            "\n"
            "#ifndef LH_UCD_PROPS_H\n"
            "#define LH_UCD_PROPS_H\n"
            "\n"
            "#include <stdint.h>\n"
            "\n"
            "#define LH_UCD_CP_COUNT 0x110000u\n"
            "\n"
            "/* General_Category enumeration (Unicode 17.0.0). Stable values. */\n"
            "typedef enum {\n");
        for (int i = 0; i < LH_GC_COUNT; i++) {
            lh_emit_printf(&e, "    LH_GC_%s = %d,\n", GC_NAMES[i], i);
        }
        lh_emit_printf(&e,
            "    LH_GC_COUNT = %d\n"
            "} lh_gc;\n"
            "\n"
            "extern const char *const lh_gc_names[LH_GC_COUNT];\n"
            "\n"
            "/* General_Category indexed by Unicode scalar value 0..0x10FFFF. */\n"
            "extern const uint8_t lh_ucd_gc[LH_UCD_CP_COUNT];\n"
            "\n"
            "static inline lh_gc lh_ucd_gc_of(uint32_t cp) {\n"
            "    return cp < LH_UCD_CP_COUNT ? (lh_gc)lh_ucd_gc[cp] : LH_GC_Cn;\n"
            "}\n"
            "\n"
            "#endif /* LH_UCD_PROPS_H */\n", LH_GC_COUNT);
        if (lh_emit_close(&e) != 0) { free(g_gc); lh_input_close(&in); return -1; }
    }

    /* ── Emit lh_ucd_props.c ───────────────────────────────────────── */
    {
        lh_emit e;
        if (lh_emit_open_source(&e, out_dir, "lh_ucd_props") != 0) {
            free(g_gc); lh_input_close(&in); return -1;
        }
        lh_emit_printf(&e,
            "/* GENERATED by codegen/gen_ucd_grouped. DO NOT EDIT. */\n"
            "\n"
            "#include \"lh_ucd_props.h\"\n"
            "\n"
            "const char *const lh_gc_names[LH_GC_COUNT] = {\n");
        for (int i = 0; i < LH_GC_COUNT; i++) {
            lh_emit_printf(&e, "    \"%s\"%s\n", GC_NAMES[i],
                           i + 1 < LH_GC_COUNT ? "," : "");
        }
        lh_emit_printf(&e,
            "};\n"
            "\n"
            "const uint8_t lh_ucd_gc[LH_UCD_CP_COUNT] = {\n");

        for (uint32_t cp = 0; cp < LH_CP_COUNT; cp++) {
            const char *sep = (cp + 1 == LH_CP_COUNT) ? "" : ",";
            if ((cp & 0x0F) == 0) lh_emit_printf(&e, "    /* U+%06X */ ", cp);
            lh_emit_printf(&e, "%u%s", (unsigned)g_gc[cp], sep);
            if ((cp & 0x0F) == 0x0F) lh_emit_printf(&e, "\n");
        }
        lh_emit_printf(&e, "};\n");
        if (lh_emit_close(&e) != 0) { free(g_gc); lh_input_close(&in); return -1; }
    }

    free(g_gc);
    lh_input_close(&in);
    return 0;
}

int main(int argc, char **argv)
{
    if (argc != 3) {
        fprintf(stderr, "usage: %s <ucd.all.grouped.xml> <out_dir>\n", argv[0]);
        return 2;
    }
    return run(argv[1], argv[2]) == 0 ? 0 : 1;
}
