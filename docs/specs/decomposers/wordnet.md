# WordNet Decomposer Specification

## Identity

- **Decomposer class**: `WordNetDecomposer` extends `BaseDecomposer`
- **Source path**: `D:\Models\princeton-wordnet\WordNet-3.0\dict\`
- **Trust prior**: High (Princeton University academic curation)
- **Provenance**: `princeton/wordnet/3.0`
- **Dependency**: Phase 2b (ISO 639 must be seeded -- WordNet is English, referenced via `eng` language entity)

## What This Decomposer Creates

The English sense inventory. Synsets, lemmas, word-sense pairs, semantic relations (hypernymy, hyponymy, meronymy, etc.), verb frames, morphological exceptions, lexical categories, sense frequency counts. This is the semantic backbone that gives the substrate typed meaning for English words and provides the relation vocabulary that OMW extends cross-lingually.

## Source Files

### `data.noun`, `data.verb`, `data.adj`, `data.adv`

One file per POS. Each line is a synset. Format:

```
synset_offset lex_filenum ss_type w_cnt word lex_id [word lex_id...] p_cnt [ptr_symbol synset_offset pos source/target...] [frames...] | gloss
```

Fields:
- `synset_offset` -- byte offset (unique synset ID within the file)
- `lex_filenum` -- lexicographer file number (maps to lexnames)
- `ss_type` -- synset type: n=noun, v=verb, a=adjective, s=adjective satellite, r=adverb
- `w_cnt` -- number of words in the synset (hex)
- `word` -- word string (underscores for collocations)
- `lex_id` -- lexical ID (distinguishes words with same form in same synset type)
- `p_cnt` -- number of pointers (semantic relations)
- `ptr_symbol` -- pointer type (see Pointer Types below)
- `synset_offset` (target) -- byte offset of the target synset
- `pos` -- POS of target (n/v/a/r)
- `source/target` -- word numbers within source/target synsets (0000 = all words)
- `frames` (verb only) -- verb subcategorization frame numbers
- `gloss` -- textual definition and/or examples after `|`

### `index.noun`, `index.verb`, `index.adj`, `index.adv`

One file per POS. Alphabetized word lookup. Format:

```
lemma pos synset_cnt p_cnt [ptr_symbol...] sense_cnt tagsense_cnt synset_offset [synset_offset...]
```

Fields:
- `lemma` -- lowercase word (underscores for collocations)
- `pos` -- part of speech (n/v/a/r)
- `synset_cnt` -- number of synsets this word appears in
- `p_cnt` -- number of distinct pointer types for this word
- `ptr_symbol` -- pointer types present
- `sense_cnt` -- same as synset_cnt
- `tagsense_cnt` -- number of senses tagged in corpus
- `synset_offset` -- byte offsets to each synset

### `index.sense`

Sense index mapping sense keys to synsets. Format:

```
sense_key synset_offset sense_number tag_cnt
```

Sense key format: `lemma%ss_type:lex_filenum:lex_id:head_word:head_id`

- `tag_cnt` -- frequency count from semantically tagged corpora (higher = more common sense)

### `lexnames`

45 lexicographer file categories mapping numbers to semantic domains:

| Number | Name | POS |
|--------|------|-----|
| 00 | adj.all | 3 (adj) |
| 01 | adj.pert | 3 |
| 02 | adv.all | 4 (adv) |
| 03 | noun.Tops | 1 (noun) |
| 04 | noun.act | 1 |
| 05 | noun.animal | 1 |
| 06 | noun.artifact | 1 |
| 07 | noun.attribute | 1 |
| 08 | noun.body | 1 |
| 09 | noun.cognition | 1 |
| 10 | noun.communication | 1 |
| 11 | noun.event | 1 |
| 12 | noun.feeling | 1 |
| 13 | noun.food | 1 |
| 14 | noun.group | 1 |
| 15 | noun.location | 1 |
| 16 | noun.motive | 1 |
| 17 | noun.object | 1 |
| 18 | noun.person | 1 |
| 19 | noun.phenomenon | 1 |
| 20 | noun.plant | 1 |
| 21 | noun.possession | 1 |
| 22 | noun.process | 1 |
| 23 | noun.quantity | 1 |
| 24 | noun.relation | 1 |
| 25 | noun.shape | 1 |
| 26 | noun.state | 1 |
| 27 | noun.substance | 1 |
| 28 | noun.time | 1 |
| 29 | verb.body | 2 (verb) |
| 30 | verb.change | 2 |
| 31 | verb.cognition | 2 |
| 32 | verb.communication | 2 |
| 33 | verb.competition | 2 |
| 34 | verb.consumption | 2 |
| 35 | verb.contact | 2 |
| 36 | verb.creation | 2 |
| 37 | verb.emotion | 2 |
| 38 | verb.motion | 2 |
| 39 | verb.perception | 2 |
| 40 | verb.possession | 2 |
| 41 | verb.social | 2 |
| 42 | verb.stative | 2 |
| 43 | verb.weather | 2 |
| 44 | adj.ppl | 3 |

Each is a row in the `lexname` reference table.

### Pointer Types (Semantic Relation Vocabulary)

Confirmed from data parsing (15 distinct symbols across nouns, more across all POS):

| Symbol | Relation | Domain | Description |
|--------|----------|--------|-------------|
| `!` | antonym | n,v,a,r | Antonymy (word-level) |
| `@` | hypernym | n,v | "is-a" (synset-level) |
| `@i` | instance_hypernym | n | Instance-of |
| `~` | hyponym | n,v | Inverse of hypernym |
| `~i` | instance_hyponym | n | Has-instance |
| `#m` | member_holonym | n | Member-of |
| `#s` | substance_holonym | n | Substance-of |
| `#p` | part_holonym | n | Part-of |
| `%m` | member_meronym | n | Has-member |
| `%s` | substance_meronym | n | Has-substance |
| `%p` | part_meronym | n | Has-part |
| `=` | attribute | n,a | Attribute relation |
| `+` | derivationally_related | n,v,a,r | Derivational morphology |
| `;c` | domain_of_synset_topic | n,v,a,r | Domain: topic |
| `-c` | member_of_domain_topic | n | Member of domain: topic |
| `;r` | domain_of_synset_region | n,v,a,r | Domain: region |
| `-r` | member_of_domain_region | n | Member of domain: region |
| `;u` | domain_of_synset_usage | n,v,a,r | Domain: usage |
| `-u` | member_of_domain_usage | n | Member of domain: usage |
| `*` | entailment | v | Verb entailment |
| `>` | cause | v | Cause relation |
| `^` | also_see | v,a | See also |
| `$` | verb_group | v | Verb group |
| `&` | similar_to | a | Similar adjective |
| `<` | participle_of_verb | a | Participle form |
| `\\` | pertainym | a,r | Pertains to / derived from |

Each pointer type becomes a row in the `semantic_relation_type` reference table (which documents the relation vocabulary) AND a corresponding row in the `edge_type` reference table (which operationally types edges in the substrate). The semantic relation vocabulary from WordNet feeds the edge type system — `hypernym`, `hyponym`, `antonym`, etc. are edge_type codes with `category='semantic'` and proper domain/range constraints.

### Verb Subcategorization Frames

35 frames (confirmed from `frames.vrb`):

| Frame | Pattern |
|-------|---------|
| 1 | Something ----s |
| 2 | Somebody ----s |
| 3 | It is ----ing |
| 4 | Something is ----ing PP |
| 5 | Something ----s something Adjective/Noun |
| 6 | Something ----s Adjective/Noun |
| 7 | Somebody ----s Adjective |
| 8 | Somebody ----s something |
| 9 | Somebody ----s somebody |
| 10 | Something ----s somebody |
| 11 | Something ----s something |
| 12 | Something ----s to somebody |
| 13 | Somebody ----s on something |
| 14 | Somebody ----s somebody something |
| 15 | Somebody ----s something to somebody |
| 16 | Somebody ----s something from somebody |
| 17 | Somebody ----s somebody with something |
| 18 | Somebody ----s somebody of something |
| 19 | Somebody ----s something on somebody |
| 20 | Somebody ----s somebody PP |
| 21 | Somebody ----s something PP |
| 22 | Somebody ----s PP |
| 23 | Somebody's (body part) ----s |
| 24 | Somebody ----s somebody to INFINITIVE |
| 25 | Somebody ----s somebody INFINITIVE |
| 26 | Somebody ----s that CLAUSE |
| 27 | Somebody ----s to somebody |
| 28 | Somebody ----s to INFINITIVE |
| 29 | Somebody ----s whether INFINITIVE |
| 30 | Somebody ----s somebody into V-ing something |
| 31 | Somebody ----s something with something |
| 32 | Somebody ----s INFINITIVE |
| 33 | Somebody ----s VERB-ing |
| 34 | It ----s that CLAUSE |
| 35 | Something ----s INFINITIVE |

Each frame is an entity. Verbs relate to their applicable frames via edges.

### Morphological Exceptions

**Files**: `noun.exc`, `verb.exc`, `adj.exc`, `adv.exc`

Format: `inflected_form base_form [base_form...]`

Examples:
- `aardwolves aardwolf`
- `abaci abacus`
- `abscissae abscissa`

These are irregular morphological mappings that standard rules don't cover. Each becomes an edge (edge_type: `irregular_morphology`) from the inflected form entity to the base form entity.

### Sense Frequency Counts

**File**: `cntlist` / `cntlist.rev` (911KB each)

Corpus-derived sense frequency data. Combined with `tag_cnt` in `index.sense`, this provides the content's own ELO signal for sense disambiguation -- more frequently attested senses get higher initial significance.

### Verb Sentence Examples

**Files**: `sentidx.vrb`, `sents.vrb`

Attested example sentences for verb senses. Each becomes an edge from the verb sense entity to the sentence composition entity.

## Entity Model

Synsets, lemmas, and word-senses are entities in the entity table. Semantic relations (hypernymy, antonymy, etc.) are edges in the edge table. POS and sense assignments populate junction tables for fast lookups.

```
-- Entity table rows:
entity: hash=BLAKE3('synset_00001740'), entity_type_id→entity_type('synset')
entity: hash=BLAKE3('entity'), entity_type_id→entity_type('lemma')
entity: hash=BLAKE3('entity%1:03:00::'), entity_type_id→entity_type('word_sense')

-- Reference table rows (populated once):
lexname: code='noun.Tops'
pos: code='NOUN'
sense: synset_offset='00001740', gloss='that which is perceived...', lexname_id→lexname('noun.Tops')

-- Junction table entries (fast application-layer lookups):
entity_sense: entity_id='entity', sense_id→sense('entity%1:03:00::'), mu=derived_from_tag_count
entity_pos: entity_id='entity', pos_id→pos('NOUN'), mu=derived_from_frequency
entity_language: entity_id=synset_00001740, language_id→language('eng')

-- Edges (semantic relations — traversable, significance-weighted):
edge(type='hyponym', source=synset_00001740, target=synset_00001930)  // physical_entity
edge(type='hyponym', source=synset_00001740, target=synset_00002137)  // abstraction
edge(type='in_synset', source=word_sense_entity%1:03:00::, target=synset_00001740)
edge(type='has_word', source=synset_00001740, target=entity_'entity')

-- Sequence (composition structure):
sequence: parent_id='entity', children=[e, n, t, i, t, y] (codepoint references from UCD)
```

Synsets, lemmas, and word-senses are three distinct entity types with different significance profiles. A lemma is a composition of codepoints. A word-sense connects a lemma to a synset via an edge. A synset groups word-senses. Pointer relations connect synsets to synsets (or word-senses for word-level pointers like antonymy and derivation) via the edge table.

## Physicality

- Lemma entities: LINESTRINGZM trajectory from constituent codepoint S3 positions + derived centroid.
- Synset entities: LINESTRINGZM trajectory from constituent lemma centroids + derived centroid.
- Edge geometries: LINESTRINGZM from source entity centroid to target entity centroid (trajectory through participant positions for n-ary edges).

## Significance

- Initial mu derived from `tag_cnt` in index.sense (corpus frequency = content's own ELO signal).
- Context type: `lexical_disambiguation` for sense frequency.
- Source trust prior: High (Princeton academic curation).
- Synsets with more tagged senses start with higher significance for disambiguation.

## Completeness Criteria

- All synsets from data.noun/verb/adj/adv are entities.
- All lemmas from index files are entities (compositions of codepoints).
- All word-sense pairs from index.sense are entities with sense keys.
- All 15+ pointer types are rows in the `semantic_relation_type` / `edge_type` reference table with domain/range constraints.
- All semantic relations between synsets are edges in the edge table.
- All 35 verb frames are entities with verb-to-frame edges.
- All morphological exceptions are edges.
- All lexicographer categories (lexnames) are rows in the `lexname` reference table.
- Sense frequency data populates initial significance + `entity_sense` junction table mu values.
- Verb example sentences are decomposed into substrate content.
- Glosses are decomposed into substrate content (not stored as opaque strings).
- Every entity has physicality (trajectory + centroid).
- Language tagged via `entity_language` junction table entry to `eng`.
- POS assignments populate `entity_pos` junction table.
