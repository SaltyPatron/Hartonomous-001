# Text Recomposer

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Engineers implementing the text recompose pipeline, anyone designing recipes that produce text output, anyone debugging round-trip text fidelity.

---

## What this is

The text recomposer takes substrate state — a composition entity (document, paragraph, sentence, word_form) or a constructed traversal output — and produces material text bytes (UTF-8). It is the inverse of the text decomposer (`20-technical/02-text-decomposer.md`).

Every step of the text decomposer is reversed:

| Decomposer step | Recomposer step |
|---|---|
| Encoding detection → UTF-8 | (UTF-8 is the substrate's canonical form; output is UTF-8) |
| Codepoint decode | Codepoint encode (UTF-8 byte sequence per codepoint) |
| NFC normalization | Optionally re-NFD if the recipe requests; default is NFC output |
| Atom emission | Atom dereference (codepoint atom → UTF-8 bytes) |
| UAX#29 grapheme segmentation | Grapheme cluster traversal |
| UAX#29 word/sentence segmentation | Word/sentence reassembly with appropriate separators |

The recomposer's primary guarantee is **round-trip fidelity**: decompose(text) → substrate; recompose(substrate) → text'. For text that was already in NFC and uses no encoding-specific features, text == text'. For text with normalization or formatting differences, the recomposer's recipe controls fidelity-vs-canonical-form tradeoffs.

## Inputs

The recomposer accepts:

- A composition entity ID (any text-modality composition: document, paragraph, sentence, word_form).
- A recipe specifying:
  - Output normalization form (NFC default; NFD, NFKC, NFKD opt-in).
  - Word/sentence boundary handling (use the substrate's segmentation, or use input's; default is substrate's).
  - Whitespace policy (collapse multiple spaces, preserve original, etc.).
  - Cite-source rendering (insert provenance markers inline; default: no markers).
  - Output encoding (UTF-8 default; UTF-16, UTF-32 opt-in for legacy consumers).
  - Linebreak policy (LF default; CRLF for Windows; CR for legacy macOS).

## Outputs

- A UTF-8 byte stream (or other encoding if requested).
- An optional metadata sidecar JSON describing the recompose pass (composition_id, recipe used, character count, byte count, audit trace ID).

## Pipeline

### Step 1 — composition traversal

Walk the composition tree from the root entity:
- Document → paragraphs → sentences → word_forms → grapheme_clusters → codepoints (atoms).

The traversal uses the bulk-fetch SPI for performance (each step fetches all children in one call; see `20-technical/23-astar-bulk-fetch-spi.md`).

### Step 2 — codepoint atom dereference

For each leaf codepoint atom, fetch its `codepoint_value` (uint32). Encode to UTF-8 bytes per Unicode standard:
- 0x00–0x7F: 1 byte.
- 0x80–0x7FF: 2 bytes.
- 0x800–0xFFFF: 3 bytes.
- 0x10000–0x10FFFF: 4 bytes.
- Surrogates (0xD800–0xDFFF): rejected (substrate doesn't store these as atoms).

### Step 3 — grapheme cluster reassembly

Concatenate the codepoint UTF-8 bytes in the cluster's `composed_of_codepoint` ordinal order. For NFC clusters, the result is the cluster's canonical NFC byte sequence.

If the recipe requests NFD output and the cluster has a `nfc_normalization_of` edge to an NFD source, traverse to that source and emit its byte sequence instead. If no NFD source exists in substrate, the recomposer DOES NOT INVENT one — it can either:
- Compute NFD from NFC via Unicode's canonical decomposition algorithm (this is a well-defined algorithm, not invention), OR
- Emit NFC and flag the recipe-vs-substrate mismatch in the audit trace.

The choice is recipe-driven; default is "compute NFD from NFC if NFD is requested but not stored."

### Step 4 — word reassembly

Concatenate grapheme clusters within a word_form in their `composed_of_grapheme_cluster` ordinal order. No inter-cluster separator (clusters concatenate directly into the word's UTF-8 bytes).

### Step 5 — sentence reassembly

Concatenate word_forms within a sentence in their `composed_of_word_form` ordinal order. Inter-word separator depends on the language:
- For most languages (Latin script, Cyrillic, etc.): single space U+0020 between words.
- For Chinese, Japanese, Thai, etc. (no inter-word space): no separator.
- The substrate determines this from the language metadata of the parent sentence.

For sentences with non-trivial intra-sentence punctuation (commas, periods, etc.), the punctuation appears as its own word_form constituents, so reassembly is naturally correct.

### Step 6 — paragraph reassembly

Concatenate sentences within a paragraph with the recipe's sentence-separator (default: single space). Sentence-final punctuation is part of the sentence's word_form constituents, so it's included automatically.

### Step 7 — document reassembly

Concatenate paragraphs with the recipe's paragraph-separator (default: two newlines for "blank line between paragraphs," matching common Markdown/plain-text convention).

### Step 8 — output

Emit the byte stream. If a metadata sidecar is requested, write it alongside.

## Cite-source rendering

When the recipe requests inline citations, the recomposer inserts source markers at appropriate boundaries:

```
The metformin-PCOS connection [DrugBank, Wiktionary, NEJM-2024] was first
proposed in the 1990s [Wikipedia-Medical-History].
```

Citation markers are computed by:
1. For each output composition, traverse its provenance edges.
2. Aggregate distinct provenance sources.
3. Render as `[source_1, source_2, ...]` after the relevant span.

The granularity (per-word, per-sentence, per-paragraph) is recipe-controlled. Default is per-sentence — coarse enough not to clutter; fine enough to be informative.

## Round-trip fidelity guarantee

For text that originated as a substrate ingestion:

- The decomposer's NFC step produces canonical NFC for the substrate.
- The recomposer's NFC output emits the same NFC.
- ⇒ decompose(text) → substrate → recompose() = NFC(text).

If `text` was already in NFC, then `recompose(decompose(text)) == text` byte-for-byte.

If `text` was NOT in NFC (e.g., NFD source), then `recompose(decompose(text)) == NFC(text)`. The recomposer can be configured to output NFD, in which case `recompose(decompose(text)) == NFD(NFC(text))` which equals `NFD(text)` if `text` was in NFD, or `NFD(NFC(text))` otherwise.

These guarantees are tested by the round-trip validation gate (`40-process/02-validation-gates.md`).

## Encoding fidelity

The substrate stores codepoints (Unicode-standard) regardless of source encoding. The recomposer outputs UTF-8 by default. Legacy encodings (Latin-1, Shift-JIS, GB2312, etc.) are NOT preserved at the byte level — the substrate is Unicode-from-day-one (Substrate Law 7).

Recipes that need legacy encoding for output explicitly request it:

```jsonc
{
  "output_encoding": "shift_jis",
  "output_encoding_error_policy": "fail_loud" // | "replace" | "skip"
}
```

The recomposer encodes the UTF-8 string to the target encoding. Codepoints not representable in the target encoding trigger the error policy: fail_loud raises an error; replace substitutes a placeholder; skip omits the character. Substrate Law 13 prefers fail_loud as default.

## Performance

| Operation | Performance |
|---|---|
| Codepoint dereference | ~1 μs per codepoint (atom lookup) |
| Grapheme cluster assembly | ~5 μs per cluster |
| Sentence reassembly | ~50 μs per sentence (typical 10-20 words) |
| Paragraph reassembly | ~500 μs per paragraph |
| Document reassembly | proportional to total codepoint count |

For a typical document (10 paragraphs, 100 sentences, 2K words, 10K codepoints), recompose takes ~50 ms. Bulk-recompose of many compositions amortizes via batch SPI.

## Recipe-driven variations

### Plain text output

```jsonc
{
  "kind": "recompose",
  "target_format": "plain_text",
  "output_encoding": "utf-8",
  "normalization": "nfc",
  "linebreaks": "lf",
  "include_citations": false
}
```

### Cited markdown output

```jsonc
{
  "kind": "recompose",
  "target_format": "markdown",
  "output_encoding": "utf-8",
  "normalization": "nfc",
  "include_citations": true,
  "citation_granularity": "sentence",
  "citation_style": "footnote_marker",
  "footnote_format": "[^N]"
}
```

This produces markdown with footnote-style citations and a footnote section at the end.

### LaTeX output

```jsonc
{
  "kind": "recompose",
  "target_format": "latex",
  "output_encoding": "utf-8",
  "include_citations": true,
  "citation_style": "biblatex",
  "tex_special_char_escape": true
}
```

LaTeX output escapes special characters (\, &, %, etc.) and renders citations using biblatex `\cite{}` commands. The substrate emits a sibling `.bib` file with the bibliography entries.

### Plaintext with provenance sidecar

```jsonc
{
  "kind": "recompose",
  "target_format": "plain_text",
  "metadata_sidecar": true,
  "metadata_sidecar_format": "json",
  "metadata_sidecar_includes": ["audit_chain", "provenance_summary", "composition_id"]
}
```

Outputs the text as one stream and a sidecar JSON with detailed provenance and audit information. Useful for compliance scenarios.

## Cross-references

- Text decomposer (the inverse): `20-technical/02-text-decomposer.md`
- Recomposer contract (general principles): `10-architecture/06-recomposer-contract.md`
- Cognitive functions (how recompose is invoked): `20-technical/08-cognitive-functions.md`
- Audit chain (provenance rendering): `10-architecture/17-audit-chain.md`
- Substrate Law 7 (Unicode-from-day-one): `10-architecture/01-substrate-laws.md`
