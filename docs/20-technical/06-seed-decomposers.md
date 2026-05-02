# Seed Decomposers — Per-Source Specifications

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Engineers implementing or maintaining seed decomposers; substrate operators verifying seed ingestion produces expected substrate state.

---

## What seed decomposers are

Seed decomposers ingest the substrate's foundational reference corpora — the curated authoritative sources that provide structural scaffolding for the entire substrate. They are NOT just "data load steps." They are domain-specific decomposers that route per-format input through the correct parsing path, emitting substrate state in accordance with the decomposer contract and the seed-uses-core principle.

The seed decomposers are:

| Decomposer | Source | Provenance code | Trust prior μ | Phase |
|---|---|---|---|---|
| UcdUcaDecomposer | UCD/UCA mirror | `unicode_consortium` | 2000 | UcdUca |
| Iso639Decomposer | ISO 639-3 .tab files | `sil_international` | 2000 | Iso639 |
| WordNetDecomposer | Princeton WordNet 3.0 dict files | `princeton_wordnet` | 1800 | WordNetOmw |
| OmwDecomposer | OMW per-language alignments | `omwn_consortium` | 1600 | WordNetOmw |
| UdDecomposer | UD treebanks v2.17 (CoNLL-U) | `universaldependencies` | 1600 | UniversalDeps |
| WiktionaryDecomposer | kaikki.org wiktextract JSONL | `wiktextract` | 1400 | Wiktionary |
| TatoebaDecomposer | Tatoeba sentences/links/audio | `tatoeba` | 1200 | Tatoeba |
| AtomicDecomposer | ATOMIC 2020 TSV | `atomic-2020` | 1500 | TextDecomp |

Each decomposer emits the substrate state required for its source's structural contribution. All route text-bearing strings through `text_decompose` (Substrate Law 5; AP-9 if violated).

This document specifies each per-source.

---

## UcdUcaDecomposer

### Purpose

Populate the substrate's foundational atom layer (codepoints) and the codepoint-property junctions that drive UAX #29 segmentation. This is the prerequisite seed for all subsequent text-bearing decomposers.

### Source

`D:\Models\UCD\Public\UCD\latest\` — full Unicode FTP mirror. See `20-technical/14-ucd-inventory.md` for full file inventory.

Primary input files (the substrate's UCD decomposer consumes all of these):
- `ucd/UnicodeData.txt` — canonical codepoint properties
- `ucd/Blocks.txt`, `ucd/Scripts.txt`, `ucd/ScriptExtensions.txt`
- `ucd/auxiliary/GraphemeBreakProperty.txt`, `WordBreakProperty.txt`, `SentenceBreakProperty.txt`
- `ucd/PropertyAliases.txt`, `ucd/PropertyValueAliases.txt`
- `ucd/extracted/Derived*.txt` (cross-checks)
- `ucd/CaseFolding.txt`, `ucd/SpecialCasing.txt`
- `ucd/CompositionExclusions.txt`, `ucd/DerivedNormalizationProps.txt`
- `ucd/HangulSyllableType.txt`, `ucd/Jamo.txt`
- `ucd/BidiBrackets.txt`, `ucd/BidiMirroring.txt`
- `uca/allkeys.txt` — DUCET collation weights for S³ Super-Fibonacci
- `emoji/emoji-data.txt`, `emoji/emoji-sequences.txt`, `emoji/emoji-zwj-sequences.txt`
- `security/confusables.txt` — visual-confusability pairs

### Substrate state produced

- ~150K codepoint atom entities (one per assigned Unicode codepoint)
- For each codepoint: a `point4d` physicality on S³ via UCA Super-Fibonacci spiral
- For each codepoint: a `junc.codepoint_property` row with general_category, script, block, gcb, wb, sb, lb, combining_class, decomposition_type, decomposition_mapping, etc.
- Reference table populations: `ref.general_category` (~30 rows), `ref.script` (~160 rows), `ref.block` (~300 rows), `ref.break_property` (~80 rows across GCB/WB/SB/LB)
- Edges: `canonical_decomposition_of` (one per decomposable codepoint), `case_folds_to`, `case_maps_to_lowercase/uppercase/titlecase` (per case-mappable codepoint), `compatibility_decomposition_of`
- Edges: `visually_confusable_with` (from confusables.txt; tens of thousands of pairs)
- Multi-codepoint emoji sequence compositions (ZWJ emoji, regional indicator pairs, variation sequences)

### The pipeline

1. Parse `PropertyAliases.txt` and `PropertyValueAliases.txt` to populate property and property-value reference tables (canonical short and long names).
2. Stream-parse `ucd.all.flat.xml` (or per-file walks of `UnicodeData.txt` + auxiliaries). For each `<char>` element:
   - Compute `atom_id(codepoint)`.
   - Upsert `substrate.entity(codepoint, hash)`.
   - Compute UCA collation tuple from `allkeys.txt` lookup.
   - Insert into per-codepoint sort order (deferred to step 3).
   - Upsert `junc.codepoint_property` with general_category, combining_class, decomposition (if any), case mappings, bidi class, etc.
   - Parse `Blocks.txt` for codepoint range → block; insert into junc.codepoint_property.block_id.
   - Parse `Scripts.txt` similarly for script_id.
   - Parse `auxiliary/GraphemeBreakProperty.txt`, `WordBreakProperty.txt`, `SentenceBreakProperty.txt` for break properties.
3. After all codepoints loaded, compute global sort order:
   - Sort all codepoints by full UCA collation tuple.
   - For each codepoint at sorted index `i` of total `N`: compute `point4d` via `super_fibonacci_4d(i, N)`.
   - Upsert `substrate.physicality(s3_codepoint, codepoint_atom_hash, point4d)`.
4. Emit canonical decomposition edges: for each codepoint with non-empty decomposition_mapping, emit `canonical_decomposition_of` edge from precomposed codepoint to first sub-codepoint, plus chained edges for multi-codepoint decompositions.
5. Emit case mapping edges: parse `CaseFolding.txt`, `SpecialCasing.txt`; emit `case_folds_to`, `case_maps_to_*` per row.
6. Parse `confusables.txt`; emit `visually_confusable_with` edges.
7. Parse `emoji-zwj-sequences.txt` and `emoji-sequences.txt`; for each multi-codepoint emoji sequence, emit a composition entity (using text decomposer's grapheme cluster path) and attach `emoji_sequence_form` metadata.

### Determinism

UCD data is versioned (Unicode 16.0, 17.0, etc.). Re-running the decomposer on the same UCD version produces byte-identical substrate state.

A UCD version bump may produce different state (new codepoints, refined properties, new emoji). The version is recorded in `provenance` metadata; old substrate state is preserved.

### Performance

Full UCD ingestion: ~5–15 minutes for first run; subsequent re-runs are no-ops if substrate is already seeded.

### Validation gates

- D-codepoint-count: count of `(entity_type=codepoint)` matches assigned-codepoint count from `UnicodeData.txt` (currently ~150K)
- D-property-coverage: every codepoint has a `junc.codepoint_property` row
- D-s3-positions: every codepoint has exactly one `s3_codepoint` physicality row
- D-decomposition-coverage: every codepoint with non-empty decomposition has at least one `canonical_decomposition_of` outbound edge
- D-uax29-conformance: text decomposer using these property tables passes 100% of UAX #29 conformance tests

### Failure modes

- `ucd_path_not_found`: source directory missing
- `ucd_version_unrecognized`: CompositionExclusions or related file missing required Unicode-version-anchored data

---

## Iso639Decomposer

### Purpose

Populate the substrate's `ref.language` reference table from ISO 639-3, giving every multilingual decomposer a stable language taxonomy.

### Source

`D:\Models\ISO639\`:
- `iso-639-3.tab` — main 7,928-language registry
- `iso-639-3-macrolanguages.tab` — macrolanguage relationships
- `iso-639-3_Name_Index.tab` — language name lookups
- `iso-639-3_Retirements.tab` — retired/superseded codes

### Substrate state produced

- ~7,928 rows in `ref.language` with iso639_3, name, scope, type, family
- Edges in substrate via a special-case (language-as-entity for cross-language relationships): `macrolanguage_includes`, `superseded_by`, `language_family_member`

### The pipeline

1. Parse `iso-639-3.tab` (TSV with header). For each row:
   - Insert `ref.language(iso639_3, name, scope, type)`.
   - If the language belongs to a typological family, insert `ref.language.family`.
2. Parse `iso-639-3-macrolanguages.tab`. For each row, emit `macrolanguage_includes` edges between language entities.
3. Parse `iso-639-3_Retirements.tab`. For each retired code, emit `superseded_by` edge to its replacement.

### Validation gates

- D-language-count: ~7,928 rows in `ref.language` (varies by ISO 639-3 release)
- D-macrolanguage-coverage: every macrolanguage's listed members are represented as edges

---

## WordNetDecomposer

### Purpose

Ingest Princeton WordNet 3.0's lexical-semantic graph: synsets, lemmas, hypernym/hyponym/meronym/holonym/antonym/entailment relations, glosses, examples, sense inventory, lexnames.

### Source

`D:\Models\princeton-wordnet\WordNet-3.0\dict\`:
- `data.{noun,verb,adj,adv}` — synset definitions with pointers
- `index.{noun,verb,adj,adv,sense}` — lemma → synset indices
- `lexnames` — 45 lexicographer file categories
- `frames.vrb`, `sentidx.vrb`, `sents.vrb` — verb sentence frames
- `*.exc` — exception lists (irregular plurals, conjugations)
- `cntlist`, `cntlist.rev` — sense frequency counts

### Substrate state produced

- ~117K `synset` entities
- ~150K English `lemma` entities (composed via text_decompose from each lemma's surface form)
- ~206K `word_sense` entities (lemma↔synset pairings)
- Edges:
  - `has_sense` (lemma → word_sense → synset chain)
  - `hypernym`, `hyponym` (synset → synset)
  - `meronym`, `holonym` (synset → synset, multiple subtypes: part_of, member_of, substance_of)
  - `antonym` (synset → synset)
  - `entailment` (verb synset → verb synset)
  - `cause` (verb synset → verb synset)
  - `similar_to` (adjective synsets)
  - `also_see`, `derivation_of`, `attribute_of` per WordNet pointer types
  - `has_gloss` (synset → text_composition for gloss text — through text_decompose)
  - `has_example` (synset → text_composition for examples — through text_decompose)
- `junc.entity_pos`: every lemma has POS junction rows (NOUN, VERB, ADJ, ADV)
- `junc.entity_sense`: every word_sense junction row
- `ref.lexname`: ~45 lexicographer categories
- `ref.semantic_relation_type`: ~25 WordNet pointer types

### The pipeline

WordNet's text format: each line in `data.<pos>` describes one synset with offset, lex_filenum, ss_type, w_cnt, words, p_cnt, ptrs, gloss.

1. Parse `lexnames` to populate `ref.lexname`.
2. Parse `data.noun`, `data.verb`, `data.adj`, `data.adv`. For each line:
   - Extract synset offset (used as part of synset's content for hashing — note: content for a synset is its sense definition, not its byte offset; use the sense definition string as canonical content via text_decompose to ensure cross-language convergence with OMW's same-synset alignments).
   - Compute synset_hash = composition of (lex_filenum, ss_type, member-lemma hashes, pointer-target hashes). This is a slight Merkle DAG for synsets.
   - For each member lemma in the synset's `words` list:
     - Call text_decompose for the lemma's surface form → lemma_hash.
     - Compute word_sense_hash = composition(lemma_hash, synset_hash).
     - Emit `has_sense` edge from lemma to word_sense, and lemma↔synset via word_sense as mediator.
     - Emit `entity_pos` junction for this lemma (POS = current file's POS).
   - For each pointer in the synset's `ptrs`:
     - Emit edge of pointer type (`hypernym`, `hyponym`, etc.) from this synset to target synset.
   - Parse the gloss field (everything after `|`):
     - Split on `;` to separate definition from examples.
     - Call text_decompose on definition → emit `has_gloss` edge.
     - For each example (quoted strings), call text_decompose → emit `has_example` edge.
3. Parse `index.sense` for sense frequency counts; populate `entity_sense` significance via Glicko initial μ proportional to count.

### Determinism

WordNet 3.0 is a frozen release. Same dict files always produce same state.

### Performance

Full WordNet ingestion: ~5–10 minutes (limited by per-gloss text_decompose calls).

### Validation gates

- D-synset-count: ~117K synsets
- D-lemma-count: ~150K English lemmas
- D-pointer-coverage: every synset's stated pointers result in edges
- D-gloss-coverage: every synset has a `has_gloss` edge to a text_composition

### Critical: seed-uses-core enforcement

The WordNet decomposer MUST route every gloss text and every example text through `pipeline.decompose_text`. It MUST NOT compute `BLAKE3.Hash(gloss_string)` directly. Code-review check: grep for `Blake3.Hash` calls on string-bearing variables in WordNet decomposer source — should return zero matches.

---

## OmwDecomposer

### Purpose

Graft 30+ non-English wordnets onto Princeton's synset spine, populating cross-lingual lemma alignment.

### Source

`D:\Models\omw\wns\` — per-language directories (als, arb, bul, cmn, dan, ell, eng, fas, fin, fra, heb, hrv, isl, ita, jpn, mcr, msa, nld, nor, pol, por, ron, slk, slv, swe, tha, etc.).

Per-language `.tab` files: each row is `<offset> <pos>\t<lemma>` aligning a non-English lemma to a Princeton synset offset (which the substrate has hashed via WordNetDecomposer).

### Substrate state produced

- Non-English lemma entities (one per (language, surface form) combination, deduplicated by content via text_decompose)
- `aligned_to_synset` edges from non-English lemma to Princeton synset
- `entity_language` junction rows (each lemma tagged with its language)

### The pipeline

1. For each language directory:
   - Determine ISO 639-3 code from directory name.
   - Lookup `language_id` in `ref.language` (populated by Iso639Decomposer).
2. For each `.tab` file in the language directory:
   - Parse rows.
   - For each lemma:
     - text_decompose(surface_form) → lemma_hash.
     - Lookup synset_hash by Princeton offset.
     - Emit `aligned_to_synset` edge from lemma to synset.
     - Emit `entity_language` junction row tagging the lemma.

### Validation gates

- D-omw-language-count: 30+ language directories ingested
- D-omw-alignment-count: total `aligned_to_synset` edges in expected order of magnitude (~300K–1M depending on coverage)
- D-cross-lingual-convergence: a synset has multiple `aligned_to_synset` inbound edges, one per language with coverage

---

## UdDecomposer

### Purpose

Ingest Universal Dependencies treebanks, populating syntactic dependency edges, POS tags, morphological features per attested word_form across 100+ languages.

### Source

`D:\Models\ud-treebanks\ud-treebanks-v2.17\` — 339 treebank directories. Each directory contains CoNLL-U files (`*.conllu`) per train/dev/test split.

### Substrate state produced

- `ud_sentence` entities (one per treebank sentence)
- `ud_token` entities (one per word in each sentence; aliased to `word_form` content via text_decompose)
- `lemma` entities (deduplicated; aligned to WordNet's lemma entities by content where overlap exists)
- `dep_*` edges (one per UD relation type: `dep_nsubj`, `dep_obj`, `dep_iobj`, `dep_obl`, ~70 types plus subtypes like `dep_nsubj:pass`)
- `ref.pos` populated with 17 UPOS tags
- `ref.deprel` populated with ~70 dependency relations
- `ref.morph_feature` populated with ~68 morphological feature keys × their values
- `junc.entity_pos`: every word_form gets POS junction rows
- `junc.entity_morph_feature`: every word_form gets morph junction rows
- `junc.entity_language`: tagged per treebank's language

### The pipeline

UD decomposer uses `tree-sitter-conllu` (per `20-technical/16-tree-sitter-grammar-strategy.md`) to parse `.conllu` files into typed AST.

1. For each treebank directory, determine language from directory name (`UD_<Language>-<TreebankName>`).
2. For each `.conllu` file:
   - Parse via tree-sitter-conllu.
   - For each sentence in the parsed AST:
     - Extract sentence's raw text via text_decompose → sentence_text_composition_hash.
     - For each token row:
       - text_decompose(form) → form_hash; text_decompose(lemma) → lemma_hash if lemma differs from form.
       - Emit `entity_pos` junction with UPOS.
       - Parse FEATS column; emit `entity_morph_feature` junctions.
       - Parse DEPREL column; emit `dep_<relation>` edge from form to head_form.
     - Compose `ud_sentence` entity wrapping the sentence.
   - Emit treebank-level metadata (citation, license, version) as edges on the treebank entity.

### Validation gates

- D-treebank-count: 339 treebanks ingested
- D-pos-coverage: every UPOS in `ref.pos`
- D-deprel-coverage: every UD deprel in `ref.deprel`
- D-morph-coverage: morphological features ingested per treebank-language
- D-cross-language-convergence: lemmas shared across UD treebanks (e.g., "dog" appears in English and German treebanks both with lemma form "dog" via text_decompose) converge to single substrate entity rows

---

## WiktionaryDecomposer

### Purpose

Ingest the kaikki.org wiktextract dump (the largest community-curated multilingual dictionary), populating broad lexical coverage including etymology, IPA pronunciation, inflections, translations, synonyms, and senses.

### Source

`D:\Models\wiktionary\`:
- `raw-wiktextract-data.jsonl` (~22GB) — full multilingual dump
- `kaikki.org-dictionary-English.jsonl` (~2.9GB) — English-only filtered dump

Operator chooses which to ingest (or both with separate sub-provenance).

### Substrate state produced

- `lemma` entities (deduplicated against WordNet/OMW where overlap)
- `wikt_sense` entities (sense definitions)
- `inflected_form` entities (per inflection from inflection tables)
- Edges:
  - `wikt_has_sense` (lemma → wikt_sense)
  - `has_etymology` (wikt_sense → text_composition for etymology text)
  - `has_pronunciation` (wikt_sense → text_composition for IPA — through text_decompose, IPA is just Unicode codepoints)
  - `has_form` (lemma → inflected_form)
  - `inflection_of` (inflected_form → lemma)
  - `translation_of` (per translation entry, maps English to non-English lemmas)
  - `synonym_of`, `antonym_of`, `hyponym_of`, etc. (Wiktionary's lexical-relation tags)
  - `has_audio_pronunciation` (wikt_sense → audio_recording for embedded audio links — when present and audio is ingested separately)
- `entity_pos`, `entity_sense`, `entity_morph_feature` junctions

### The pipeline

WiktionaryDecomposer uses `tree-sitter-kaikki-jsonl` grammar (per `20-technical/16-tree-sitter-grammar-strategy.md`) wrapping tree-sitter-json.

1. mmap the JSONL file (avoids loading 22GB into RAM).
2. Use multiple parser threads (per `Substrate Law 5` — concurrency over parsing work, not over substrate emission). Each thread processes a chunk of byte ranges (newline-aligned).
3. For each line (one JSON object per line; one Wiktionary entry):
   - Parse JSON via tree-sitter-json + kaikki schema mapping.
   - text_decompose(word) → lemma_hash.
   - For each sense: text_decompose(definition) → wikt_sense_hash; emit `wikt_has_sense` edge.
   - For each etymology: text_decompose(text) → emit `has_etymology` edge.
   - For each sound (IPA): text_decompose(ipa_text) → emit `has_pronunciation` edge with IPA-derived metadata.
   - For each form (inflection): text_decompose(form) → inflected_form_hash; emit `has_form` and `inflection_of` edges.
   - For each translation: parse target language, text_decompose(translation_word), emit `translation_of` edge.
   - For each lexical relation (`synonyms`, `antonyms`, etc.): emit edges per type.

### Validation gates

- D-entry-count: total entries ingested ≥ expected (millions for full dump)
- D-language-count: 100+ languages represented across translations
- D-ipa-coverage: substantial IPA pronunciation edges (Wiktionary is the substrate's primary IPA source for most languages)
- D-etymology-graph: etymology edges form chains traceable to PIE roots where present

---

## TatoebaDecomposer

### Purpose

Ingest sentence-level translation pairs and audio recordings across 400+ languages, populating cross-lingual sentence alignment and cross-modal `recording_of` edges.

### Source

`D:\Models\tatoeba\`:
- `sentences.csv` — `sentence_id, lang, text` triples
- `links.csv` — `sentence_a_id, sentence_b_id` translation pairs
- `audio/eng/` — English audio recordings (per-sentence MP3 files)
- `sentences_with_audio.csv` — links sentences to audio files
- Tarballs as alternative compressed forms

### Substrate state produced

- `tatoeba_sentence` entities (one per source sentence; same sentence text in different `tatoeba_sentence` rows from different submissions converges via text_decompose)
- `audio_recording` entities (one per audio file; via AudioDecomposer)
- Edges:
  - `has_text` (tatoeba_sentence → text_composition)
  - `translation_link` (sentence_a → sentence_b for translation pairs; bidirectional)
  - `recording_of` (audio_recording → tatoeba_sentence)
  - `has_contributor` (sentence → contributor entity, for attribution)
- `entity_language` junction rows per sentence

### The pipeline

1. Parse `sentences.csv` (CSV with `id\tlang\ttext`):
   - For each sentence: text_decompose(text) → text_composition_hash.
   - Compose tatoeba_sentence entity from text_composition + lang + id metadata.
   - Emit `has_text` edge, `entity_language` junction.
2. Parse `links.csv`:
   - For each link: emit `translation_link` edge between two tatoeba_sentence entities.
3. For each audio file in `audio/eng/`:
   - AudioDecomposer ingests → audio_recording_hash.
   - Lookup linked tatoeba_sentence from `sentences_with_audio.csv`.
   - Emit `recording_of` edge.

### Validation gates

- D-sentence-count: total tatoeba_sentence entities matches Tatoeba's published total (~10M)
- D-language-count: 400+ languages represented
- D-translation-link-coverage: links.csv rows produce equivalent edge count
- D-audio-link-coverage: every English audio file links to at least one sentence

---

## AtomicDecomposer

### Purpose

Ingest ATOMIC 2020's commonsense knowledge graph, providing if-then world-knowledge edges between text compositions.

### Source

`D:\Models\atomic2020_data-feb2021\`:
- `train.tsv` (1,076,880 rows)
- `dev.tsv`, `test.tsv` (smaller eval splits)

Format: `head_text \t relation \t tail_text` per row. ~23 commonsense relation types: `xWant`, `xAttr`, `xEffect`, `xIntent`, `xNeed`, `xReact`, `oEffect`, `oReact`, `oWant`, `AtLocation`, `Causes`, `CapableOf`, `HasProperty`, `HasSubEvent`, `HinderedBy`, `IsAfter`, `IsBefore`, `MadeUpOf`, `NotDesires`, `ObjectUse`, etc.

### Substrate state produced

- `text_composition` entities for each unique head and tail (via text_decompose; head/tail pairs across ATOMIC rows converge with substrate's existing text content where bytes match)
- One edge per row of edge type `atomic_<relation>` between head and tail compositions
- New entries in `ref.edge_type` for each ATOMIC relation if not already registered (one-time at ingestion)
- Edges in commonsense arenas (default arena: `commonsense_relevance`)

### The pipeline

AtomicDecomposer uses `tree-sitter-atomic-tsv` (~30 lines of grammar.js per `20-technical/16-tree-sitter-grammar-strategy.md`).

1. For each TSV row:
   - text_decompose(head) → head_hash.
   - Lookup or register edge_type for the relation column.
   - text_decompose(tail) → tail_hash.
   - Emit edge with edge_type matching the relation, between head and tail.
   - Initialize edge significance with `commonsense_relevance` arena μ from ATOMIC provenance trust prior (1500).

### Validation gates

- D-atomic-row-count: edges emitted matches train.tsv row count (1,076,880 + dev + test)
- D-relation-coverage: 23 distinct ATOMIC relation types in `ref.edge_type`
- D-convergence-with-text: ATOMIC head/tail texts that overlap with WordNet glosses, Tatoeba sentences, etc. converge to shared text_composition entities

---

## Common patterns across seed decomposers

### Pattern: every text-bearing field through text_decompose

Mandatory across every seed decomposer. Code review check: grep for `Blake3.Hash` or `composition_id` calls on text-string-bearing variables in seed-decomposer source. Should return zero matches.

### Pattern: provenance attribution

Each seed decomposer's emitted records carry the seed-source's provenance_id. Cross-source convergence is on entity hashes (same hash from any source); provenance accumulates on edges via `relation_evidence` rows.

### Pattern: validation gates per decomposer

Every seed decomposer has D-* validation gates per `40-process/02-validation-gates.md`. Validation runs as part of CI on substrate releases.

### Pattern: incremental vs bulk

Seed decomposers run as ONE-TIME bulk ingestion phases. They are not incremental. Re-running on the same source produces no new rows (idempotent).

For incremental updates (new UCD release, new UD treebank version, new Wiktionary dump), the decomposer is re-run; new content is added; existing content is unchanged. Determinism (Substrate Law 6) ensures stability.

## Cross-references

- Decomposer contract: `10-architecture/05-decomposer-contract.md`
- Text decomposer (called by every seed for text content): `20-technical/02-text-decomposer.md`
- UCD inventory (UCD's full file structure): `20-technical/14-ucd-inventory.md`
- Tree-sitter grammar strategy (per-format grammars): `20-technical/16-tree-sitter-grammar-strategy.md`
- Provenance catalog (trust priors per source): `20-technical/13-provenance-catalog.md`
- Decomposer checklist: `40-process/checklists/00-decomposer-checklist.md`
- Substrate Law 5 (decomposers as pure producers; no inline SQL): `10-architecture/01-substrate-laws.md`
- Anti-pattern AP-9 (hashing placement metadata): `40-process/01-anti-patterns.md`

## External references

- UCD: <https://www.unicode.org/ucd/>
- UCA / DUCET: <https://unicode.org/reports/tr10/>
- ISO 639-3 registry: <https://iso639-3.sil.org/>
- WordNet: <https://wordnet.princeton.edu/>
- OMW: <http://compling.hss.ntu.edu.sg/omw/>
- Universal Dependencies: <https://universaldependencies.org/>
- Wiktionary / kaikki.org: <https://kaikki.org/dictionary/>
- Tatoeba: <https://tatoeba.org/>
- ATOMIC 2020: <https://arxiv.org/abs/2010.05953>
