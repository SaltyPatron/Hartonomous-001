# Wiktionary Decomposer Specification

## Identity

- **Decomposer class**: `WiktionaryDecomposer` extends `BaseDecomposer`
- **Source path**: `D:\Models\wiktionary\raw-wiktextract-data.jsonl` (20.4GB)
- **Trust prior**: Medium (community-curated via wiki editing, with template/Lua expansion by wiktextract)
- **Provenance**: `wiktextract/raw-wiktextract-data` with per-entry language provenance
- **Dependency**: Phase 2d (UD must be seeded -- Wiktionary morphological data cross-references POS/syntax types from UD). WordNet for sense cross-reference. ISO 639 for language tags.

## What This Decomposer Creates

Broad lexical knowledge: morphology, etymology, pronunciation (IPA + audio URLs), definitions with per-sense granularity, translations across hundreds of languages, inflected forms with grammatical tags, semantic relations (synonyms, antonyms, hypernyms, hyponyms, meronyms, coordinate terms, derived terms, related terms), hyphenation, and categories. Far broader than WordNet in coverage, lower in precision.

## Source Format

JSONL (one JSON object per line). Each line is a word entry for one word in one language at one POS. The same word may have multiple entries (different POS, different etymology numbers).

**File size**: 20.4GB, millions of entries. MUST be streamed line-by-line. Cannot be loaded into memory.

### Top-Level Fields (confirmed from actual data)

| Field | Type | Description | Present in all entries? |
|-------|------|-------------|----------------------|
| `word` | string | Headword | Yes |
| `lang` | string | Language name (English, French, etc.) | Yes |
| `lang_code` | string | ISO 639 code | Yes |
| `pos` | string | Part of speech (noun, verb, adj, adv, etc.) | Yes |
| `senses` | list | Per-sense definitions and metadata | Yes |
| `forms` | list | Inflected forms with grammatical tags | Common |
| `sounds` | list | Pronunciations (IPA, audio) | Common |
| `etymology_text` | string | Etymology narrative text | Common |
| `etymology_templates` | list | Structured etymology data | Common |
| `etymology_number` | int | Which etymology (for words with multiple) | When >1 etymology |
| `translations` | list | Translations to other languages | Common for English entries |
| `synonyms` | list | Synonym relations | When present |
| `antonyms` | list | Antonym relations | When present |
| `hypernyms` | list | Hypernym relations | When present |
| `hyponyms` | list | Hyponym relations | When present |
| `meronyms` | list | Meronym relations | When present |
| `coordinate_terms` | list | Coordinate term relations | When present |
| `derived` | list | Derived terms | When present |
| `related` | list | Related terms | When present |
| `categories` | list | Wiki categories | When present |
| `head_templates` | list | Headword template data | Common |
| `hyphenations` | list | Syllable/hyphenation patterns | When present |

### `senses` Structure (per-sense, confirmed from data)

Each sense is a dict with:

| Field | Type | Description |
|-------|------|-------------|
| `glosses` | list[str] | Definition text(s) |
| `raw_glosses` | list[str] | Unprocessed gloss text |
| `tags` | list[str] | Grammatical/usage tags (transitive, countable, informal, etc.) |
| `categories` | list[str] | Per-sense categories |
| `examples` | list[dict] | Usage examples with `text`, `type`, optional `bold_text_offsets`, optional `ref` |
| `links` | list[list[str]] | Wiki links within the definition |
| `senseid` | list[str] | Sense identifier(s) |
| `wikidata` | list[str] | Wikidata Q-identifiers |
| `attestations` | list[dict] | Historical attestations with `date`, `references` |
| `synonyms` | list[dict] | Per-sense synonyms |
| `antonyms` | list[dict] | Per-sense antonyms |
| `hypernyms` | list[dict] | Per-sense hypernyms |
| `hyponyms` | list[dict] | Per-sense hyponyms |
| `coordinate_terms` | list[dict] | Per-sense coordinate terms |

### `forms` Structure (confirmed from data)

Each form is a dict:

| Field | Type | Description |
|-------|------|-------------|
| `form` | string | The inflected form text |
| `tags` | list[str] | Grammatical tags (plural, singular, present, past, participle, comparative, superlative, third-person, etc.) |

### `sounds` Structure (confirmed from data)

Each sound is a dict:

| Field | Type | Description |
|-------|------|-------------|
| `ipa` | string | IPA transcription |
| `enpr` | string | English pronunciation key |
| `tags` | list[str] | Dialect/variety tags (Received-Pronunciation, General-American, etc.) |
| `audio` | string | Audio filename |
| `ogg_url` | string | OGG audio URL |
| `mp3_url` | string | MP3 audio URL |

### `etymology_templates` Structure (confirmed from data)

Each template is a dict:

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | Template name (etymon, inh, der, bor, etc.) |
| `args` | dict | Template arguments (numbered + named) |
| `expansion` | string | Expanded text output |

Etymology templates encode structured derivation chains: which language a word was inherited from, borrowed from, derived from, etc. The `args` contain source language, source word, and derivation type.

### `translations` Structure (confirmed from data)

Each translation is a dict:

| Field | Type | Description |
|-------|------|-------------|
| `lang` | string | Target language name |
| `code` | string | Target language ISO 639 code |
| `lang_code` | string | Same as code |
| `word` | string | Translated word |
| `sense` | string | Which sense this translation is for |
| `roman` | string | Romanization (for non-Latin scripts) |
| `tags` | list[str] | Tags (gender, number, formality, etc.) |

### Relation Fields (`synonyms`, `antonyms`, `hypernyms`, `hyponyms`, `meronyms`, `coordinate_terms`, `derived`, `related`)

Each item is a dict:

| Field | Type | Description |
|-------|------|-------------|
| `word` | string | Related word |
| `source` | string | Source (e.g., "Thesaurus:dictionary") |
| `tags` | list[str] | Qualifying tags (US, UK, etc.) |

### `hyphenations` Structure

Each hyphenation is a dict:

| Field | Type | Description |
|-------|------|-------------|
| `parts` | list[str] | Syllable parts (e.g., ["dic", "tion", "a", "ry"]) |

## Entity Model

Lemmas, senses, inflected forms, and translations are entities in the entity table. POS and sense assignments populate junction tables. All semantic relations, etymology chains, and cross-references are edges.

```
-- Entity table rows:
entity: hash=BLAKE3('dictionary'), entity_type_id→entity_type('lemma')
entity: hash=BLAKE3(dict_sense_1), entity_type_id→entity_type('wikt_sense')
entity: hash=BLAKE3('dictionaries'), entity_type_id→entity_type('inflected_form')

-- Sequence:
sequence: parent_id='dictionary', children=[d, i, c, t, i, o, n, a, r, y]

-- Junction table entries (fast application-layer lookups):
entity_pos: entity_id='dictionary', pos_id→pos('NOUN'), mu=frequency_derived
entity_sense: entity_id='dictionary', sense_id→sense(dict_sense_1), mu=frequency_derived
entity_language: entity_id='dictionary', language_id→language('eng')

-- Edges (semantic relations — traversable, significance-weighted):
edge(type='has_sense', source=Entity('dictionary'), target=dict_sense_1)
edge(type='has_gloss', source=dict_sense_1, target=Entity('A reference work...'))
edge(type='has_example', source=dict_sense_1, target=Entity('If you want to know...'))
edge(type='synonym', source=dict_sense_1, target=Entity('lexicon'))
edge(type='hypernym', source=dict_sense_1, target=Entity('wordbook'))
edge(type='has_form', source=Entity('dictionary'), target=Entity('dictionaries'))
edge(type='inflection_of', source=Entity('dictionaries'), target=Entity('dictionary'))

-- Junction table entry for morphological feature (fast application-layer lookup):
entity_morph_feature: entity_id='dictionaries', morph_feature_id→morph_feature('Number=Plur')
edge(type='has_hyphenation', source=Entity('dictionary'), target=syllabification(['dic','tion','a','ry']))
edge(type='has_pronunciation', source=Entity('dictionary'), target=Entity('/ˈdɪk.ʃə.nə.ɹi/'))
edge(type='has_etymology', source=Entity('dictionary'), target=etym_chain)
edge(type='has_wikidata', source=dict_sense_1, target=Entity('Q23622'))

-- Translation edge (n-ary — language expressed via entity_language junction on target):
edge(type='translation_of', members=[
    (entity=dict_sense_1, role='source_sense'),
    (entity=Entity('dictionnaire'), role='target_word')
])
-- Target word's language is expressed via entity_language junction table:
entity_language: entity_id='dictionnaire', language_id→language('fra')
```

### Cross-references

- Wiktionary lemmas that match WordNet lemmas in the same language get a cross-reference edge.
- Wiktionary POS tags map to UD UPOS values in the `pos` reference table where equivalent.
- Translation targets link to OMW lemma entities where they exist via edges.
- Wikidata IDs are preserved as edges for external knowledge graph linkage.

## Streaming Strategy

The 20.4GB file MUST be streamed:

1. Open file in line-by-line read mode.
2. Parse each JSON line.
3. Decompose into entities + edges.
4. Hash and deduplicate within a configurable batch window (e.g., 10,000 entries).
5. Batch-check existence against substrate.
6. Submit batch through centralized pipeline.
7. Checkpoint after each batch (record last byte offset or line number for resume).
8. Repeat until EOF.

Memory usage is bounded by batch size, not file size. Resume is by byte offset.

## Significance

- Trust prior: Medium (community curation).
- Per-entry confidence derived from:
  - Number of senses (more senses = more thoroughly documented).
  - Presence of examples and attestations.
  - Number of translations (heavily translated entries are typically higher quality).
- Entries that corroborate WordNet senses get significance boost.
- Entries that contradict WordNet enter arena.

## Analysis Passes

- `EtymologyChainPass` -- parse etymology templates into structured derivation chains (inherited-from, borrowed-from, derived-from relations between language-specific word entities)
- `MorphologicalParadigmPass` -- extract inflectional paradigms from forms + tags (reuses UD paradigm types)
- `PronunciationVariantPass` -- extract dialect-tagged pronunciation variants as edges
- `TranslationGraphPass` -- build cross-lingual translation edges, linking to OMW alignments where they exist
- `SenseAlignmentPass` -- attempt alignment between Wiktionary senses and WordNet synsets via gloss/hypernym matching

## Completeness Criteria

- Every line of the 20.4GB JSONL is processed.
- Every word/lang/pos combination is an entity.
- Every sense is a separate entity with per-sense relations.
- Every form is a separate entity with inflection relations.
- Every pronunciation (IPA and audio URL) is stored.
- Every etymology template is decomposed into structured relations.
- Every translation is a typed cross-lingual edge with language tagging via `entity_language` junction.
- Every semantic relation (synonym, antonym, hypernym, etc.) is an edge.
- Hyphenation/syllabification is stored as a composition.
- Streaming with checkpointed resume -- crash at line N resumes at line N.
- Cross-references to WordNet, UD, and OMW entities where applicable.
- Wikidata IDs preserved for external linkage.
- ZERO opaque string blobs. Every text field is decomposed into codepoint compositions.
