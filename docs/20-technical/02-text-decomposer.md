# Text Decomposer — Universal Text Ingestion Path

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Engineers implementing the text decomposer; every author of a seed decomposer that emits text-bearing content (must route through this); anyone debugging text ingestion issues.

---

## What the text decomposer is

The text decomposer is the substrate's universal entry point for converting raw bytes-that-represent-text into substrate state. Every text-bearing input — user prompts, ingested documents, WordNet glosses, UD sentences, Wiktionary etymology fields, Tatoeba sentence text, safetensors `config.json` values, image captions, audio transcripts, model display names, even individual identifier strings — routes through this single path.

Two non-negotiable consequences flow from this:

1. **Convergence works.** Identical text bytes from any source produce the same `text_composition` entity hash, regardless of which seed decomposer or modality decomposer first encountered them.
2. **Seeds use core.** No seed decomposer is allowed to compute its own hashes for text-bearing strings. They route via the text decomposer's pipeline interface and receive the resulting `text_composition` hash. (Substrate Law 5; AP-9 if violated.)

The text decomposer's correctness — particularly NFC handling, UAX #29 segmentation correctness, and seed-uses-core enforcement — is the most load-bearing piece of substrate's identity layer for textual content. Bugs here cascade everywhere.

## The pipeline, in order

```
input bytes (any encoding)
        │
        ▼
[1] Encoding detection + UTF-8 conversion
        │
        ▼
[2] Codepoint decode (UTF-8 → uint32 codepoint sequence)
        │
        ▼
[3] NFC normalization (Unicode normalization form C)
        │
        ▼
[4] Codepoint atom emission (each codepoint → atom entity if not already in substrate)
        │
        ▼
[5] UAX #29 grapheme cluster segmentation (using GCB property from junc.codepoint_property)
        │
        ▼
[6] Grapheme cluster composition emission (linestring4d through child codepoint S³ positions)
        │
        ▼
[7] UAX #29 word boundary segmentation (using WB property)
        │
        ▼
[8] Word form composition emission (linestring4d through grapheme cluster centroids)
        │
        ▼
[9] UAX #29 sentence boundary segmentation (using SB property)
        │
        ▼
[10] Sentence composition emission (linestring4d through word centroids)
        │
        ▼
[11] Paragraph segmentation (UAX #14 line breaks + blank-line heuristics)
        │
        ▼
[12] Paragraph composition emission (linestring4d through sentence centroids)
        │
        ▼
[13] Document composition emission (linestring4d / multilinestring4d through paragraph centroids)
        │
        ▼
returns: BLAKE3 hash of root document/paragraph/sentence/text_composition entity (per call's level)
```

Each step's output feeds the next. Each step has a dedicated function in the implementation. Each step has a determinism contract (Substrate Law 6) — same inputs produce byte-identical outputs.

## Step-by-step specification

### Step 1 — Encoding detection and UTF-8 conversion

**Input:** Arbitrary bytes from caller, optionally with declared encoding hint.

**Output:** UTF-8 byte sequence + detection metadata (declared encoding, detected encoding, BOM flag).

**Behavior:**
- If caller declares encoding and provides matching bytes: trust caller's declaration; convert to UTF-8 if not already.
- If no declaration: detect via BOM first (UTF-8, UTF-16-LE/BE, UTF-32-LE/BE BOMs); fall back to ICU's `ucsdet` for charset detection; default to UTF-8 if ambiguous.
- Strip BOM if present.
- For corrupt bytes (invalid sequences in declared encoding): substitute U+FFFD REPLACEMENT CHARACTER for the bad byte(s), record the substitution count in the call's diagnostics.

**Determinism:** ICU charset detection is deterministic given identical input bytes. BOM detection is byte-exact. Replacement-character substitution is deterministic.

**Failure modes:**
- Caller declares an encoding the substrate doesn't support (e.g., proprietary encoding): raises `unsupported_encoding`.
- Bytes are entirely binary noise with no detectable encoding signature: raises `undetectable_encoding`. Caller may pass `assume_encoding => 'utf-8'` to force a best-effort decode.

**Example:**
```
input:  0xEF 0xBB 0xBF 0x48 0x65 0x6C 0x6C 0x6F   (UTF-8 BOM + "Hello")
output: 0x48 0x65 0x6C 0x6C 0x6F                    (BOM stripped, UTF-8)
```

### Step 2 — Codepoint decode

**Input:** UTF-8 byte sequence.

**Output:** Sequence of uint32 codepoint values (`int4[]` for SQL-side handling).

**Behavior:** Standard UTF-8 decoding. Each multi-byte sequence becomes a single uint32. Surrogate pairs are forbidden (UTF-8 should never contain them); invalid surrogates are substituted with U+FFFD.

**Determinism:** Standard UTF-8 decoding is deterministic.

**Failure modes:** Truncated final UTF-8 sequence (input cut mid-character): substitute U+FFFD; record diagnostic.

### Step 3 — NFC normalization

**Input:** Codepoint sequence.

**Output:** Codepoint sequence in Normalization Form C (canonically composed).

**Behavior:**
1. For each codepoint, look up its canonical decomposition mapping from `junc.codepoint_property.decomposition_mapping` (sourced from UCD's UnicodeData.txt and DerivedNormalizationProps.txt).
2. Recursively decompose codepoints with canonical decomposition until all are fully decomposed.
3. Canonically reorder combining marks within each decomposition group by their `combining_class` (sourced from UCD).
4. Canonically compose: walk decomposed sequence, look up `(starter, combining-mark) → composed-codepoint` mappings (UCD's CompositionExclusions.txt-aware), substitute composed forms.

**Determinism:** UCD-defined; deterministic by Unicode standard. Substrate's implementation uses UCD-derived tables loaded into `junc.codepoint_property` at UCD seed time, so the algorithm is deterministic given a fixed UCD version.

**Critical:** NFC normalization is NOT identity collapse between NFC and NFD inputs. The substrate STORES different bytes as different entities. NFC normalization ensures that within a single text decomposer call, the codepoint sequence is canonical. Different NFC vs NFD inputs produce DIFFERENT codepoint sequences pre-NFC and the SAME canonical sequence post-NFC — but the substrate records both inputs' processing because the original bytes had different content hashes BEFORE the text decomposer was called.

The canonical-equivalence linkage between NFC and NFD precomposed/decomposed forms is recorded as `canonical_decomposition_of` edges from UCD seed. Querying "are these canonically equivalent" walks those edges; it does NOT collapse them at decomposer time.

**Failure modes:**
- UCD seed not yet run: `junc.codepoint_property` is empty; raises `ucd_not_seeded`. The text decomposer requires UCD seed to function.
- Codepoint not in UCD (e.g., unassigned PUA code): treated as non-decomposable, non-combining; passes through unchanged.

**Example:**
```
input:  [0x0063, 0x0061, 0x0066, 0x0065, 0x0301]    ("c" "a" "f" "e" combining-acute)
output: [0x0063, 0x0061, 0x0066, 0x00E9]            ("c" "a" "f" "é")
```

The two inputs `[0x0063, 0x0061, 0x0066, 0x00E9]` (NFC `café`) and `[0x0063, 0x0061, 0x0066, 0x0065, 0x0301]` (NFD `cafe + combining-acute`) produce the SAME post-NFC sequence, hence the SAME `text_composition` hash. But the substrate records the original bytes via `provenance` so audit can identify which original form was ingested.

### Step 4 — Codepoint atom emission

**Input:** NFC codepoint sequence.

**Output:** For each unique codepoint in the sequence, ensure a row exists in `substrate.entity` with `entity_type_id = codepoint` and `hash = atom_id(codepoint_value)`. Most codepoints will already exist after UCD seed; this step is a no-op for those.

**Behavior:**
- For each codepoint `c`:
  - Compute `hash = BLAKE3(le32(c))` via `hartonomous.atom_id(c)`.
  - `INSERT INTO substrate.entity (hash) VALUES (hash) ON CONFLICT DO NOTHING`; classification recorded separately in `substrate.entity_classification(entity_hash, entity_type_id, provenance_id)`.
- After UCD seed: every assigned codepoint already exists; this step exits early.
- For unassigned codepoints (PUA, unsupported planes): atoms are still emitted but with no `junc.codepoint_property` row (UCD didn't define properties).

**Determinism:** Identity hashing is deterministic. ON CONFLICT DO NOTHING ensures concurrent decomposers don't conflict.

### Step 5 — UAX #29 grapheme cluster segmentation

**Input:** NFC codepoint sequence.

**Output:** Sequence of grapheme cluster boundaries (cluster start indices into the codepoint sequence).

**Behavior:** Implements UAX #29 GCB rules using `junc.codepoint_property.gcb_id` lookups:

1. For each codepoint, look up its Grapheme_Cluster_Break property (CR, LF, Control, Extend, ZWJ, Regional_Indicator, Prepend, SpacingMark, L, V, T, LV, LVT, Extended_Pictographic, Other).
2. Apply UAX #29 GCB rules:
   - GB1: break at start of text
   - GB2: break at end of text
   - GB3: do not break between CR and LF
   - GB4–GB5: break before/after Controls
   - GB6–GB8: do not break Hangul jamo sequences
   - GB9–GB9b: do not break before Extend, ZWJ, SpacingMark; do not break after Prepend
   - GB11: do not break within emoji modifier sequences or extended pictographic + ZWJ + extended pictographic
   - GB12–GB13: do not break between Regional_Indicator pairs (flag emoji)
   - GB999: otherwise break

**Determinism:** UAX #29 is deterministic. Substrate's implementation matches Unicode's official `GraphemeBreakTest.txt` conformance test on every test case (validation gate D-graphemebreak).

**Validation gate:** every released text decomposer version must pass 100% of `D:\Models\UCD\Public\UCD\latest\ucd\auxiliary\GraphemeBreakTest.txt` test cases. Any failure blocks release.

**Example:**
```
input:    [0x0063, 0x0061, 0x0066, 0x00E9]    (NFC "café")
boundaries: [0, 1, 2, 3]                      (4 grapheme clusters: c | a | f | é)

input:    [0x0061, 0x0301, 0x1F1FA, 0x1F1F8] (a + combining-acute + flag-US (regional indicators))
boundaries: [0, 2]                           (2 grapheme clusters: á | 🇺🇸)
                                                                          ^ flag is one cluster from two regional indicators
```

### Step 6 — Grapheme cluster composition emission

**Input:** Codepoint sequence + cluster boundaries.

**Output:** For each grapheme cluster, ensure a `substrate.entity` row exists with `entity_type_id = grapheme_cluster` and `hash = composition_id([codepoint_atom_hashes_in_order])`. Plus `physicality` row with `linestring4d` through child codepoint S³ positions.

**Behavior:**
- For each cluster (range `[start, end)` in the codepoint sequence):
  - Compute child hashes: `codepoint_atom_hashes = [atom_id(c) for c in codepoints[start:end]]`.
  - Compute composition hash: `cluster_hash = composition_id(codepoint_atom_hashes)`.
  - Upsert `substrate.entity (grapheme_cluster, cluster_hash)`.
  - If multi-codepoint cluster, build linestring4d: lookup S³ position for each child codepoint from `substrate.physicality(s3_codepoint, codepoint_atom_hash)`; build `linestring4d` through those positions; upsert into `substrate.physicality(composition_trajectory, grapheme_cluster, cluster_hash)`.
  - For single-codepoint clusters, the cluster's physicality is the single codepoint's `point4d` (no separate linestring4d row needed; reuses the codepoint's physicality).
- Emit one `edge_member` row per (cluster, codepoint) participation.

**Determinism:** Merkle hash is deterministic. S³ position lookup is read-only deterministic.

**Critical:** Grapheme clusters with the SAME codepoint sequence produce the SAME hash regardless of which decomposer call originated them. This is the convergence guarantee for grapheme-level content.

### Step 7 — UAX #29 word boundary segmentation

**Input:** Codepoint sequence + grapheme cluster boundaries (so word boundaries are at cluster boundaries, not mid-cluster).

**Output:** Sequence of word boundaries (cluster-aligned start indices).

**Behavior:** Implements UAX #29 WB rules using `junc.codepoint_property.wb_id`. Similar structure to GCB but uses Word_Break property values (CR, LF, Newline, Extend, ZWJ, Regional_Indicator, Format, Katakana, Hebrew_Letter, ALetter, Single_Quote, Double_Quote, MidNumLet, MidLetter, MidNum, Numeric, ExtendNumLet, WSegSpace, Extended_Pictographic, Other).

Rules WB1 through WB999 per UAX #29.

**Validation gate:** must pass `WordBreakTest.txt` 100%.

**Note on whitespace and punctuation:** word boundaries are between content; whitespace and punctuation themselves form their own "word forms" at this segmentation tier. The text decomposer DOES NOT discard whitespace; it forms whitespace word_form entities. This is essential for lossless reconstruction.

### Step 8 — Word form composition emission

**Input:** Codepoint sequence + word boundaries (cluster-aligned).

**Output:** For each word, a `word_form` entity with `composition_id` of its constituent grapheme cluster hashes; `linestring4d` through grapheme cluster centroids.

**Behavior:**
- For each word's range of clusters:
  - Compute child hashes (grapheme cluster hashes).
  - Compute composition hash via Merkle.
  - Upsert `substrate.entity(word_form, hash)`.
  - Build `linestring4d` through grapheme cluster centroids.
  - Upsert `substrate.physicality(composition_trajectory, word_form, hash)`.
- Emit edge_member rows linking word_form to its grapheme cluster children with positional ordering preserved.

### Step 9 — UAX #29 sentence boundary segmentation

**Input:** Codepoint sequence + word boundaries.

**Output:** Sentence boundaries (word-aligned).

**Behavior:** Implements UAX #29 SB rules using `junc.codepoint_property.sb_id`.

Sentence boundaries respect:
- ATerm (period, full-stop) followed by uppercase
- STerm (`?`, `!`, etc.) followed by space + uppercase
- Newline + newline (paragraph break) implies sentence boundary
- ATerm + Close (e.g., `."` ) handling

**Validation gate:** must pass `SentenceBreakTest.txt` 100%.

### Step 10 — Sentence composition emission

**Input:** Word forms + sentence boundaries.

**Output:** Sentence (typed as `text_composition` with sentence-level metadata, OR `ud_sentence` if UD ingestion is in progress and the text is being placed into UD's framework).

**Behavior:**
- Compute composition hash from constituent word_form hashes.
- Build linestring4d through word_form centroids.
- Upsert entity, physicality, edge_members.

**Cross-decomposer note:** When the UD seed decomposer ingests a CoNLL-U sentence, it calls the text decomposer with the sentence's raw text bytes. The text decomposer produces a `text_composition` entity. The UD decomposer THEN attaches dependency edges (`dep_nsubj`, etc.) to that `text_composition`'s constituent `word_form` entities. The text decomposer does NOT produce dependency annotations; it produces the structural skeleton onto which UD attaches them.

### Step 11 — Paragraph segmentation

**Input:** Sentences + original codepoint sequence (for line-break inspection).

**Output:** Paragraph boundaries.

**Behavior:**
- A blank line (two consecutive line breaks) implies paragraph break.
- A line break (CR, LF, CRLF, or any UAX #14 mandatory break) followed by content NOT starting with whitespace implies a paragraph continuation, not a paragraph break.
- For inputs without explicit blank-line structure, the entire input is one paragraph.
- Customer-supplied paragraph boundaries (via decomposer parameters) override automatic detection.

### Step 12 — Paragraph composition emission

Standard pattern: `composition_id` from sentence hashes; linestring4d through sentence centroids; entity + physicality + edge_member rows.

### Step 13 — Document composition emission

**Input:** Paragraphs + document-level metadata (filename, title, etc., if provided).

**Output:** Top-level `text_composition` entity (or `document` entity for explicitly-document-typed input).

**Behavior:**
- For linear documents: linestring4d through paragraph centroids.
- For branched documents (chapters, sections): `multilinestring4d` with one branch per top-level section.
- Document-level metadata (provenance, original path, language, encoding) attaches as edges to the document entity.

## What the text decomposer DOES NOT do

These are explicitly NOT the text decomposer's responsibilities:

- **Lemma / morpheme analysis.** That comes from morphological analysis seeds (UD, WordNet, Wiktionary). The text decomposer produces word_forms only.
- **Syntactic parsing.** UD seeder produces dependency annotations on word_form entities the text decomposer creates.
- **Sense disambiguation.** Inference at query time, using `lexical_disambiguation` arena.
- **Named entity recognition.** GUM, OntoNotes, or NER decomposer attaches NER edges to word_form entities the text decomposer creates.
- **Discourse structure.** GUM's RST annotations are attached separately.
- **Sentiment / emotion classification.** GoEmotions / EmoBank attach via cross-corpus edges.

The text decomposer produces the LEXICAL skeleton: codepoints → grapheme clusters → word_forms → sentences → paragraphs → documents. Every other annotation layer is a separate decomposer that attaches edges to the skeleton's entities.

## Pipeline interface contract

The text decomposer is exposed to other decomposers via the pipeline's `DecomposeText` function:

```csharp
public interface IIngestionPipeline {
    Task<byte[]> DecomposeText(byte[] utf8Bytes, int provenanceId, CancellationToken ct);
    Task<byte[]> DecomposeText(string text, int provenanceId, CancellationToken ct);
    // Variants:
    Task<byte[]> DecomposeText(byte[] bytes, int provenanceId, TextDecomposerOptions options, CancellationToken ct);
}

public record TextDecomposerOptions {
    public string? DeclaredEncoding;       // override encoding detection
    public string? Language;                // hint for language-specific paragraph rules
    public bool TreatAsSingleSentence;     // skip sentence boundary detection
    public bool TreatAsSingleParagraph;    // skip paragraph boundary detection
    public bool EmitDocumentEntity;         // wrap output in document entity (vs paragraph/sentence/text_composition)
    public Dictionary<string, string>? Metadata;  // attached as document-level edges
}
```

Returns the BLAKE3 hash of the appropriate root entity (text_composition if `EmitDocumentEntity` is false; document if true).

## SQL surface

```sql
-- Decompose UTF-8 bytes to substrate; return root composition hash
hartonomous.text_decompose(
    bytes         BYTEA,
    provenance_id INT
) RETURNS BYTEA;

-- Decompose a string; convenience wrapper
hartonomous.text_decompose_string(
    text          TEXT,
    provenance_id INT,
    declared_encoding TEXT DEFAULT 'utf-8',
    language          TEXT DEFAULT NULL
) RETURNS BYTEA;

-- Decompose with options
hartonomous.text_decompose_with_options(
    bytes         BYTEA,
    provenance_id INT,
    options       JSONB
) RETURNS TABLE (
    root_hash      BYTEA,
    root_type      TEXT,
    paragraphs     INT,
    sentences      INT,
    word_forms     INT,
    grapheme_clusters INT,
    codepoints_seen   INT,
    diagnostics    JSONB
);
```

## Determinism guarantees (Substrate Law 6)

For the same input bytes + same `provenance_id` + same UCD version + same text decomposer version:

- The output root hash is byte-identical.
- The set of substrate entity rows produced is identical.
- The set of edge_member rows produced is identical.
- The physicality rows produced are identical.

Re-running text_decompose on the same bytes produces no new rows (ON CONFLICT DO NOTHING) and returns the same root hash.

A version bump (UCD update; text decomposer logic change) may produce different output. The version is recorded in the diagnostics for replay reproducibility.

## Performance characteristics

For typical inputs:

| Input size | Codepoints | Latency target |
|---|---|---|
| Single sentence (10–20 words) | ~100 | <1 ms |
| Paragraph (100 words) | ~600 | ~3–5 ms |
| Document (10K words) | ~60K | ~100–300 ms |
| Book (200K words) | ~1.2M | ~3–10 s |

Latency is dominated by:
- UCD property lookups for each codepoint (cached after warmup; first-time hit on cold cache adds ~10× overhead)
- BLAKE3 hashing per composition tier (microseconds per hash; SIMD-accelerated)
- INSERT round-trips for new entities/edges/physicality (batched via COPY in pipeline; not per-row)

For substrate-bulk ingestion (entire corpora): the text decomposer runs as a stage in the pipeline's bulk-load path, processing thousands of documents in parallel. Throughput typically >100K codepoints/sec/core after warmup.

## Validation gates

Required for any released text decomposer version:

- **D-graphemebreak**: 100% pass on `GraphemeBreakTest.txt` (Unicode-official).
- **D-wordbreak**: 100% pass on `WordBreakTest.txt`.
- **D-sentbreak**: 100% pass on `SentenceBreakTest.txt`.
- **D-nfc**: 100% pass on `NormalizationTest.txt`.
- **D-determinism**: same bytes + same version → byte-identical entity row set.
- **D-convergence**: same content from two different decomposer callers (e.g., WordNet seed + Tatoeba seed) → same root hash.
- **D-cafe**: NFC `café` + NFD `cafe + U+0301` produce the same root `text_composition` hash (post-NFC sequences are identical).
- **D-empty**: empty byte input produces a defined empty `text_composition` entity (not error).
- **D-bom**: UTF-8 BOM is stripped; UTF-16/UTF-32 BOM-detected inputs are converted; output is identical regardless of input encoding (post-UTF-8 conversion).
- **D-multilingual**: same logical sentence in different scripts (Mandarin, Arabic, Hindi, Hebrew, Thai) produces correct grapheme/word/sentence segmentation per UAX #29.
- **D-emoji**: ZWJ emoji sequences produce one grapheme cluster per ZWJ-joined sequence.

## Failure modes

- **`unsupported_encoding`**: caller declared an encoding the substrate doesn't support.
- **`undetectable_encoding`**: bytes have no detectable encoding signature.
- **`ucd_not_seeded`**: text decomposer requires UCD seed (`junc.codepoint_property` populated).
- **`exceeded_max_input_size`**: input exceeds the substrate's per-call size limit (default 1 GB; configurable).
- **`malformed_utf8`** (logged, not raised): input had invalid UTF-8 sequences; replaced with U+FFFD; count in diagnostics.

The text decomposer does NOT silently skip content. Every codepoint in the input is accounted for in some emitted entity (codepoint atoms; possibly U+FFFD substitutes). Lossless reconstruction is preserved.

## Concurrency model

Multiple text decomposer calls can run in parallel. Each call is logically independent. Substrate-side concurrency safety:

- `INSERT ... ON CONFLICT DO NOTHING` on `substrate.entity (hash)` handles concurrent identical entity insertion without conflict.
- `substrate.edge_member` similarly protected.
- `substrate.physicality` similarly.
- The pipeline batches writes via `COPY ... FROM STDIN (FORMAT binary)` to amortize transaction overhead.

The text decomposer is therefore safe to run with any concurrency level the pipeline supports, bounded only by Postgres connection pool size and shared_buffers.

## Worked example

Input bytes for "Hello, world.\nThis is the substrate.":

```
0x48 0x65 0x6C 0x6C 0x6F 0x2C 0x20 0x77 0x6F 0x72 0x6C 0x64 0x2E
0x0A
0x54 0x68 0x69 0x73 0x20 0x69 0x73 0x20 0x74 0x68 0x65 0x20 0x73 0x75 0x62 0x73 0x74 0x72 0x61 0x74 0x65 0x2E
```

Pipeline:

1. UTF-8 already; no BOM. (Step 1 no-op.)
2. Codepoints: `[0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x2C, 0x20, ..., 0x2E]` — 35 codepoints.
3. NFC normalize: no decomposable codepoints; unchanged.
4. Atom emission: ASCII codepoints already in substrate (UCD seeded); no new atoms.
5. Grapheme clusters: each ASCII codepoint is its own cluster; 35 clusters.
6. Cluster compositions: each is a single-codepoint cluster reusing codepoint physicality; no new linestring4d.
7. Word boundaries: between letters and punctuation/space. 8 word_forms total: `Hello`, `,`, ` `, `world`, `.`, `\n`, `This`, ..., etc. (Whitespace and punctuation are word_forms; this preserves lossless reconstruction.)
8. Word_form compositions: 8 new word_form entities (or reuses if already in substrate).
9. Sentence boundaries: after `Hello, world.\n` and `This is the substrate.`. 2 sentences.
10. Sentence compositions: 2 new sentence entities.
11. Paragraph: blank-line check finds none (just newline, not double); whole input is one paragraph.
12. Paragraph composition: 1 new paragraph entity, linestring4d through 2 sentence centroids.
13. Document composition: depends on caller's `EmitDocumentEntity` flag. If true, wraps paragraph in document. If false, returns paragraph hash.

Returns: BLAKE3 hash of the paragraph (or document) entity.

Substrate state after this call:
- ~28 unique codepoint atoms referenced (most ASCII chars repeated)
- ~22 unique grapheme clusters (most ASCII single-codepoint clusters)
- 8 unique word_forms (Hello, comma, space, world, period, newline, This, is, the, substrate, period — exact count depends on punctuation/whitespace tokenization details)
- 2 sentence entities
- 1 paragraph entity
- 1 paragraph→sentence linestring4d
- 1 document entity (if EmitDocumentEntity)

## Cross-references

- Substrate Law 5 (decomposers as pure producers): `10-architecture/01-substrate-laws.md`
- Substrate Law 7 (language-agnostic by Unicode): same
- Identity layer (BLAKE3, Merkle DAG): `10-architecture/02-identity-and-convergence.md`
- Geometry layer (linestring4d through child centroids): `10-architecture/03-geometry-4d.md`
- Decomposer contract: `10-architecture/05-decomposer-contract.md`
- UCD inventory (the data this decomposer depends on): `20-technical/14-ucd-inventory.md`
- Tree-sitter grammar strategy: `20-technical/16-tree-sitter-grammar-strategy.md`
- Anti-pattern AP-9 (hashing placement metadata): `40-process/01-anti-patterns.md`

## External references

- UAX #29 (text segmentation): <https://unicode.org/reports/tr29/>
- UAX #15 (Unicode normalization): <https://unicode.org/reports/tr15/>
- UAX #14 (line breaking): <https://unicode.org/reports/tr14/>
