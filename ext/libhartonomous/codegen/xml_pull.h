/*
 * xml_pull.h — minimal XML 1.0 pull parser for UCD/Unihan grouped XML.
 *
 * Scope (sufficient for the documents we read, deliberately small):
 *
 *   - UTF-8 input (UCD XML is always UTF-8).
 *   - Elements with attributes (single or double quoted).
 *   - Self-closing elements <foo/>.
 *   - Empty-content elements with start/end tags.
 *   - <?xml …?> processing instructions (skipped).
 *   - <!-- comments --> (skipped).
 *   - <!DOCTYPE …> (skipped, including internal subset between [ and ]).
 *   - <![CDATA[ … ]]> sections delivered verbatim as text events.
 *   - Numeric (&#NNN; &#xHEX;) and the 5 named character references
 *     (&amp; &lt; &gt; &apos; &quot;) inside attribute values and text.
 *
 * Out of scope (the UCD grouped XML does not use any of these):
 *
 *   - DTD validation, external entity resolution, parameter entities.
 *   - User-defined entity declarations.
 *   - Mixed-encoding input (UTF-16, etc.).
 *   - Namespaces — qualified names are returned as-is.
 *
 * Pull model: caller drives `xml_pull_next` until it returns
 * `XML_EVT_EOF`. Each event refers to internal storage that is valid only
 * until the next call. Attribute names/values are pre-decoded.
 *
 * No allocations beyond a single caller-provided scratch buffer.
 */

#ifndef LH_XML_PULL_H
#define LH_XML_PULL_H

#include <stddef.h>
#include <stdint.h>

typedef enum xml_evt_kind {
    XML_EVT_NONE        = 0,
    XML_EVT_START_ELEM  = 1,  /* <name attrs…> */
    XML_EVT_END_ELEM    = 2,  /* </name> or self-close </ */
    XML_EVT_TEXT        = 3,  /* character data (decoded entities) */
    XML_EVT_EOF         = 4,
    XML_EVT_ERROR       = -1
} xml_evt_kind;

typedef struct xml_attr {
    const char *name;     /* NUL-terminated, valid until next event */
    const char *value;    /* NUL-terminated, entity-decoded */
} xml_attr;

/* CJK Unihan codepoints in ucd.all.flat.xml carry many kIRG_-prefixed and
 * kHanYu / kCantonese / etc. attributes. The largest entries observed in
 * UCD 17.0.0 push past 160; 256 gives headroom and stays in stack budget. */
#define XML_MAX_ATTRS 256

typedef struct xml_pull {
    /* input */
    const uint8_t *src;
    size_t src_len;
    size_t pos;

    /* scratch buffer (caller-provided) */
    char  *scratch;
    size_t scratch_cap;
    size_t scratch_used;

    /* current event */
    xml_evt_kind evt;
    const char *elem_name;   /* for START/END */
    int          self_close; /* for START — also fires END at next pull */
    xml_attr     attrs[XML_MAX_ATTRS];
    int          attr_count;
    const char  *text;       /* for TEXT */
    size_t       text_len;

    /* error reporting */
    const char *err_msg;
    size_t      err_pos;

    /* internal: pending end-of-self-closing-element to emit */
    const char *pending_end_name;
} xml_pull;

/*
 * Initialise a parser over an in-memory document. `scratch`/`scratch_cap`
 * are used to stage decoded attribute values and text. 256 KiB is plenty
 * for the longest <name="…"/> attribute we encounter in UCD XML.
 */
void xml_pull_init(xml_pull *p,
                   const uint8_t *src, size_t src_len,
                   char *scratch, size_t scratch_cap);

/* Returns the kind of the next event. Stores details on `p`. */
xml_evt_kind xml_pull_next(xml_pull *p);

/* Convenience: look up an attribute on the current START_ELEM event. */
const char *xml_pull_attr(const xml_pull *p, const char *name);

#endif /* LH_XML_PULL_H */
