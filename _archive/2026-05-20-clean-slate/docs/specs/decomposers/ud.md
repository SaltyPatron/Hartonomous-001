# Universal Dependencies Decomposer Specification

## Identity

- **Decomposer class**: `UdDecomposer` extends `BaseDecomposer`
- **Source path**: `D:\Models\ud-treebanks\ud-treebanks-v2.17\`
- **Trust prior**: High (academic, per-treebank provenance)
- **Provenance**: `universaldependencies/v2.17` with per-treebank sub-provenance (e.g., `universaldependencies/v2.17/UD_English-EWT`)
- **Dependency**: Phase 2c (WordNet/OMW must be seeded -- UD lemmas should cross-reference existing sense entities where possible). ISO 639 for language tags.

## What This Decomposer Creates

Syntactic structure types: dependency relations, POS tags, morphological features, and attested sentence structures across 100+ languages and 339 treebanks. This gives the substrate the grammar to decompose any sentence into typed structural relations.

## Source Format

### Treebank Organization

339 treebanks (confirmed), each in a directory `UD_{Language}-{Treebank}`. Each treebank contains:
- `{lang}_{treebank}-ud-train.conllu` -- training data
- `{lang}_{treebank}-ud-dev.conllu` -- development data
- `{lang}_{treebank}-ud-test.conllu` -- test data
- `README.md` -- treebank documentation (contributors, license, genre, etc.)
- `LICENSE.txt` -- per-treebank license
- `stats.xml` -- treebank statistics

Not all treebanks have all three splits. Some have only test data.

### CoNLL-U Format (10 columns, tab-separated)

Each token is one line. Blank lines separate sentences. Comment lines start with `#`.

| Column | Name | Description | Values |
|--------|------|-------------|--------|
| 1 | ID | Word index | Integer (1-based), range (1-2 for MWTs), decimal (0.1 for empty nodes) |
| 2 | FORM | Word form / punctuation symbol | UTF-8 text |
| 3 | LEMMA | Lemma or stem | UTF-8 text |
| 4 | UPOS | Universal POS tag | See UPOS inventory below |
| 5 | XPOS | Language-specific POS tag | Varies per treebank, `_` if absent |
| 6 | FEATS | Morphological features | `Key=Value\|Key=Value...` or `_` |
| 7 | HEAD | Head word ID | Integer (0 = root) |
| 8 | DEPREL | Universal dependency relation | See DEPREL inventory below |
| 9 | DEPS | Enhanced dependencies | `head:deprel\|head:deprel...` or `_` |
| 10 | MISC | Miscellaneous annotations | `Key=Value\|Key=Value...` or `_` |

### Sentence-level metadata (comment lines)

- `# sent_id = ...` -- unique sentence identifier
- `# text = ...` -- raw sentence text
- `# text_name = ...` -- source document name (some treebanks)
- `# text_orth = ...` -- orthographic segmentation (some treebanks)
- `# text_transcription = ...` -- IPA transcription (some treebanks)
- `# text_rus = ...` -- Russian translation (some treebanks, e.g., Abaza)
- Other treebank-specific metadata

### UPOS Inventory (17 tags + underscore, confirmed from data)

| Tag | Description |
|-----|-------------|
| `ADJ` | Adjective |
| `ADP` | Adposition |
| `ADV` | Adverb |
| `AUX` | Auxiliary |
| `CCONJ` | Coordinating conjunction |
| `DET` | Determiner |
| `INTJ` | Interjection |
| `NOUN` | Noun |
| `NUM` | Numeral |
| `PART` | Particle |
| `PRON` | Pronoun |
| `PROPN` | Proper noun |
| `PUNCT` | Punctuation |
| `SCONJ` | Subordinating conjunction |
| `SYM` | Symbol |
| `VERB` | Verb |
| `X` | Other |
| `_` | Unspecified |

### DEPREL Inventory (70+ values, confirmed from data sampling)

Universal relations (37 core):
`acl`, `advcl`, `advmod`, `amod`, `appos`, `aux`, `case`, `cc`, `ccomp`, `clf`, `compound`, `conj`, `cop`, `csubj`, `dep`, `det`, `discourse`, `dislocated`, `expl`, `fixed`, `flat`, `goeswith`, `iobj`, `list`, `mark`, `nmod`, `nsubj`, `nummod`, `obj`, `obl`, `orphan`, `parataxis`, `punct`, `reparandum`, `root`, `vocative`, `xcomp`

Language-specific subtypes (confirmed from data, 33+ subtypes):
`acl:relcl`, `advcl:compar`, `advcl:cond`, `advcl:conv`, `advcl:purp`, `advcl:quote`, `advcl:seq`, `advmod:emph`, `advmod:q`, `aux:pass`, `ccomp:iobj`, `ccomp:lo`, `ccomp:obj`, `ccomp:poss`, `ccomp:purp`, `ccomp:quote`, `ccomp:ro`, `compound:pred`, `compound:prt`, `conj:q`, `csubj:outer`, `csubj:quote`, `dep:repeat`, `det:poss`, `flat:name`, `iobj:cs`, `iobj:lo`, `iobj:po`, `iobj:poss`, `iobj:ro`, `nmod:poss`, `nmod:quote`, `nsubj:outer`, `nsubj:pass`, `xcomp:lo`, `xcomp:subj`

Each is a row in the `deprel` reference table with domain/range constraints and parent hierarchy.

### Morphological Feature Keys (68+ confirmed from data sampling)

Core features: `Animacy`, `Aspect`, `Case`, `Caus`, `Definite`, `Degree`, `Evident`, `ExtPos`, `Gender`, `Int`, `Mood`, `NameType`, `NounBase`, `NumType`, `Number`, `PartType`, `Person`, `Polarity`, `Poss`, `PronType`, `RcpType`, `Reflex`, `RelType`, `Reln`, `Subcat`, `Subordinative`, `Tense`, `Ventive`, `VerbForm`, `VerbStem`, `VerbType`, `Voice`

Layered features (with agreement brackets): `Gender[abs]`, `Gender[cs]`, `Gender[erg]`, `Gender[io]`, `Gender[lo]`, `Gender[obj]`, `Gender[po]`, `Gender[psor]`, `Gender[refl]`, `Gender[ro]`, `Gender[subj]`, `Number[abs]`, `Number[cs]`, `Number[erg]`, `Number[io]`, `Number[lo]`, `Number[obj]`, `Number[po]`, `Number[psor]`, `Number[refl]`, `Number[ro]`, `Number[subj]`, `Person[abs]`, `Person[cs]`, `Person[erg]`, `Person[io]`, `Person[lo]`, `Person[obj]`, `Person[po]`, `Person[psor]`, `Person[refl]`, `Person[ro]`, `Person[subj]`

Language-specific features: `AdjType`, `AdpType`, `Dyn`

Each feature key and each feature value is a row in the `morph_feature` reference table (keyed on key+value compound). Feature assignments are junction table entries or edges.

## Entity Model

Sentences and tokens are entities in the entity table. UPOS and DEPREL values are reference table rows. POS assignments populate the `entity_pos` junction table. Morphological feature assignments populate the `entity_morph_feature` junction table. Dependency arcs are edges typed with the specific deprel value (e.g., edge_type='nsubj', 'amod'), connecting head and dependent token entities.

```
-- Entity table rows:
entity: hash=BLAKE3('sent_dev-s1'), entity_type_id→entity_type('ud_sentence')
entity: hash=BLAKE3('token_1_of_dev-s1'), entity_type_id→entity_type('ud_token')
entity: hash=BLAKE3('En'), entity_type_id→entity_type('word_form')
entity: hash=BLAKE3('en'), entity_type_id→entity_type('lemma')

-- Reference table rows (populated during this phase, shared globally):
pos: code='CCONJ'
pos: code='PRON'
deprel: code='amod'
deprel: code='nsubj'
morph_feature: key='Case', value='Acc,Nom'
morph_feature: key='Number', value='Plur'
morph_feature: key='Person', value='1'
morph_feature: key='PronType', value='Prs'

-- Junction table entries (fast application-layer lookups):
entity_pos: entity_id='En', pos_id→pos('CCONJ'), mu=frequency_derived
entity_pos: entity_id='ons', pos_id→pos('PRON'), mu=frequency_derived
entity_language: entity_id=sent_dev-s1, language_id→language('afr')

-- Edges (syntactic structure — traversable, significance-weighted):
edge(type='amod', members=[
    (entity=token_1, role='dependent'),
    (entity=token_3, role='head')
])
edge(type='has_form', source=token_1, target=Entity('En'))
edge(type='has_lemma', source=token_1, target=Entity('en'))

-- Junction table entries for morphological features (fast application-layer lookups):
entity_morph_feature: entity_id=token_2, morph_feature_id→morph_feature('Case=Acc,Nom')
entity_morph_feature: entity_id=token_2, morph_feature_id→morph_feature('Number=Plur')

-- Sequence (sentence structure):
sequence: parent_id=sent_dev-s1, children=[token_1, token_2, token_3, ...]
```

Shared values:
- UPOS "CCONJ" is ONE row in the `pos` reference table, looked up via `entity_pos` junction by every coordinating conjunction across all treebanks.
- DEPREL "nsubj" is ONE row in the `deprel` reference table, referenced by every nominal subject dependency edge across all treebanks.
- Feature values like "Number=Plur" are ONE row in the `morph_feature` reference table, shared across all tokens with that feature.
- Lemma "en" (Afrikaans "and") is a composition of codepoints in the entity table, shared if the same string appears elsewhere.

### Cross-reference to WordNet

Where a UD lemma matches a WordNet lemma in the same language (English), a cross-reference relation is created linking the UD lemma entity to the WordNet lemma entity. This connects syntactic and semantic knowledge.

## Physicality

- Token entities: POINTZM from word form centroid.
- Sentence entities: LINESTRINGZM trajectory from token centroids in sequence order.
- Dependency edge geometries: LINESTRINGZM from dependent centroid to head centroid.

## Significance

- Per-treebank trust prior based on treebank documentation (size, genre diversity, annotation quality).
- Token-level significance derived from treebank frequency.
- Dependency pattern significance from how many treebanks attest the same pattern.

## Per-Treebank Handling

Each treebank is processed independently. Different treebanks for the same language (e.g., UD_English-EWT vs UD_English-GUM) provide corroborating or contrasting evidence. When multiple treebanks agree on a syntactic pattern, corroboration strengthens it. When they disagree, the arena resolves it.

Treebank-specific metadata (README.md, LICENSE.txt, stats.xml) is recorded as provenance.

## Analysis Passes

- `DeprelPatternPass` -- extract common dependency subtree patterns as reusable substrate compositions
- `MorphologicalParadigmPass` -- extract inflectional paradigms from form/lemma/feat combinations
- `SentenceStructurePass` -- classify sentences by structural type (SVO, SOV, VSO, etc.)
- `TreebankCorroborationPass` -- identify patterns attested across multiple treebanks for the same language
- `CrossLingualSyntaxPass` -- identify syntactic universals vs language-specific patterns

## Completeness Criteria

- All 339 treebanks processed.
- All CoNLL-U files (train/dev/test) in every treebank ingested.
- All 17 UPOS tags are rows in the `pos` reference table.
- All 70+ DEPREL values (including subtypes) are rows in the `deprel` reference table with domain/range constraints and parent hierarchy.
- All 68+ morphological feature keys and their values are rows in the `morph_feature` reference table.
- Every token has edges for FORM, LEMMA. POS populated via `entity_pos` junction table. HEAD/DEPREL as dependency edges typed with the specific deprel value (e.g., edge_type='nsubj', 'amod').
- Feature assignments populated via `entity_morph_feature` junction table entries when FEATS is not `_` (sparsity).
- XPOS assignments only present when XPOS is not `_` (sparsity).
- Enhanced dependencies (DEPS column) processed where present.
- Multi-word tokens (ID ranges like 1-2) handled correctly.
- Empty nodes (decimal IDs like 0.1) handled correctly.
- Per-treebank provenance, license, and metadata recorded.
- Cross-reference to WordNet lemmas where applicable.
- Language tags reference existing `language` reference table rows via `entity_language` junction.
