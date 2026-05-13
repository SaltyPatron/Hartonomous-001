/*
 * xml_pull.c — hand-written XML 1.0 pull parser.
 *
 * See xml_pull.h for scope. The implementation is intentionally simple
 * and direct: a single pass over `src`, decoding attributes and text into
 * the caller's scratch buffer. No DOM, no allocations, no callbacks.
 */

#include "xml_pull.h"

#include <stdint.h>
#include <string.h>

/* ── Error helpers ───────────────────────────────────────────────────── */

static xml_evt_kind xml_fail(xml_pull *p, const char *msg)
{
    p->evt = XML_EVT_ERROR;
    p->err_msg = msg;
    p->err_pos = p->pos;
    return XML_EVT_ERROR;
}

/* ── Scratch buffer ──────────────────────────────────────────────────── */

static char *scratch_alloc(xml_pull *p, size_t n)
{
    if (p->scratch_used + n > p->scratch_cap) return NULL;
    char *out = p->scratch + p->scratch_used;
    p->scratch_used += n;
    return out;
}

static void scratch_reset(xml_pull *p)
{
    p->scratch_used = 0;
}

/* ── Low-level byte cursor ───────────────────────────────────────────── */

static int peek(xml_pull *p) { return p->pos < p->src_len ? p->src[p->pos] : -1; }
static int eat(xml_pull *p) { return p->pos < p->src_len ? p->src[p->pos++] : -1; }

static int starts_with(xml_pull *p, const char *needle)
{
    size_t n = strlen(needle);
    if (p->pos + n > p->src_len) return 0;
    return memcmp(p->src + p->pos, needle, n) == 0;
}

static void skip_ws(xml_pull *p)
{
    while (p->pos < p->src_len) {
        uint8_t c = p->src[p->pos];
        if (c == ' ' || c == '\t' || c == '\r' || c == '\n') p->pos++;
        else break;
    }
}

/* XML 1.0 NameStartChar / NameChar — implemented per ABNF subset that
 * covers ASCII names plus the codepoint ranges UCD/Unihan use. We do not
 * need the full Unicode 5.0 NameStartChar table: UCD XML names are all
 * ASCII letters / digits / `-` / `.` / `_` / `:`. */

static int is_name_start(int c)
{
    if (c < 0) return 0;
    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_' || c == ':';
}

static int is_name_char(int c)
{
    if (c < 0) return 0;
    return is_name_start(c) || (c >= '0' && c <= '9') || c == '-' || c == '.';
}

/* ── Entity decoding ─────────────────────────────────────────────────── */

/* Encode codepoint cp into UTF-8 at *out, advancing it. Returns bytes written
 * or 0 on invalid codepoint. Caller is responsible for buffer space. */
static int utf8_encode(uint32_t cp, char *out)
{
    if (cp <= 0x7Fu) { out[0] = (char)cp; return 1; }
    if (cp <= 0x7FFu) {
        out[0] = (char)(0xC0u | (cp >> 6));
        out[1] = (char)(0x80u | (cp & 0x3Fu));
        return 2;
    }
    if (cp <= 0xFFFFu) {
        if (cp >= 0xD800u && cp <= 0xDFFFu) return 0;
        out[0] = (char)(0xE0u | (cp >> 12));
        out[1] = (char)(0x80u | ((cp >> 6) & 0x3Fu));
        out[2] = (char)(0x80u | (cp & 0x3Fu));
        return 3;
    }
    if (cp <= 0x10FFFFu) {
        out[0] = (char)(0xF0u | (cp >> 18));
        out[1] = (char)(0x80u | ((cp >> 12) & 0x3Fu));
        out[2] = (char)(0x80u | ((cp >> 6) & 0x3Fu));
        out[3] = (char)(0x80u | (cp & 0x3Fu));
        return 4;
    }
    return 0;
}

/* On entry: p->pos points at '&'. On success advances past ';' and writes
 * the decoded UTF-8 bytes to *out (advancing *out_len). */
static int decode_entity(xml_pull *p, char *out_buf, size_t out_cap, size_t *out_len)
{
    if (peek(p) != '&') return 0;
    p->pos++; /* consume '&' */

    /* Numeric: &#NN;  or  &#xHH; */
    if (peek(p) == '#') {
        p->pos++;
        uint32_t cp = 0;
        int hex = 0;
        if (peek(p) == 'x' || peek(p) == 'X') { hex = 1; p->pos++; }
        int any = 0;
        while (p->pos < p->src_len && p->src[p->pos] != ';') {
            int c = p->src[p->pos++];
            if (hex) {
                if (c >= '0' && c <= '9') cp = (cp << 4) | (uint32_t)(c - '0');
                else if (c >= 'a' && c <= 'f') cp = (cp << 4) | (uint32_t)(c - 'a' + 10);
                else if (c >= 'A' && c <= 'F') cp = (cp << 4) | (uint32_t)(c - 'A' + 10);
                else return -1;
            } else {
                if (c < '0' || c > '9') return -1;
                cp = cp * 10u + (uint32_t)(c - '0');
            }
            any = 1;
            if (cp > 0x10FFFFu) return -1;
        }
        if (!any || peek(p) != ';') return -1;
        p->pos++; /* consume ';' */
        if (*out_len + 4 > out_cap) return -1;
        int n = utf8_encode(cp, out_buf + *out_len);
        if (n <= 0) return -1;
        *out_len += (size_t)n;
        return 0;
    }

    /* Named: amp, lt, gt, apos, quot */
    static const struct { const char *name; const char *bytes; } table[] = {
        { "amp",  "&"  },
        { "lt",   "<"  },
        { "gt",   ">"  },
        { "apos", "'"  },
        { "quot", "\"" },
    };
    for (size_t i = 0; i < sizeof(table) / sizeof(table[0]); i++) {
        size_t n = strlen(table[i].name);
        if (p->pos + n + 1 <= p->src_len &&
            memcmp(p->src + p->pos, table[i].name, n) == 0 &&
            p->src[p->pos + n] == ';') {
            p->pos += n + 1;
            if (*out_len + 1 > out_cap) return -1;
            out_buf[(*out_len)++] = table[i].bytes[0];
            return 0;
        }
    }
    return -1;
}

/* ── Attribute parsing ───────────────────────────────────────────────── */

/* Read NUL-terminated attribute name into scratch. Returns name or NULL. */
static const char *parse_name(xml_pull *p)
{
    if (!is_name_start(peek(p))) return NULL;
    char *out = p->scratch + p->scratch_used;
    size_t cap = p->scratch_cap - p->scratch_used;
    if (cap < 2) return NULL;
    size_t n = 0;
    while (is_name_char(peek(p))) {
        if (n + 1 >= cap) return NULL;
        out[n++] = (char)eat(p);
    }
    out[n++] = '\0';
    p->scratch_used += n;
    return out;
}

/* Read quoted attribute value into scratch with entity decoding. */
static const char *parse_attr_value(xml_pull *p)
{
    int q = peek(p);
    if (q != '"' && q != '\'') return NULL;
    p->pos++; /* consume opening quote */

    char *out = p->scratch + p->scratch_used;
    size_t cap = p->scratch_cap - p->scratch_used;
    size_t len = 0;
    while (p->pos < p->src_len) {
        int c = p->src[p->pos];
        if (c == q) { p->pos++; break; }
        if (c == '<') return NULL; /* '<' not allowed in attr values */
        if (c == '&') {
            if (decode_entity(p, out, cap, &len) != 0) return NULL;
        } else {
            if (len + 1 >= cap) return NULL;
            out[len++] = (char)c;
            p->pos++;
        }
    }
    if (len + 1 > cap) return NULL;
    out[len++] = '\0';
    p->scratch_used += len;
    return out;
}

/* ── Skip helpers ────────────────────────────────────────────────────── */

static int skip_until(xml_pull *p, const char *needle)
{
    size_t n = strlen(needle);
    while (p->pos + n <= p->src_len) {
        if (memcmp(p->src + p->pos, needle, n) == 0) {
            p->pos += n;
            return 0;
        }
        p->pos++;
    }
    return -1;
}

/* DOCTYPE may contain an internal subset [...] with nested brackets. */
static int skip_doctype(xml_pull *p)
{
    int depth = 0;
    while (p->pos < p->src_len) {
        int c = p->src[p->pos++];
        if (c == '[') depth++;
        else if (c == ']') depth--;
        else if (c == '>' && depth <= 0) return 0;
    }
    return -1;
}

/* ── Public API ──────────────────────────────────────────────────────── */

void xml_pull_init(xml_pull *p,
                   const uint8_t *src, size_t src_len,
                   char *scratch, size_t scratch_cap)
{
    memset(p, 0, sizeof(*p));
    p->src = src;
    p->src_len = src_len;
    p->scratch = scratch;
    p->scratch_cap = scratch_cap;
    /* Skip a UTF-8 BOM if present. */
    if (src_len >= 3 && src[0] == 0xEF && src[1] == 0xBB && src[2] == 0xBF) {
        p->pos = 3;
    }
}

xml_evt_kind xml_pull_next(xml_pull *p)
{
    /* Emit pending end-of-self-closing element. */
    if (p->pending_end_name) {
        scratch_reset(p);
        p->elem_name = p->pending_end_name;
        p->pending_end_name = NULL;
        p->attr_count = 0;
        p->evt = XML_EVT_END_ELEM;
        return p->evt;
    }

    scratch_reset(p);
    p->attr_count = 0;
    p->elem_name = NULL;
    p->text = NULL;
    p->text_len = 0;
    p->self_close = 0;

    if (p->pos >= p->src_len) {
        p->evt = XML_EVT_EOF;
        return p->evt;
    }

    int c = peek(p);

    /* Markup */
    if (c == '<') {
        /* <?xml … ?> */
        if (starts_with(p, "<?")) {
            p->pos += 2;
            if (skip_until(p, "?>") != 0) return xml_fail(p, "unterminated PI");
            return xml_pull_next(p);
        }
        /* <!-- … --> */
        if (starts_with(p, "<!--")) {
            p->pos += 4;
            if (skip_until(p, "-->") != 0) return xml_fail(p, "unterminated comment");
            return xml_pull_next(p);
        }
        /* <![CDATA[ … ]]> */
        if (starts_with(p, "<![CDATA[")) {
            p->pos += 9;
            size_t start = p->pos;
            const char *end_marker = "]]>";
            size_t end_n = 3;
            while (p->pos + end_n <= p->src_len &&
                   memcmp(p->src + p->pos, end_marker, end_n) != 0) p->pos++;
            if (p->pos + end_n > p->src_len) return xml_fail(p, "unterminated CDATA");
            size_t len = p->pos - start;
            char *out = scratch_alloc(p, len + 1);
            if (!out) return xml_fail(p, "scratch overflow in CDATA");
            memcpy(out, p->src + start, len);
            out[len] = '\0';
            p->pos += end_n;
            p->text = out;
            p->text_len = len;
            p->evt = XML_EVT_TEXT;
            return p->evt;
        }
        /* <!DOCTYPE …> */
        if (starts_with(p, "<!DOCTYPE")) {
            p->pos += 9;
            if (skip_doctype(p) != 0) return xml_fail(p, "unterminated DOCTYPE");
            return xml_pull_next(p);
        }
        /* </name> */
        if (starts_with(p, "</")) {
            p->pos += 2;
            const char *name = parse_name(p);
            if (!name) return xml_fail(p, "expected end tag name");
            skip_ws(p);
            if (eat(p) != '>') return xml_fail(p, "expected '>' on end tag");
            p->elem_name = name;
            p->evt = XML_EVT_END_ELEM;
            return p->evt;
        }
        /* <name attrs…> or <name attrs… /> */
        p->pos++; /* consume '<' */
        const char *name = parse_name(p);
        if (!name) return xml_fail(p, "expected start tag name");
        p->elem_name = name;
        for (;;) {
            skip_ws(p);
            int n = peek(p);
            if (n == '/') {
                p->pos++;
                if (eat(p) != '>') return xml_fail(p, "expected '>' after '/'");
                p->self_close = 1;
                p->pending_end_name = name;
                break;
            }
            if (n == '>') { p->pos++; break; }
            if (!is_name_start(n)) return xml_fail(p, "expected attribute name");
            const char *aname = parse_name(p);
            if (!aname) return xml_fail(p, "bad attribute name");
            skip_ws(p);
            if (eat(p) != '=') return xml_fail(p, "expected '=' after attribute name");
            skip_ws(p);
            const char *aval = parse_attr_value(p);
            if (!aval) return xml_fail(p, "bad attribute value");
            if (p->attr_count >= XML_MAX_ATTRS) return xml_fail(p, "too many attributes");
            p->attrs[p->attr_count].name = aname;
            p->attrs[p->attr_count].value = aval;
            p->attr_count++;
        }
        p->evt = XML_EVT_START_ELEM;
        return p->evt;
    }

    /* Character data — accumulate up to next '<', decoding entities. */
    char *out = p->scratch + p->scratch_used;
    size_t cap = p->scratch_cap - p->scratch_used;
    size_t len = 0;
    while (p->pos < p->src_len && p->src[p->pos] != '<') {
        if (p->src[p->pos] == '&') {
            if (decode_entity(p, out, cap, &len) != 0)
                return xml_fail(p, "bad entity in text");
        } else {
            if (len + 1 >= cap) return xml_fail(p, "scratch overflow in text");
            out[len++] = (char)p->src[p->pos++];
        }
    }
    if (len + 1 > cap) return xml_fail(p, "scratch overflow in text");
    out[len++] = '\0';
    p->scratch_used += len;
    p->text = out;
    p->text_len = len - 1;
    p->evt = XML_EVT_TEXT;
    return p->evt;
}

const char *xml_pull_attr(const xml_pull *p, const char *name)
{
    for (int i = 0; i < p->attr_count; i++) {
        if (strcmp(p->attrs[i].name, name) == 0) return p->attrs[i].value;
    }
    return NULL;
}
