# Reference Table DDL

**Status**: ✅ Complete

Reference tables are the substrate's classification vocabulary. Small, properly normalized lookup tables populated during seed ingestion. They are NOT entities — they are infrastructure that enables the substrate to process.

Every reference table follows the same structural pattern:
- `id SERIAL PRIMARY KEY` — surrogate key for FK efficiency
- `code VARCHAR NOT NULL UNIQUE` — natural key, human-readable, what application code uses
- Domain-specific columns where needed
- `parent_id INT REFERENCES <self>(id)` — optional hierarchy within the domain

All tables live in the `substrate` schema. All are read-heavy, write-rare after seed ingestion.

---

## Entity Type

Structural classification of entities. Determines what kind of content an entity is.

```sql
CREATE TABLE substrate.entity_type (
    id        SERIAL PRIMARY KEY,
    code      VARCHAR(64) NOT NULL UNIQUE,
    modality  VARCHAR(32) NOT NULL,
    parent_id INT REFERENCES substrate.entity_type(id)
);

CREATE INDEX idx_entity_type_modality ON substrate.entity_type(modality);

COMMENT ON TABLE substrate.entity_type IS 'Structural classification of entities by content kind and modality.';
```

**Row count**: ~25 initial, grows as new modalities/formats are added.
**Populated by**: Phase 1 seed script (bootstrap).
**Values (corrected per [`docs/00-substrate-spec.md`](../../00-substrate-spec.md) §II.1):** Content entity types: `codepoint`, `grapheme_cluster`, `word_form`, `morpheme`, `lemma`, `text_composition`, `paragraph`, `document`, `synset`, `collation_element`, `language_name`, `pixel_region`, `audio_recording`, `audio_chunk`, `video_frame`. Real model-side structural artifact entities: `tensor`, `model_architecture`, `tokenizer_model`. **Phantom entity types previously listed here (`attention_pattern`, `bpe_token`, `ud_sentence`, `ud_token`, `tatoeba_sentence`, `word_sense`, `wikt_sense`, `inflected_form`) are deprecated by the 2026-05-08 architectural correction.** These phantom types have been removed from entity_type.sql (the 2026-05-08 correction is fully applied; entity_type.sql now has 23 real content types, no phantom rows). New code MUST emit attestation edges between content entities (per the layer-type decomposer library at [`docs/specs/decomposers/layer-type-library.md`](../decomposers/layer-type-library.md)) instead of phantom per-role-unit entities. Tokenizer vocabulary tokens are `word_form` content entities (content-addressed via BLAKE3 of UTF-8 bytes through `SubstrateTextDecomposer`), NOT a separate `bpe_token` entity type. See AP-25 in `.claude/rules/45-anti-patterns.md`.

See [seed-scripts.md](seed-scripts.md) for full INSERT statements with modality assignments.

---

## Edge Type

Operational typing for edges. Every edge has exactly one edge_type. Domain/range constraints enforce which entity types can participate.

```sql
CREATE TABLE substrate.edge_type (
    id             SERIAL PRIMARY KEY,
    code           VARCHAR(64) NOT NULL UNIQUE,
    category       VARCHAR(32) NOT NULL,
    source_type_id INT REFERENCES substrate.entity_type(id),
    target_type_id INT REFERENCES substrate.entity_type(id)
);

CREATE INDEX idx_edge_type_category ON substrate.edge_type(category);

COMMENT ON TABLE substrate.edge_type IS 'Operational edge typing with domain/range entity type constraints.';
COMMENT ON COLUMN substrate.edge_type.category IS 'semantic, syntactic, morphological, cross_lingual, cross_modal, model_derived, structural, unicode';
COMMENT ON COLUMN substrate.edge_type.source_type_id IS 'FK to entity_type — constrains which entity types can be the source of this edge type.';
COMMENT ON COLUMN substrate.edge_type.target_type_id IS 'FK to entity_type — constrains which entity types can be the target of this edge type.';
```

**Row count**: ~150+ (26 semantic + 70 syntactic + morphological + cross-lingual + cross-modal + model-derived + structural + unicode).
**Populated by**: Phase 1 seed script (base structural/cross-modal/model types), then UD decomposer (syntactic deprel→edge_type), WordNet decomposer (semantic relation→edge_type).
**Categories**: `semantic`, `syntactic`, `morphological`, `cross_lingual`, `cross_modal`, `model_derived`, `structural`, `unicode`.

### Domain/Range Constraints by Category

| Category | Example Code | Source Type | Target Type |
|----------|-------------|-------------|-------------|
| semantic | `hypernym` | synset | synset |
| semantic | `antonym` | word_sense | word_sense |
| semantic | `derivationally_related` | word_sense | word_sense |
| syntactic | `nsubj` | ud_token | ud_token |
| morphological | `has_sense` | lemma | synset |
| morphological | `has_form` | lemma | inflected_form |
| morphological | `has_lemma` | word_form | lemma |
| morphological | `has_morpheme` | word_form | morpheme |
| cross_lingual | `aligned_to_synset` | lemma | synset |
| cross_lingual | `translation_of` | wikt_sense | lemma |
| cross_lingual | `translation_link` | tatoeba_sentence | tatoeba_sentence |
| cross_modal | `recording_of` | audio_recording | tatoeba_sentence |
| unicode | `maps_to_lowercase` | codepoint | codepoint |
| model_derived | `in_model` | tensor | model_architecture |
| model_derived | `co_occurrence` | (any) | (any) |

### Decision: Bootstrap vs. Decomposer-Created

Base structural, cross-modal, model-derived, and unicode edge types are created in Phase 1 bootstrap. Semantic edge types are created by the WordNet decomposer. Syntactic edge types (one per deprel value) are created by the UD decomposer. Each decomposer registers its own edge types during its initialization phase.

---

## Edge Role

Roles that participants play in n-ary edges.

```sql
CREATE TABLE substrate.edge_role (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(32) NOT NULL UNIQUE
);

COMMENT ON TABLE substrate.edge_role IS 'Participant roles in n-ary edges.';
```

**Row count**: 7.
**Populated by**: Phase 1 seed script.
**Values**: `source`, `target`, `context`, `mediator`, `evidence`, `head`, `dependent`.

---

## POS (Part of Speech)

17 UPOS top-level categories with hierarchical subtypes via `parent_id`.

```sql
CREATE TABLE substrate.pos (
    id        SERIAL PRIMARY KEY,
    code      VARCHAR(32) NOT NULL UNIQUE,
    parent_id INT REFERENCES substrate.pos(id)
);

COMMENT ON TABLE substrate.pos IS 'Part of speech classification. 17 UPOS + hierarchical subtypes.';
```

**Row count**: 17 top-level + subtypes (grows with Wiktionary ingestion).
**Populated by**: UD decomposer (17 UPOS), Wiktionary decomposer (subtypes like `countable_noun`, `proper_noun`).
**Top-level values**: `ADJ`, `ADP`, `ADV`, `AUX`, `CCONJ`, `DET`, `INTJ`, `NOUN`, `NUM`, `PART`, `PRON`, `PROPN`, `PUNCT`, `SCONJ`, `SYM`, `VERB`, `X`.

Subtypes reference parent: `countable_noun` → parent=`NOUN`, `transitive_verb` → parent=`VERB`.

---

## Deprel (Dependency Relations)

37 universal + 33+ language-specific subtypes.

```sql
CREATE TABLE substrate.deprel (
    id        SERIAL PRIMARY KEY,
    code      VARCHAR(32) NOT NULL UNIQUE,
    parent_id INT REFERENCES substrate.deprel(id)
);

COMMENT ON TABLE substrate.deprel IS 'Dependency relation types. 37 universal + language-specific subtypes.';
```

**Row count**: 70+.
**Populated by**: UD decomposer.
**Universal (37)**: `acl`, `advcl`, `advmod`, `amod`, `appos`, `aux`, `case`, `cc`, `ccomp`, `clf`, `compound`, `conj`, `cop`, `csubj`, `dep`, `det`, `discourse`, `dislocated`, `expl`, `fixed`, `flat`, `goeswith`, `iobj`, `list`, `mark`, `nmod`, `nsubj`, `nummod`, `obj`, `obl`, `orphan`, `parataxis`, `punct`, `reparandum`, `root`, `vocative`, `xcomp`.
**Subtypes**: `nsubj:pass` → parent=`nsubj`, `acl:relcl` → parent=`acl`, `flat:name` → parent=`flat`, `csubj:outer` → parent=`csubj`, etc.

---

## Morph Feature (Morphological Features)

68+ feature keys, each with multiple values. Stored as (key, value) pairs.

```sql
CREATE TABLE substrate.morph_feature (
    id        SERIAL PRIMARY KEY,
    key       VARCHAR(32) NOT NULL,
    value     VARCHAR(32) NOT NULL,
    parent_id INT REFERENCES substrate.morph_feature(id),
    UNIQUE(key, value)
);

CREATE INDEX idx_morph_feature_key ON substrate.morph_feature(key);

COMMENT ON TABLE substrate.morph_feature IS 'Morphological feature key-value pairs. Each row = one (key, value) pair.';
COMMENT ON COLUMN substrate.morph_feature.parent_id IS 'Groups values under a common feature key row.';
```

**Row count**: ~500+ (68 keys × multiple values each).
**Populated by**: UD decomposer.
**Key categories**: Nominal (`Animacy`, `Case`, `Definite`, `Degree`, `Gender`, `Number`, `NumType`), Pronominal (`Person`, `Poss`, `PronType`, `Reflex`), Verbal (`Aspect`, `Mood`, `Polarity`, `Tense`, `VerbForm`, `Voice`), Other (`AdjType`, `AdpType`, `Subordinative`, `Ventive`, `ExtPos`).

Each feature key gets a parent row (e.g., `key='Case', value='_GROUP'` with `parent_id=NULL`), and each value row references that parent (e.g., `key='Case', value='Nom'` with `parent_id=<Case group row>`).

See [type-system.md](../../type-system.md) § Morphological Feature Values for complete value inventory.

---

## Sense (WordNet Synset Inventory)

```sql
CREATE TABLE substrate.sense (
    id         SERIAL PRIMARY KEY,
    code       VARCHAR(32) NOT NULL UNIQUE,
    gloss      TEXT NOT NULL,
    lexname_id INT REFERENCES substrate.lexname(id),
    pos_id     INT REFERENCES substrate.pos(id)
);

COMMENT ON TABLE substrate.sense IS 'WordNet synset inventory. code = synset offset + POS indicator (e.g., 02084071-n).';
COMMENT ON COLUMN substrate.sense.gloss IS 'Human-readable definition from WordNet.';
```

**Row count**: ~117,659 (WordNet 3.0 synset count).
**Populated by**: WordNet decomposer.
**Code format**: `{offset}-{pos}` where offset is the synset's byte offset in the data file and pos is `n` (noun), `v` (verb), `a` (adjective), `r` (adverb), `s` (adjective satellite).

---

## Lexname (Lexicographer Categories)

```sql
CREATE TABLE substrate.lexname (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(32) NOT NULL UNIQUE
);

COMMENT ON TABLE substrate.lexname IS 'WordNet lexicographer categories. 45 values.';
```

**Row count**: 45.
**Populated by**: WordNet decomposer.
**Values**: `adj.all`, `adj.pert`, `adj.ppl`, `adv.all`, `noun.Tops`, `noun.act`, `noun.animal`, `noun.artifact`, `noun.attribute`, `noun.body`, `noun.cognition`, `noun.communication`, `noun.event`, `noun.feeling`, `noun.food`, `noun.group`, `noun.location`, `noun.motive`, `noun.object`, `noun.person`, `noun.phenomenon`, `noun.plant`, `noun.possession`, `noun.process`, `noun.quantity`, `noun.relation`, `noun.shape`, `noun.state`, `noun.substance`, `noun.time`, `verb.body`, `verb.change`, `verb.cognition`, `verb.communication`, `verb.competition`, `verb.consumption`, `verb.contact`, `verb.creation`, `verb.emotion`, `verb.motion`, `verb.perception`, `verb.possession`, `verb.social`, `verb.stative`, `verb.weather`.

---

## Semantic Relation Type

WordNet pointer type vocabulary. Documents the classification system — operational edge typing uses `edge_type` with the same codes.

```sql
CREATE TABLE substrate.semantic_relation_type (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(32) NOT NULL UNIQUE
);

COMMENT ON TABLE substrate.semantic_relation_type IS 'WordNet semantic relation vocabulary. 26 pointer types.';
```

**Row count**: 26.
**Populated by**: WordNet decomposer.
**Values**: `antonym`, `hypernym`, `instance_hypernym`, `hyponym`, `instance_hyponym`, `member_holonym`, `substance_holonym`, `part_holonym`, `member_meronym`, `substance_meronym`, `part_meronym`, `attribute`, `derivationally_related`, `domain_of_synset_topic`, `member_of_domain_topic`, `domain_of_synset_region`, `member_of_domain_region`, `domain_of_synset_usage`, `member_of_domain_usage`, `entailment`, `cause`, `also_see`, `verb_group`, `similar_to`, `participle_of_verb`, `pertainym`.

**Note**: These codes also appear as `edge_type.code` values with `category='semantic'`. The `semantic_relation_type` table documents the vocabulary; `edge_type` operationally types edges.

---

## General Category (Unicode)

30 values in 7 groups.

```sql
CREATE TABLE substrate.general_category (
    id          SERIAL PRIMARY KEY,
    code        VARCHAR(4) NOT NULL UNIQUE,
    group_code  VARCHAR(1) NOT NULL,
    description VARCHAR(64) NOT NULL
);

CREATE INDEX idx_general_category_group ON substrate.general_category(group_code);

COMMENT ON TABLE substrate.general_category IS 'Unicode General Category property. 30 values in 7 groups (L, M, N, P, S, Z, C).';
```

**Row count**: 30.
**Populated by**: UCD decomposer.
**Values**:

| Code | Group | Description |
|------|-------|-------------|
| `Lu` | L | Uppercase letter |
| `Ll` | L | Lowercase letter |
| `Lt` | L | Titlecase letter |
| `Lm` | L | Modifier letter |
| `Lo` | L | Other letter |
| `Mn` | M | Nonspacing mark |
| `Mc` | M | Spacing combining mark |
| `Me` | M | Enclosing mark |
| `Nd` | N | Decimal digit number |
| `Nl` | N | Letter number |
| `No` | N | Other number |
| `Pc` | P | Connector punctuation |
| `Pd` | P | Dash punctuation |
| `Ps` | P | Open punctuation |
| `Pe` | P | Close punctuation |
| `Pi` | P | Initial quote punctuation |
| `Pf` | P | Final quote punctuation |
| `Po` | P | Other punctuation |
| `Sm` | S | Math symbol |
| `Sc` | S | Currency symbol |
| `Sk` | S | Modifier symbol |
| `So` | S | Other symbol |
| `Zs` | Z | Space separator |
| `Zl` | Z | Line separator |
| `Zp` | Z | Paragraph separator |
| `Cc` | C | Control |
| `Cf` | C | Format |
| `Cs` | C | Surrogate |
| `Co` | C | Private use |
| `Cn` | C | Not assigned |

---

## Script (Unicode)

160+ Unicode script values.

```sql
CREATE TABLE substrate.script (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);

COMMENT ON TABLE substrate.script IS 'Unicode Script property. 160+ scripts, grows per Unicode version.';
```

**Row count**: 160+ (grows with each Unicode version).
**Populated by**: UCD decomposer.
**Representative values**: `Latin`, `Greek`, `Cyrillic`, `Armenian`, `Hebrew`, `Arabic`, `Devanagari`, `Bengali`, `Tamil`, `Telugu`, `Kannada`, `Malayalam`, `Thai`, `Lao`, `Tibetan`, `Georgian`, `Hangul`, `Han`, `Hiragana`, `Katakana`, `Bopomofo`, `Ethiopic`, `Cherokee`, `Braille`, `Common`, `Inherited`, `Unknown`.

---

## Block (Unicode)

300+ Unicode block ranges.

```sql
CREATE TABLE substrate.block (
    id          SERIAL PRIMARY KEY,
    code        VARCHAR(128) NOT NULL UNIQUE,
    range_start INT NOT NULL,
    range_end   INT NOT NULL
);

CREATE INDEX idx_block_range ON substrate.block(range_start, range_end);

COMMENT ON TABLE substrate.block IS 'Unicode Block ranges. 300+ blocks. range_start/range_end store codepoint range for O(1) block lookup.';
```

**Row count**: 300+ (grows with each Unicode version).
**Populated by**: UCD decomposer.
**Representative values**: `Basic_Latin` (0x0000–0x007F), `Latin_1_Supplement` (0x0080–0x00FF), `Latin_Extended_A` (0x0100–0x017F), `CJK_Unified_Ideographs` (0x4E00–0x9FFF), `Hangul_Syllables` (0xAC00–0xD7AF).

The `range_start`/`range_end` columns enable block lookup for any codepoint without scanning the entire table — a query with `WHERE range_start <= cp AND range_end >= cp` hits the composite index.

---

## Break Property (Unicode UAX #29)

Four segmentation property categories in one table, discriminated by `category`.

```sql
CREATE TABLE substrate.break_property (
    id       SERIAL PRIMARY KEY,
    code     VARCHAR(32) NOT NULL,
    category VARCHAR(16) NOT NULL,
    UNIQUE(code, category)
);

CREATE INDEX idx_break_property_category ON substrate.break_property(category);

COMMENT ON TABLE substrate.break_property IS 'UAX #29 break properties for segmentation. Four categories: GCB, WB, SB, LB.';
```

**Row count**: ~90 (14 GCB + 19 WB + 15 SB + 42 LB).
**Populated by**: UCD decomposer.
**Categories**:
- **GCB** (Grapheme Cluster Break, 14): `CR`, `LF`, `Control`, `Extend`, `ZWJ`, `Regional_Indicator`, `Prepend`, `SpacingMark`, `L`, `V`, `T`, `LV`, `LVT`, `Other`.
- **WB** (Word Break, 19): `ALetter`, `CR`, `Double_Quote`, `Extend`, `ExtendNumLet`, `Format`, `Hebrew_Letter`, `Katakana`, `LF`, `MidLetter`, `MidNum`, `MidNumLet`, `Newline`, `Numeric`, `Regional_Indicator`, `Single_Quote`, `WSegSpace`, `ZWJ`, `Other`.
- **SB** (Sentence Break, 15): `ATerm`, `CR`, `Close`, `Extend`, `Format`, `LF`, `Lower`, `Numeric`, `OLetter`, `SContinue`, `STerm`, `Sep`, `Sp`, `Upper`, `Other`.
- **LB** (Line Break, 42): `AI`, `AL`, `B2`, `BA`, `BB`, `BK`, `CB`, `CJ`, `CL`, `CM`, `CP`, `CR`, `EX`, `GL`, `H2`, `H3`, `HL`, `HY`, `ID`, `IN`, `IS`, `JL`, `JT`, `JV`, `LF`, `NL`, `NS`, `NU`, `OP`, `PO`, `PR`, `QU`, `RI`, `SA`, `SG`, `SP`, `SY`, `WJ`, `XX`, `ZW`, `ZWJ`.

---

## Language (ISO 639-3)

7,928 languages.

```sql
CREATE TABLE substrate.language (
    id    SERIAL PRIMARY KEY,
    code  CHAR(3) NOT NULL UNIQUE,
    name  VARCHAR(128) NOT NULL,
    scope CHAR(1) NOT NULL,
    type  CHAR(1) NOT NULL
);

CREATE INDEX idx_language_scope ON substrate.language(scope);
CREATE INDEX idx_language_type ON substrate.language(type);

COMMENT ON TABLE substrate.language IS 'ISO 639-3 language inventory. 7,928 languages.';
COMMENT ON COLUMN substrate.language.scope IS 'I = individual, M = macrolanguage, S = special.';
COMMENT ON COLUMN substrate.language.type IS 'A = ancient, C = constructed, E = extinct, H = historical, L = living, S = special.';
```

**Row count**: 7,928.
**Populated by**: ISO 639 decomposer.

---

## Tensor Role

Tensor classification for model decomposition.

```sql
CREATE TABLE substrate.tensor_role (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);

COMMENT ON TABLE substrate.tensor_role IS 'Tensor classification. 27 roles from model_catalog.json.';
```

**Row count**: 27.
**Populated by**: Phase 3 model catalog / Safetensors decomposer.
**Values**: `attention_query`, `attention_key`, `attention_value`, `attention_output`, `ffn_up`, `ffn_down`, `ffn_gate`, `moe_expert_up`, `moe_expert_down`, `moe_shared_expert`, `moe_router`, `token_embedding`, `position_embedding`, `position_embedding_2d`, `layer_norm`, `rms_norm`, `conv_kernel`, `bbox_head`, `class_head`, `object_query`, `cross_attention`, `vision_feature`, `vision_projection`, `logit_head`, `modality_projection`, `patch_embedding`, `quantization_scale`.

---

## Architecture Class

Model architecture types.

```sql
CREATE TABLE substrate.architecture_class (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);

COMMENT ON TABLE substrate.architecture_class IS 'Model architecture classification.';
```

**Row count**: 12.
**Populated by**: Phase 3 model catalog / Safetensors decomposer.
**Values**: `text_llm`, `moe_llm`, `object_detection`, `vision_language`, `multimodal_llm`, `audio_understanding`, `speech`, `speech_synthesis`, `audio_generation`, `embedding`, `image_generation`, `speech_to_text`.

---

## Physicality Type

What a physicality geometry represents.

```sql
CREATE TABLE substrate.physicality_type (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);

COMMENT ON TABLE substrate.physicality_type IS 'Geometry interpretation. What the GEOMETRYZM value in physicality represents.';
```

**Row count**: 13+.
**Populated by**: Phase 1 seed script.
**Values**: `s3_position`, `waveform`, `fft_spectrum`, `stft_spectrogram`, `pitch_contour`, `formant_trajectory`, `spectral_centroid`, `svd_spectrum`, `weight_distribution`, `contour`, `hilbert_value`, `mfcc_frame`, `chromagram`.

---

## Significance Context

Arena types. Defines WHAT a significance rating measures.

```sql
CREATE TABLE substrate.significance_context (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);

COMMENT ON TABLE substrate.significance_context IS 'Arena type definitions. What a Glicko-2 significance rating is measuring.';
```

**Row count**: 10.
**Populated by**: Phase 1 seed script.
**Values**: `lexical_disambiguation`, `syntactic_role_fitness`, `translation_quality`, `model_trust`, `source_authority`, `semantic_relevance`, `corroboration_strength`, `frequency_significance`, `attention_pattern_confidence`, `morphological_productivity`.

---

## Provenance

Where data came from. Source tracking for trust priors and auditability.

```sql
CREATE TABLE substrate.provenance (
    id            SERIAL PRIMARY KEY,
    code          VARCHAR(64) NOT NULL UNIQUE,
    curator_class VARCHAR(32) NOT NULL,
    initial_mu    FLOAT8 NOT NULL
);

COMMENT ON TABLE substrate.provenance IS 'Source provenance with trust prior. initial_mu seeds Glicko-2 significance for entities/edges from this source.';
COMMENT ON COLUMN substrate.provenance.curator_class IS 'authoritative_standard, academic_curated, academic_consortium, community_curated, community_contributed, model_derived, system_computed, user_input.';
```

**Row count**: 10.
**Populated by**: Phase 1 seed script.
**Values**:

| Code | Curator Class | Initial mu |
|------|--------------|-----------|
| `unicode_consortium` | `authoritative_standard` | 2000 |
| `sil_international` | `authoritative_standard` | 2000 |
| `princeton_wordnet` | `academic_curated` | 1800 |
| `omwn_consortium` | `academic_consortium` | 1700 |
| `universaldependencies` | `academic_consortium` | 1700 |
| `wiktextract` | `community_curated` | 1400 |
| `tatoeba` | `community_contributed` | 1300 |
| `huggingface_model` | `model_derived` | 1200 |
| `godel_engine_directed` | `system_computed` | 1100 |
| `user_session` | `user_input` | 1000 |

---

## Creation Order

Reference tables must be created in FK dependency order:

1. `entity_type` (no FK dependencies)
2. `edge_role` (no FK dependencies)
3. `physicality_type` (no FK dependencies)
4. `significance_context` (no FK dependencies)
5. `provenance` (no FK dependencies)
6. `architecture_class` (no FK dependencies)
7. `tensor_role` (no FK dependencies)
8. `script` (no FK dependencies)
9. `block` (no FK dependencies)
10. `break_property` (no FK dependencies)
11. `language` (no FK dependencies)
12. `general_category` (no FK dependencies)
13. `semantic_relation_type` (no FK dependencies)
14. `pos` (self-referencing parent_id only)
15. `deprel` (self-referencing parent_id only)
16. `morph_feature` (self-referencing parent_id only)
17. `lexname` (no FK dependencies)
18. `sense` (FK → `lexname`, `pos`)
19. `edge_type` (FK → `entity_type` × 2)

All reference tables are created before the core data tables (`entity`, `edge`, `physicality`, `sequence`, `significance`, `edge_member`) and before all junction tables.
