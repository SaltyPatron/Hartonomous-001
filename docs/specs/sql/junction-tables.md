# Junction Table DDL

**Status**: ✅ Complete

Junction tables map entities to classification reference table rows. They are the fast application-layer lookup path — "Is 'rake' a noun?" = one JOIN. Some carry significance (Glicko-2 priors from seed data, updated during inference).

These are NOT edges. They provide fast indexed lookups for classification. The edge table provides significance-weighted traversal. Both use the same underlying data; junction tables are the fast path, edges are the deep path.

All tables live in the `substrate` schema.

---

## entity_pos

Entity → POS classification(s) with significance. A word can have multiple POS assignments (noun AND verb) with frequency-weighted significance.

```sql
CREATE TABLE substrate.entity_pos (
    entity_id  BIGINT NOT NULL REFERENCES substrate.entity(id),
    pos_id     INT NOT NULL REFERENCES substrate.pos(id),
    mu         FLOAT8 NOT NULL DEFAULT 1500,
    sigma      FLOAT8 NOT NULL DEFAULT 350,
    volatility FLOAT8 NOT NULL DEFAULT 0.06,
    games      INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_id, pos_id)
);

CREATE INDEX idx_entity_pos_pos ON substrate.entity_pos(pos_id, entity_id);

COMMENT ON TABLE substrate.entity_pos IS 'Entity → POS assignment with Glicko-2 significance. Multiple POS per entity supported.';
COMMENT ON COLUMN substrate.entity_pos.mu IS 'Frequency-weighted POS distribution. Higher mu = more common POS for this entity.';
```

**Populated by**: UD decomposer (UPOS assignments from treebanks), Wiktionary decomposer (POS from dictionary entries).
**Significance updated by**: Inference — as the entity is used in context, POS significance adjusts.
**Example**: "rake" → `[(NOUN, mu=1600), (VERB, mu=1400)]` — more commonly a noun.

---

## entity_sense

Entity → sense(s) with significance. Captures word sense disambiguation priors.

```sql
CREATE TABLE substrate.entity_sense (
    entity_id BIGINT NOT NULL REFERENCES substrate.entity(id),
    sense_id  INT NOT NULL REFERENCES substrate.sense(id),
    mu        FLOAT8 NOT NULL DEFAULT 1500,
    sigma     FLOAT8 NOT NULL DEFAULT 350,
    volatility FLOAT8 NOT NULL DEFAULT 0.06,
    games     INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_id, sense_id)
);

CREATE INDEX idx_entity_sense_sense ON substrate.entity_sense(sense_id, entity_id);

COMMENT ON TABLE substrate.entity_sense IS 'Entity → sense assignment with Glicko-2 significance. WSD priors from seed data.';
COMMENT ON COLUMN substrate.entity_sense.mu IS 'Sense prevalence. Higher mu = more common sense for this entity.';
```

**Populated by**: WordNet decomposer (lemma-to-synset mappings with sense ordering), Wiktionary decomposer (sense entries).
**Significance updated by**: Inference — `lexical_disambiguation` arena updates sense significance based on context.
**Example**: "bank" → `[(financial_institution, mu=1500), (river_edge, mu=1200), (pool_table_cushion, mu=800)]`.

---

## entity_language

Entity → language(s). No significance — language assignment is categorical, not probabilistic.

```sql
CREATE TABLE substrate.entity_language (
    entity_id   BIGINT NOT NULL REFERENCES substrate.entity(id),
    language_id INT NOT NULL REFERENCES substrate.language(id),
    PRIMARY KEY (entity_id, language_id)
);

CREATE INDEX idx_entity_language_lang ON substrate.entity_language(language_id, entity_id);

COMMENT ON TABLE substrate.entity_language IS 'Entity → language assignment. Multiple languages per entity (e.g., "chat" in eng and fra).';
```

**Populated by**: UD decomposer (treebank language), Wiktionary decomposer (per-entry language), Tatoeba decomposer (sentence language), OMW decomposer (cross-lingual alignment language).
**No significance**: language tags are factual, not probabilistic.
**Example**: "chat" → `[eng, fra]` — exists in both English and French.

---

## entity_morph_feature

Entity → morphological feature(s). No significance — morphological features are categorical.

```sql
CREATE TABLE substrate.entity_morph_feature (
    entity_id      BIGINT NOT NULL REFERENCES substrate.entity(id),
    morph_feature_id INT NOT NULL REFERENCES substrate.morph_feature(id),
    PRIMARY KEY (entity_id, morph_feature_id)
);

CREATE INDEX idx_entity_morph_feature_feat ON substrate.entity_morph_feature(morph_feature_id, entity_id);

COMMENT ON TABLE substrate.entity_morph_feature IS 'Entity → morphological feature assignment. Multiple features per entity.';
```

**Populated by**: UD decomposer (from CoNLL-U FEATS column).
**No significance**: morphological features are structural facts from treebanks.
**Example**: "dictionaries" → `[Number=Plur]`. "went" → `[Tense=Past, VerbForm=Fin, Mood=Ind]`.

---

## codepoint_property

Codepoint → Unicode properties. Wide table — one row per codepoint with all property IDs.

```sql
CREATE TABLE substrate.codepoint_property (
    entity_id           BIGINT NOT NULL REFERENCES substrate.entity(id),
    general_category_id INT NOT NULL REFERENCES substrate.general_category(id),
    script_id           INT NOT NULL REFERENCES substrate.script(id),
    block_id            INT NOT NULL REFERENCES substrate.block(id),
    gcb_id              INT REFERENCES substrate.break_property(id),
    wb_id               INT REFERENCES substrate.break_property(id),
    sb_id               INT REFERENCES substrate.break_property(id),
    lb_id               INT REFERENCES substrate.break_property(id),
    PRIMARY KEY (entity_id)
);

CREATE INDEX idx_codepoint_property_gc ON substrate.codepoint_property(general_category_id);
CREATE INDEX idx_codepoint_property_script ON substrate.codepoint_property(script_id);
CREATE INDEX idx_codepoint_property_block ON substrate.codepoint_property(block_id);

COMMENT ON TABLE substrate.codepoint_property IS 'Codepoint → Unicode properties. One row per codepoint entity. Wide table for all property FKs.';
COMMENT ON COLUMN substrate.codepoint_property.gcb_id IS 'Grapheme Cluster Break property. FK to break_property where category=GCB.';
COMMENT ON COLUMN substrate.codepoint_property.wb_id IS 'Word Break property. FK to break_property where category=WB.';
COMMENT ON COLUMN substrate.codepoint_property.sb_id IS 'Sentence Break property. FK to break_property where category=SB.';
COMMENT ON COLUMN substrate.codepoint_property.lb_id IS 'Line Break property. FK to break_property where category=LB.';
```

**Populated by**: UCD decomposer.
**No significance**: Unicode properties are authoritative facts.
**Row count**: ~149,813 (Unicode 15.1 assigned codepoints).
**Example**: U+0041 (A) → `general_category=Lu, script=Latin, block=Basic_Latin, gcb=Other, wb=ALetter, sb=Upper, lb=AL`.

---

## model_architecture_class

Model entity → architecture classification(s).

```sql
CREATE TABLE substrate.model_architecture_class (
    entity_id            BIGINT NOT NULL REFERENCES substrate.entity(id),
    architecture_class_id INT NOT NULL REFERENCES substrate.architecture_class(id),
    PRIMARY KEY (entity_id, architecture_class_id)
);

CREATE INDEX idx_model_arch_class ON substrate.model_architecture_class(architecture_class_id, entity_id);

COMMENT ON TABLE substrate.model_architecture_class IS 'Model entity → architecture classification. Multiple classes per model supported.';
```

**Populated by**: Safetensors decomposer (from model catalog matching).
**No significance**: architecture classification is factual.
**Example**: `qwen2.5-coder-7b` → `[text_llm]`. A multimodal model → `[multimodal_llm, vision_language]`.

---

## tensor_tensor_role

Tensor entity → tensor role classification(s).

```sql
CREATE TABLE substrate.tensor_tensor_role (
    entity_id     BIGINT NOT NULL REFERENCES substrate.entity(id),
    tensor_role_id INT NOT NULL REFERENCES substrate.tensor_role(id),
    PRIMARY KEY (entity_id, tensor_role_id)
);

CREATE INDEX idx_tensor_role ON substrate.tensor_tensor_role(tensor_role_id, entity_id);

COMMENT ON TABLE substrate.tensor_tensor_role IS 'Tensor entity → tensor role classification.';
```

**Populated by**: Safetensors decomposer (from tensor name pattern matching against model catalog).
**No significance**: tensor role is structural classification.
**Example**: `model.layers.0.self_attn.q_proj.weight` → `[attention_query]`.

---

## pattern_deprel

Attention pattern entity → dependency relation(s) with significance. Which syntactic relation an attention pattern encodes.

```sql
CREATE TABLE substrate.pattern_deprel (
    entity_id  BIGINT NOT NULL REFERENCES substrate.entity(id),
    deprel_id  INT NOT NULL REFERENCES substrate.deprel(id),
    mu         FLOAT8 NOT NULL DEFAULT 1200,
    sigma      FLOAT8 NOT NULL DEFAULT 350,
    volatility FLOAT8 NOT NULL DEFAULT 0.06,
    games      INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_id, deprel_id)
);

CREATE INDEX idx_pattern_deprel_deprel ON substrate.pattern_deprel(deprel_id, entity_id);

COMMENT ON TABLE substrate.pattern_deprel IS 'Attention pattern entity → deprel classification with Glicko-2 significance.';
COMMENT ON COLUMN substrate.pattern_deprel.mu IS 'Confidence that this attention head encodes this deprel. Default 1200 (model_derived trust prior).';
```

**Populated by**: Layer-type decomposers in the Safetensors container decomposer (per [`docs/specs/decomposers/layer-type-library.md`](../decomposers/layer-type-library.md)) — `AttentionQkvLayerDecomposer` and `AttentionVoLayerDecomposer` analyze attention patterns and bind them to UD deprel hypotheses.
**Significance updated by**: `attention_pattern_confidence` arena — comparison of `model_attention_pattern` edge trajectory geometries against known syntactic-pattern archetypes via Fréchet distance.
**Example (corrected per [`docs/00-substrate-spec.md`](../../00-substrate-spec.md) §III):** `pattern_deprel(entity_hash=word_form('King').hash, deprel_id=nsubj, mu=1400)` — the model's attention attests that the `word_form` "King" is bound to `nsubj` with confidence 1400 in the source contexts the model trained on. The previous `attention_head_3_layer_7` example (a phantom per-head entity) is deprecated; the same attestation now lives on the `model_attention_pattern` edge between word_form pairs with `attestation_type = model_attention_qk_pattern` and layer/head metadata on the rating event.

---

## Creation Order

Junction tables depend on both core data tables and reference tables:

1. All reference tables (see [reference-tables.md](reference-tables.md))
2. Core data tables: `entity`, `edge`, `physicality`, `sequence`, `significance`, `edge_member`
3. Junction tables: `entity_pos`, `entity_sense`, `entity_language`, `entity_morph_feature`, `codepoint_property`, `model_architecture_class`, `tensor_tensor_role`, `pattern_deprel`

All junction tables have composite primary keys (never surrogate keys). Forward indexes (`entity_id, ref_id`) serve "what classifications does this entity have?" queries. Reverse indexes (`ref_id, entity_id`) serve "which entities have this classification?" queries.
