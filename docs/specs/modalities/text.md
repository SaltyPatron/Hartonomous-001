# Text Modality Decomposer Specification

## Identity

- **Decomposer class**: `TextDecomposer` extends `BaseDecomposer`
- **Purpose**: Runtime decomposition of arbitrary text content into the substrate. This is NOT the seed-format parsers (WordNet parser, CoNLL-U parser, etc.). This is what handles any raw text -- prompts, documents, articles, code, chat messages.
- **Dependency**: Full seed type system must be in place (UCD for character identity and segmentation, WordNet/OMW for candidate sense linking, UD for syntactic structure evidence, Wiktionary for morphological analysis and lemmatization).

## Structural Parsing Frontend: Tree-sitter

Before the level-by-level decomposition runs, Tree-sitter parses the raw input into a Concrete Syntax Tree (CST) that defines the document's structural skeleton. UAX #29 handles character-level segmentation (graphemes, words, sentences); Tree-sitter handles document-level structure (chapters, paragraphs, sections, code blocks, dialogue, headers, lists, tables).

### How It Works

1. **Grammar selection**: the decomposer selects the appropriate Tree-sitter grammar for the content type — Markdown, HTML, plain prose, source code, legal text, etc. If no grammar matches, the decomposer falls back to UAX #29-only segmentation (sentences as the highest structural unit).
2. **Parse**: Tree-sitter produces a CST. Each node in the CST is a structural element (e.g., `document → section → paragraph → sentence`).
3. **Walk and decompose**: the decomposer walks the CST top-down. At each structural node, it creates a composition entity and hashes it. Leaf nodes (text content) flow into the bottom-up Level 0-7 pipeline below.
4. **Incremental update**: if the content is a modification of previously ingested text, Tree-sitter identifies which CST nodes changed. Only those nodes (and their Merkle DAG ancestors) are rehashed and updated. The rest of the substrate is untouched — structural sharing is preserved by the Merkle DAG.

### Structural Nodes as Composition Entities

Each Tree-sitter node becomes a composition entity in the substrate:

```
document (root composition)
├── chapter (composition of paragraphs)
│   ├── paragraph (composition of sentences)
│   │   ├── sentence (composition of words — from UAX #29 SB boundaries)
│   │   │   ├── word (composition of graphemes — from UAX #29 WB boundaries)
│   │   │   │   └── grapheme (composition of codepoints — from UAX #29 GCB boundaries)
│   │   │   │       └── codepoint (tier-0 atom — from UCD seed)
```

Tree-sitter defines the upper levels (document → chapter → paragraph). UAX #29 defines the lower levels (sentence → word → grapheme → codepoint). The boundary is the sentence: Tree-sitter identifies sentence containers, UAX #29's `SentenceBreakProperty` identifies the exact boundaries within them.

### Why Not Just UAX #29

UAX #29 is segmentation — it finds the *boundaries* between words and sentences. It does not produce structural hierarchy. It cannot distinguish a chapter heading from body text, a code block from prose, a footnote from a citation. Tree-sitter provides the structural typing that makes the AST semantically navigable.

### Grammar as Type System

The grammar IS the type system for the content format. A Markdown grammar declares that `## Heading` is a `section_heading` node. A legal grammar declares that `WHEREAS` introduces a `recital` clause. These structural types flow into `entity_type` classification and enable type-safe queries: "find all `section_heading` nodes in this corpus" is a reference table lookup, not a string search.

## Decomposition Pipeline

### Level 0: Raw Bytes to Codepoints

1. Detect encoding (UTF-8, UTF-16, etc.) or use declared encoding.
2. Decode to Unicode codepoint sequence.
3. Each codepoint is an existing tier-0 entity (from UCD seed). Hash lookup confirms existence. If a codepoint somehow doesn't exist, fail loud.
4. Normalize to NFC (or configurable normalization form) for canonical comparison. Original byte sequence preserved for round-trip.

### Level 1: Codepoints to Grapheme Clusters

Using UCD `GraphemeBreakProperty` (seeded in Phase 2a):
1. Apply Unicode grapheme cluster segmentation algorithm (UAX #29).
2. Each grapheme cluster is a composition of its constituent codepoints.
3. Hash, deduplicate. Most grapheme clusters for common scripts are single codepoints (already exist). Multi-codepoint clusters (accented characters via combining marks, emoji sequences, Hangul syllables from jamo) are compositions.

### Level 2: Grapheme Clusters to Words/Tokens

Using UCD `WordBreakProperty` (seeded in Phase 2a):
1. Apply Unicode word segmentation algorithm (UAX #29).
2. For scripts without word boundaries (Chinese, Japanese, Thai): use UD-trained segmentation patterns from the substrate (the UD treebanks provide word boundary training data for these scripts).
3. Each word/token is a composition of its grapheme clusters.
4. Hash, deduplicate. Common words ("the", "is", "a") already exist from seed data. New words are new compositions.

### Level 3: Words to Morphemes

Using Wiktionary morphological data + WordNet derivational relations:
1. For each word, check if morphological decomposition exists in the substrate (Wiktionary forms, WordNet derivationally_related).
2. If found: create morpheme entities and morphological role relations (prefix, root, suffix, etc.).
3. If not found: the word is treated as an opaque morphological unit (no guessing, no lazy splitting -- semantic fidelity law).
4. Morpheme entities are shared ("un-", "re-", "-ing", "-tion" each stored once).

### Level 4: Words to Lemmas and Candidate Senses (No Disambiguation)

Ingestion records facts. Inference decides meaning. (Substrate Law #8.)

1. **Lemmatization**: map inflected form to lemma using Wiktionary forms data and WordNet exception lists. This is deterministic lookup, not disambiguation.
2. **Candidate sense linking**: link the word form to ALL candidate senses from the substrate (WordNet synsets, Wiktionary senses, OMW cross-lingual alignments). No sense is selected or preferred. Every candidate gets an edge and an `entity_sense` junction table entry.
3. **Contextual evidence recording**: record the word's context as evidence edges — what words co-occur, what syntactic position it occupies, what domain signals surround it. These are facts about this occurrence, not judgments about which sense is correct.

The system does NOT attempt sense disambiguation at ingestion. "Bank" in "river bank" gets linked to ALL senses of "bank" (financial, river, pool table, etc.) with evidence edges recording that "river" co-occurs. Sense selection — determining that the river-edge sense is the active one — happens at inference time, where the significance-weighted traversal from context entities naturally activates the correct sense (see [inference.md](../../engine/inference.md)).

This design ensures:
- **Decomposition stays fully deterministic** — same text input = same entities, always, because no disambiguation judgment is made.
- **No circular dependency** — ingestion does not require the inference engine to be functioning.
- **Quality improves automatically** — every document ingested adds more evidence edges, which strengthens the significance signal for future disambiguation at inference time.

### Level 5: Words to Syntactic Structure

Using UD-trained dependency parsing patterns:
1. For each sentence (detected via UCD `SentenceBreakProperty`):
   - Identify UPOS for each token from substrate patterns. Record via `entity_pos` junction table entry (FK → `pos` reference table).
   - Identify dependency relations (HEAD, DEPREL) from substrate patterns. Each dependency arc is an edge typed via `deprel` reference table.
   - Identify morphological features from Wiktionary + UD feature data. Record via `entity_morph_feature` junction table entries (morph_feature values are reference table rows, not edge targets).
2. Each dependency arc is an edge in the `edge` table (edge_type = the DEPREL value, e.g., `nsubj`, `obj`, `amod`, looked up from the `deprel` reference table).
3. The sentence is a composition entity whose sequence references its tokens in order.
4. The dependency tree is a set of edges connecting tokens.

### Level 6: Semantic Analysis Passes

All computed at ingestion. All stored as edges and significance records.

- `NERPass` -- identify named entities (persons, organizations, locations, dates). Each NE is an entity with NER classification edges.
- `CoreferencePass` -- identify coreference chains (pronouns to their referents). Each coref link is an edge.
- `DiscourseStructurePass` -- identify discourse relations (RST: elaboration, contrast, cause, etc.). Each discourse relation is an edge.
- `SentimentPass` -- compute sentiment polarity at sentence and entity level. Stored as significance in `sentiment` context.
- `ReadabilityPass` -- compute readability metrics. Stored as edges.
- `RegisterDetectionPass` -- classify register/formality level. Stored as edge to register classification.
- `FrequencyPositionPass` -- compute term frequency, position significance, co-occurrence patterns. Stored in significance table.

### Level 7: Physicality

- Each codepoint: existing PointZM on S3 (from UCD seed).
- Each word/token composition: LinestringZM trajectory from constituent codepoint/grapheme S3 positions. Centroid derived.
- Each sentence composition: LinestringZM trajectory from constituent word centroids. Centroid derived.
- Each paragraph/document: LinestringZM from sentence centroids.
- Hilbert curve values computed at each level.

## What Gets Stored vs What Gets Computed

| Level | Stored | Not Stored |
|-------|--------|-----------|
| Codepoints | Already exist (from UCD seed) | -- |
| Grapheme clusters | Compositions of codepoints (if multi-codepoint) | Single-codepoint clusters ARE the codepoint entity |
| Words | Compositions of grapheme clusters | -- |
| Morphemes | Compositions of codepoints, role relations | Only when seed data supports decomposition |
| Lemmas | Entity with inflection edges to forms | -- |
| Candidate senses | Edges from word-in-context to ALL candidate sense entities + `entity_sense` junction entries + contextual evidence edges | Sense selection (deferred to inference) |
| Syntax | Dependency edges (typed with deprel values like nsubj, amod) + `entity_pos` junction entries + `entity_morph_feature` junction entries | -- |
| Named entities | NER-classified entities | -- |
| Coreference | Coref edges | -- |
| Discourse | Discourse edges | -- |
| Frequency/position | Significance records | -- |
| Physicality | PointZM/LinestringZM at every level | -- |

Everything that is stored is an entity, edge, junction table entry, or significance record. No opaque blobs. No flattened rows.

## Round-Trip

`TextRecomposer` reconstructs the original text:
1. Walk the top-level composition's sequence.
2. At each child, recurse to collect codepoints.
3. Encode codepoints to original encoding.
4. Byte-compare against original.

Round-trip must be bit-perfect for the canonical normalization form. If the original was not NFC and was normalized, the original normalization form is recorded as an edge on the text entity, enabling the recomposer to reconstruct in the original form. No binary blobs stored — the normalization form and codepoint sequence are sufficient for exact reconstruction.
