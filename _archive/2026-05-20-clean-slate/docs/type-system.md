# Hartonomous Classification Vocabulary

> **Authority note (2026-05-09):** Phantom entity types previously listed in this document (`attention_pattern` and similar per-role-unit-as-entity types) are deprecated by the 2026-05-08 architectural correction. The corrected vision is documented in [`docs/00-substrate-spec.md`](00-substrate-spec.md) §III: per-role units of Track 2 transformation tensors manifest as typed attestation EDGES between existing content entities, NOT as phantom entity types. Inline DEPRECATED markers below identify the affected rows.

The classification vocabulary is the substrate's grammar. It defines every category, type, and property value that the system uses to classify entities and type edges. These are **reference tables** — properly normalized relational tables populated during seed ingestion. They are NOT entities in the entity table. They are the infrastructure that enables the substrate to process, not substrate content themselves.

## Principle

No lazy groupings. "Noun" is not a type. "Noun" is a category that contains dozens of specific values. The substrate must distinguish between a countable animate noun in nominative singular third-person and an uncountable abstract noun in genitive plural — because these distinctions drive inference, generation, and transformation.

## Architecture

Classification vocabulary lives in **reference tables** — one table per classification domain (`pos`, `deprel`, `morph_feature`, `sense`, `general_category`, `script`, `block`, etc.). Each is a small, properly indexed table with a `code` column as natural key and an `id` surrogate key for FK efficiency.

Hierarchy within a classification domain is expressed via `parent_id` self-references within the reference table:

```sql
-- pos reference table
INSERT INTO pos (code, parent_id) VALUES ('NOUN', NULL);          -- top-level
INSERT INTO pos (code, parent_id) VALUES ('countable_noun', 1);    -- subtype of NOUN
INSERT INTO pos (code, parent_id) VALUES ('proper_noun', 1);       -- subtype of NOUN

-- deprel reference table
INSERT INTO deprel (code, parent_id) VALUES ('nsubj', NULL);       -- top-level
INSERT INTO deprel (code, parent_id) VALUES ('nsubj:pass', 1);     -- subtype of nsubj
```

New classification values are added by inserting rows into the appropriate reference table. No code changes. No schema changes. The classification vocabulary is data, not structure.

**Evidence junction tables** (`entity_pos`, `entity_sense`, `entity_language`, `codepoint_property`) map entities to their classification values as one-to-many lookups with significance. "Is 'rake' a noun?" = `SELECT 1 FROM entity_pos WHERE entity_id = ? AND pos_id = ?`. Direct indexed JOIN. No graph traversal. Application functionality, not AI functionality.

## Linguistic Classification Tables

### Part of Speech — `pos` Reference Table (from UD UPOS + WordNet + Wiktionary)

Top-level POS categories (17 UPOS):
- `ADJ`, `ADP`, `ADV`, `AUX`, `CCONJ`, `DET`, `INTJ`, `NOUN`, `NUM`, `PART`, `PRON`, `PROPN`, `PUNCT`, `SCONJ`, `SYM`, `VERB`, `X`

Each expands into subtypes via `parent_id` self-reference. These are NOT flat tags — they are hierarchical classification values with morphological feature compositions.

### Morphological Feature Values — `morph_feature` Reference Table (from UD, 68+ keys)

Each feature is a (key, value) pair stored as a row. Feature assignments to entities are via junction tables.

**Nominal features**:
- `Animacy`: Anim, Hum, Inan, Nhum
- `Case`: Abs, Acc, Erg, Nom, Gen, Dat, Ins, Loc, Voc, Par, Tra, Com, Abe, Ine, Ill, Ela, Add, Ade, All, Abl, Sup, Sub, Del, Lat, Tem, Ter, Cau, Ben, Dis, Equ + language-specific cases
- `Definite`: Com, Cons, Def, Ind, Spec
- `Degree`: Abs, Cmp, Equ, Pos, Sup
- `Gender`: Com, Fem, Masc, Neut + layered: `Gender[abs]`, `Gender[erg]`, `Gender[subj]`, `Gender[obj]`, `Gender[io]`, `Gender[psor]`, `Gender[refl]`
- `NounBase`: various
- `Number`: Coll, Count, Dual, Grpa, Grpl, Inv, Pauc, Plur, Ptan, Sing, Tri + layered: `Number[abs]`, `Number[erg]`, `Number[subj]`, `Number[obj]`, `Number[io]`, `Number[psor]`, `Number[refl]`
- `NumType`: Card, Dist, Frac, Mult, Ord, Range, Sets

**Pronominal features**:
- `Person`: 0, 1, 2, 3, 4 + layered: `Person[abs]`, `Person[erg]`, `Person[subj]`, `Person[obj]`, `Person[io]`, `Person[psor]`, `Person[refl]`
- `Poss`: Yes
- `PronType`: Art, Dem, Emp, Exc, Ind, Int, Neg, Prs, Rcp, Rel, Tot
- `Reflex`: Yes

**Verbal features**:
- `Aspect`: Hab, Imp, Iter, Perf, Prog, Prosp
- `Caus`: Yes
- `Evident`: Fh, Nfh
- `Mood`: Adm, Cnd, Des, Imp, Ind, Int, Irr, Jus, Nec, Opt, Pot, Prp, Qot, Sub
- `Polarity`: Neg, Pos
- `Tense`: Fut, Imp, Past, Pqp, Pres
- `VerbForm`: Conv, Fin, Gdv, Ger, Inf, Part, Sup, Vnoun
- `VerbType`: various
- `Voice`: Act, Antip, Bfoc, Cau, Dir, Inv, Lfoc, Mid, Pass, Rcp

**Other features**:
- `AdjType`: various
- `AdpType`: Circ, Comps, Post, Prep, Voc
- `Subordinative`: Yes
- `Ventive`: Yes
- `ExtPos`: various

### Dependency Relation Values — `deprel` Reference Table (from UD, 70+ values)

Core universal relations (37):
- `acl` (clausal modifier of noun), `advcl` (adverbial clause modifier), `advmod` (adverbial modifier), `amod` (adjectival modifier), `appos` (appositional modifier), `aux` (auxiliary), `case` (case marking), `cc` (coordinating conjunction), `ccomp` (clausal complement), `clf` (classifier), `compound` (compound), `conj` (conjunct), `cop` (copula), `csubj` (clausal subject), `dep` (unspecified dependency), `det` (determiner), `discourse` (discourse element), `dislocated` (dislocated elements), `expl` (expletive), `fixed` (fixed multiword expression), `flat` (flat multiword expression), `goeswith` (goes with), `iobj` (indirect object), `list` (list), `mark` (marker), `nmod` (nominal modifier), `nsubj` (nominal subject), `nummod` (numeric modifier), `obj` (object), `obl` (oblique nominal), `orphan` (orphan), `parataxis` (parataxis), `punct` (punctuation), `reparandum` (overridden disfluency), `root` (root), `vocative` (vocative), `xcomp` (open clausal complement)

Language-specific subtypes (33+ confirmed, open-ended):
- `acl:relcl`, `advcl:compar`, `advcl:cond`, `advcl:conv`, `advcl:purp`, `advcl:quote`, `advcl:seq`, `advmod:emph`, `advmod:q`, `aux:pass`, `ccomp:iobj`, `ccomp:lo`, `ccomp:obj`, `ccomp:poss`, `ccomp:purp`, `ccomp:quote`, `ccomp:ro`, `compound:pred`, `compound:prt`, `conj:q`, `csubj:outer`, `csubj:quote`, `dep:repeat`, `det:poss`, `flat:name`, `iobj:cs`, `iobj:lo`, `iobj:po`, `iobj:poss`, `iobj:ro`, `nmod:poss`, `nmod:quote`, `nsubj:outer`, `nsubj:pass`, `xcomp:lo`, `xcomp:subj`

Each subtype references its parent via `parent_id` (e.g., `nsubj:pass` → `nsubj`).

### Semantic Relation Values — `semantic_relation_type` Reference Table (from WordNet, 25+ pointer types)

- `antonym` (word-level), `hypernym` (synset-level), `instance_hypernym`, `hyponym`, `instance_hyponym`, `member_holonym`, `substance_holonym`, `part_holonym`, `member_meronym`, `substance_meronym`, `part_meronym`, `attribute`, `derivationally_related`, `domain_of_synset_topic`, `member_of_domain_topic`, `domain_of_synset_region`, `member_of_domain_region`, `domain_of_synset_usage`, `member_of_domain_usage`, `entailment`, `cause`, `also_see`, `verb_group`, `similar_to`, `participle_of_verb`, `pertainym`

Each has explicit domain/range constraints on the `edge_type` reference table (which entity types can be source and target).

### Lexical Categories — `lexname` Reference Table (from WordNet, 45 categories)

`adj.all`, `adj.pert`, `adj.ppl`, `adv.all`, `noun.Tops`, `noun.act`, `noun.animal`, `noun.artifact`, `noun.attribute`, `noun.body`, `noun.cognition`, `noun.communication`, `noun.event`, `noun.feeling`, `noun.food`, `noun.group`, `noun.location`, `noun.motive`, `noun.object`, `noun.person`, `noun.phenomenon`, `noun.plant`, `noun.possession`, `noun.process`, `noun.quantity`, `noun.relation`, `noun.shape`, `noun.state`, `noun.substance`, `noun.time`, `verb.body`, `verb.change`, `verb.cognition`, `verb.communication`, `verb.competition`, `verb.consumption`, `verb.contact`, `verb.creation`, `verb.emotion`, `verb.motion`, `verb.perception`, `verb.possession`, `verb.social`, `verb.stative`, `verb.weather`

### Morphological Role Values

- `prefix`, `suffix`, `infix`, `circumfix`, `root`, `stem`, `inflectional_affix`, `derivational_affix`
- Each morpheme entity can have sense edges (e.g., prefix "un-" → sense "negation", prefix "re-" → sense "again")

### Verb Subcategorization Frames (from WordNet, 35 frames)

Each frame encodes argument structure: what kinds of subjects, objects, complements a verb takes. Stored as reference values; assigned to verb entities via edges.

## Unicode Character Classification Tables (from UCD)

### General Category — `general_category` Reference Table (30 values in 7 groups)

Letter: `Lu` (uppercase), `Ll` (lowercase), `Lt` (titlecase), `Lm` (modifier), `Lo` (other)
Mark: `Mn` (nonspacing), `Mc` (spacing combining), `Me` (enclosing)
Number: `Nd` (decimal digit), `Nl` (letter), `No` (other)
Punctuation: `Pc` (connector), `Pd` (dash), `Ps` (open), `Pe` (close), `Pi` (initial quote), `Pf` (final quote), `Po` (other)
Symbol: `Sm` (math), `Sc` (currency), `Sk` (modifier), `So` (other)
Separator: `Zs` (space), `Zl` (line), `Zp` (paragraph)
Other: `Cc` (control), `Cf` (format), `Cs` (surrogate), `Co` (private use), `Cn` (not assigned)

### Script — `script` Reference Table (160+ scripts)

`Latin`, `Greek`, `Cyrillic`, `Armenian`, `Hebrew`, `Arabic`, `Syriac`, `Thaana`, `Devanagari`, `Bengali`, `Gurmukhi`, `Gujarati`, `Oriya`, `Tamil`, `Telugu`, `Kannada`, `Malayalam`, `Sinhala`, `Thai`, `Lao`, `Tibetan`, `Myanmar`, `Georgian`, `Hangul`, `Ethiopic`, `Cherokee`, `Canadian_Aboriginal`, `Ogham`, `Runic`, `Khmer`, `Mongolian`, `Hiragana`, `Katakana`, `Bopomofo`, `Han`, `Yi`, `Old_Italic`, `Gothic`, `Deseret`, `Tagalog`, `Hanunoo`, `Buhid`, `Tagbanwa`, `Limbu`, `Tai_Le`, `Linear_B`, `Ugaritic`, `Shavian`, `Osmanya`, `Cypriot`, `Braille`, `Buginese`, `Coptic`, `New_Tai_Lue`, `Glagolitic`, `Tifinagh`, `Syloti_Nagri`, `Old_Persian`, `Kharoshthi`, `Balinese`, `Cuneiform`, `Phoenician`, `Phags_Pa`, ... (and many more added per Unicode version)

### Block — `block` Reference Table (300+ blocks)

`Basic_Latin`, `Latin_1_Supplement`, `Latin_Extended_A`, `Latin_Extended_B`, `IPA_Extensions`, `Spacing_Modifier_Letters`, `Combining_Diacritical_Marks`, `Greek_and_Coptic`, `Cyrillic`, `Cyrillic_Supplement`, `Armenian`, `Hebrew`, `Arabic`, `Syriac`, `Thaana`, `NKo`, `Samaritan`, `Mandaic`, `Devanagari`, `Bengali`, ... `CJK_Unified_Ideographs`, `CJK_Unified_Ideographs_Extension_A` through `Extension_I`, `Hangul_Syllables`, `Emoji`, etc.

### Break Property — `break_property` Reference Table

Grapheme cluster break: `CR`, `LF`, `Control`, `Extend`, `ZWJ`, `Regional_Indicator`, `Prepend`, `SpacingMark`, `L`, `V`, `T`, `LV`, `LVT`, `Other`

Word break: `ALetter`, `CR`, `Double_Quote`, `Extend`, `ExtendNumLet`, `Format`, `Hebrew_Letter`, `Katakana`, `LF`, `MidLetter`, `MidNum`, `MidNumLet`, `Newline`, `Numeric`, `Regional_Indicator`, `Single_Quote`, `WSegSpace`, `ZWJ`, `Other`

Sentence break: `ATerm`, `CR`, `Close`, `Extend`, `Format`, `LF`, `Lower`, `Numeric`, `OLetter`, `SContinue`, `STerm`, `Sep`, `Sp`, `Upper`, `Other`

Line break: `AI`, `AL`, `B2`, `BA`, `BB`, `BK`, `CB`, `CJ`, `CL`, `CM`, `CP`, `CR`, `EX`, `GL`, `H2`, `H3`, `HL`, `HY`, `ID`, `IN`, `IS`, `JL`, `JT`, `JV`, `LF`, `NL`, `NS`, `NU`, `OP`, `PO`, `PR`, `QU`, `RI`, `SA`, `SG`, `SP`, `SY`, `WJ`, `XX`, `ZW`, `ZWJ`

## Model/Tensor Classification Tables

### Architecture — `architecture_class` Reference Table
`text_llm`, `moe_llm`, `object_detection`, `vision_language`, `multimodal_llm`, `audio_understanding`, `speech`, `speech_synthesis`, `audio_generation`, `embedding`, `image_generation`, `speech_to_text`

### Tensor Role — `tensor_role` Reference Table (20+ confirmed from model_catalog.json)
`attention_query`, `attention_key`, `attention_value`, `attention_output`, `ffn_up`, `ffn_down`, `ffn_gate`, `moe_expert_up`, `moe_expert_down`, `moe_shared_expert`, `moe_router`, `token_embedding`, `position_embedding`, `position_embedding_2d`, `layer_norm`, `rms_norm`, `conv_kernel`, `bbox_head`, `class_head`, `object_query`, `cross_attention`, `vision_feature`, `vision_projection`, `logit_head`, `modality_projection`, `patch_embedding`, `quantization_scale`

### Data Type Values
`bf16`, `f32`, `f16`, `f64`, `i64`, `i32`, `i16`, `i8`, `u8`, `bool`, `f8_e4m3`, `f8_e5m2`

### Model Component Values
`encoder`, `decoder`, `projector`, `backbone`, `codec`, `quantizer`, `router`, `adaptor`, `lora_adapter`

## Modality — `modality` Reference Table

`text`, `image`, `audio`, `video`, `model_weights`, `tensor_metadata`, `configuration`, `vocabulary`

Used by `entity_type.modality` and edge scope filtering.

## Provenance — `provenance` Reference Table

### Curator Class Values
`authoritative_standard` (Unicode, ISO), `academic_curated` (Princeton WordNet), `academic_consortium` (OMW, UD), `community_curated` (Wiktionary), `community_contributed` (Tatoeba), `model_derived` (extracted from AI model weights), `system_computed` (analysis passes), `user_input` (prompts and feedback)

### Source Values
`unicode_consortium`, `sil_international`, `princeton_wordnet`, `omwn_consortium`, `universaldependencies`, `wiktextract`, `tatoeba`, `huggingface_model`, `user_session`

## Significance Context — `significance_context` Reference Table (Arena Types)

These define WHAT a rating is for. The same entity or edge can have different ratings in different contexts.

- `lexical_disambiguation` -- which sense of an ambiguous word is correct in context
- `syntactic_role_fitness` -- how well an entity fills a syntactic role
- `translation_quality` -- how good a cross-lingual alignment is
- `model_trust` -- how reliable a model's extracted knowledge is
- `source_authority` -- how authoritative the provenance source is
- `semantic_relevance` -- how relevant an entity is to a query context
- `corroboration_strength` -- how strongly independent sources agree
- `frequency_significance` -- how significant frequency/position data is
- `attention_pattern_confidence` -- confidence in attention head type classification
- `morphological_productivity` -- how productive a morphological pattern is

## Entity Type Registry — `entity_type` Reference Table

Every entity has exactly one type. Types are grouped by modality and seeding phase.

**Seed entity types** (populated during seed ingestion):

| Entity Type | Modality | Phase | Source |
|-------------|----------|-------|--------|
| `codepoint` | text | 2a | UCD |
| `collation_element` | text | 2a | UCA |
| `language_name` | text | 2b | ISO 639 |
| `synset` | text | 2c | WordNet |
| `lemma` | text | 2c/2e | WordNet, OMW, Wiktionary |
| `word_sense` | text | 2c | WordNet |
| `inflected_form` | text | 2c/2e | WordNet, Wiktionary |
| `ud_sentence` | text | 2d | UD |
| `ud_token` | text | 2d | UD |
| `wikt_sense` | text | 2e | Wiktionary |
| `tatoeba_sentence` | text | 2f | Tatoeba |
| `audio_recording` | audio | 2f | Tatoeba |
| `tensor` | model_weights | 3 | SafeTensors |
| `model_architecture` | model_weights | 3 | SafeTensors |
| ~~`attention_pattern`~~ | ~~model_weights~~ | ~~3~~ | **DEPRECATED 2026-05-08** — phantom entity type per spec §XII. Per-role units (attention patterns, FFN rows, etc.) of Track 2 transformation tensors are typed attestation EDGES between existing `word_form` content entities (`model_attention_pattern` per `sql/schema/seed/edge_type.sql:84-90`) with `attestation_type = model_attention_qk_pattern` (or `model_attention_vo_pattern`) on the rating event. See [`docs/00-substrate-spec.md`](00-substrate-spec.md) §III, AP-25. |

**Runtime entity types** (created by runtime decomposers):

| Entity Type | Modality | Created By |
|-------------|----------|-----------|
| `grapheme_cluster` | text | TextDecomposer |
| `word_form` | text | TextDecomposer |
| `morpheme` | text | TextDecomposer |
| `text_composition` | text | TextDecomposer (document, chapter, paragraph, sentence) |
| `pixel_region` | image | ImageDecomposer |
| `patch` | image | ImageDecomposer |
| `contour` | image | ImageDecomposer |
| `image_composition` | image | ImageDecomposer |
| `audio_chunk` | audio | AudioDecomposer |
| `spectral_entity` | audio | AudioDecomposer |
| `temporal_event` | audio | AudioDecomposer |
| `scene` | video | VideoDecomposer |
| `shot` | video | VideoDecomposer |

## Edge Type Registry — `edge_type` Reference Table

Every edge has exactly one type. Types have a `category` column for broad grouping and `code` for the specific type.

| Category | Edge Types | Source |
|----------|-----------|--------|
| `semantic` | `hypernym`, `hyponym`, `antonym`, `meronym_part`, `meronym_substance`, `meronym_member`, `holonym_part`, `holonym_substance`, `holonym_member`, `similar_to`, `entailment`, `cause`, `also_see`, `domain_topic`, `domain_region`, `domain_usage`, `derivationally_related`, `pertainym`, `participle_of_verb`, `verb_group`, `attribute` | WordNet |
| `lexical` | `has_form`, `has_sense`, `alternate_name_of`, `aligned_to_synset`, `translation_of` | WordNet, OMW, Wiktionary |
| `syntactic` | All 70+ `deprel` values (`nsubj`, `obj`, `amod`, `advmod`, etc.) | UD |
| `morphological` | `has_morpheme`, `morpheme_role` | Wiktionary, analysis passes |
| `compositional` | `child_of`, `frame_of`, `in_model`, `recording_of` | All decomposers |
| `case_mapping` | `maps_to_lowercase`, `maps_to_uppercase`, `maps_to_titlecase` | UCD |
| `cross_lingual` | `macrolanguage_of` | ISO 639 |
| `spatial` | `adjacent_to`, `contains_region`, `contour_of` | Image/Audio analysis |
| `temporal` | `at_time`, `at_position`, `follows`, `scene_boundary` | Video analysis |
| `cross_modal` | `caption_of`, `transcript_of`, `alignment` | Runtime cross-modal |

## Edge Domain/Range Constraints

Every edge type has explicit domain and range entity types. The `EdgeTypeValidator` enforces these at ingestion time. Examples:

| Edge Type | Source Entity Type | Target Entity Type |
|-----------|-------------------|--------------------|
| `hypernym` | synset | synset |
| `antonym` | word_sense | word_sense |
| `nsubj` | ud_token (dependent) | ud_token (head) |
| `amod` | ud_token (dependent) | ud_token (head) |
| `aligned_to_synset` | lemma | synset |
| `translation_of` | wikt_sense | lemma |
| `in_model` | tensor | model_architecture |
| `recording_of` | audio_recording | tatoeba_sentence |

Domain/range validation fails loud. You cannot create a `hypernym` edge from a codepoint to an audio recording — the edge type system prevents it.

**Note on semantic_relation_type vs edge_type**: WordNet pointer types (hypernym, hyponym, etc.) populate BOTH the `semantic_relation_type` reference table (documenting the vocabulary) and the `edge_type` reference table (operationally typing edges). Similarly, UD deprel values (nsubj, amod, etc.) populate both the `deprel` reference table (classification vocabulary) and `edge_type` (with `category='syntactic'`). The vocabulary tables document the classification system; `edge_type` operationally types edges in the substrate.

**Note on junction table operations vs edges**: Some property assignments use junction tables for fast application-layer lookups rather than edges. These are NOT edge types:

| Junction Table | Maps | Lookup Pattern |
|----------------|------|---------------|
| `entity_pos` | entity → pos reference table | "Is 'rake' a noun?" = one JOIN |
| `entity_sense` | entity → sense reference table | Sense candidates for a word |
| `entity_language` | entity → language reference table | Language tags |
| `entity_morph_feature` | entity → morph_feature reference table | Morphological features |
| `codepoint_property` | codepoint → general_category, script, block, break values | Unicode properties |
| `model_architecture_class` | model → architecture_class reference table | Model classification |
| `tensor_tensor_role` | tensor → tensor_role reference table | Tensor classification |
| `pattern_deprel` | entity (typically a word_form, formerly attention_pattern phantom) → deprel reference table | What attention pattern encodes (Glicko-2 mu confidence on the binding). With the phantom `attention_pattern` entity type deprecated per spec §XII, `pattern_deprel` rows now bind to `word_form` content entities the model attests on, with the dependency-relation hypothesis as the junction value. |

The junction tables and edges can coexist — the junction table is the fast indexed path, the edge is the significance-weighted traversal path.
