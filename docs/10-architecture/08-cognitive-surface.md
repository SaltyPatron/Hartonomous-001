# Cognitive Surface — All AI Operations as SQL

**Status:** Canonical
**Last verified:** 2026-04-29
**Audience:** Engineers exposing substrate functionality, customers writing queries, integrators building applications.

---

## The principle

Every AI operation the substrate supports is a SQL function. Inference, refinement, distillation, generation, translation, comparison, idiomaticity, frayed-edge research — all expressible as `SELECT hartonomous.X(...)` calls against a Postgres database. The cognitive surface is the API.

There is no separate inference server. There is no "engine" running outside Postgres. The native compute extension provides primitives (BLAKE3, 4D ops, A\*, Glicko, GiST opclasses, Laplacian eigenmap); SQL functions on top compose them; customers call the SQL functions.

This means: deploying the substrate IS deploying the AI. There's no extra service to operate. There's no separate scaling concern (Postgres scales like Postgres). There's no per-request inference cost beyond query execution.

## Function organization

The cognitive surface is divided into categories:

```
hartonomous.{category}.{operation}
```

Categories:

- `inference.*` — A\* over edges, recipes, conversational state
- `transform.*` — translation, summarization, style transfer, paraphrase
- `generate.*` — text generation, image generation, audio synthesis
- `compare.*` — cross-model, cross-source, cross-modal analysis
- `analyze.*` — idiomaticity, frayed edges, antipodal violations, sparsity flags
- `recompose.*` — produce output bytes (safetensors, image, audio, etc.)
- `provenance.*` — source attribution, audit, trustrank
- `lexical.*` — senses, hypernyms, synonyms, etymology
- `cross_lingual.*` — translation pairs, alignment, parallel corpora
- `geometric.*` — Fréchet, Hausdorff, dispersion, centroids
- `substrate.*` — internal substrate operations (mostly for substrate operators, not customers)

## Function signatures

A representative selection of cognitive functions (full reference in `20-technical/08-cognitive-functions.md`):

### Inference

```sql
hartonomous.inference.converse(
    prompt           TEXT,
    arena_recipe     JSONB DEFAULT NULL,         -- recipe DSL or NULL for default
    target_lang      TEXT DEFAULT NULL,
    max_cost         FLOAT8 DEFAULT 1000.0,
    max_depth        INT DEFAULT 10,
    explanation      BOOLEAN DEFAULT TRUE
) RETURNS TABLE (
    response_text       TEXT,
    response_entity_id  BYTEA,
    paths               JSONB,
    explanation_trace   JSONB,
    arenas_consulted    TEXT[],
    elapsed_ms          FLOAT8
);

hartonomous.inference.outcome(
    response_entity_id  BYTEA,
    outcome             TEXT,                     -- 'accept', 'reject', 'partial', 'unknown'
    arenas              TEXT[] DEFAULT NULL       -- arenas to apply update to (NULL = all consulted)
) RETURNS BIGINT;                                  -- count of edges updated

hartonomous.inference.replay(
    explanation_trace   JSONB,                    -- prior trace
    substrate_snapshot  TEXT DEFAULT 'current'    -- substrate state to replay against
) RETURNS TABLE (paths JSONB, identical BOOLEAN);
```

### Transform

```sql
hartonomous.transform.translate(
    text             TEXT,
    target_lang      TEXT,
    arena_recipe     JSONB DEFAULT NULL
) RETURNS TEXT;

hartonomous.transform.summarize(
    text             TEXT,
    target_length    INT,
    arena_recipe     JSONB DEFAULT NULL
) RETURNS TEXT;

hartonomous.transform.style_transfer(
    text             TEXT,
    target_register  TEXT,                       -- 'formal', 'casual', 'archaic', 'technical'
    arena_recipe     JSONB DEFAULT NULL
) RETURNS TEXT;

hartonomous.transform.paraphrase(
    text             TEXT,
    n_variants       INT DEFAULT 3
) RETURNS TEXT[];
```

### Compare

```sql
hartonomous.compare.cross_model_consensus(
    entity_text     TEXT,
    arena_filter    TEXT[] DEFAULT NULL
) RETURNS TABLE (
    entity_hash     BYTEA,
    centroid        POINT4D,
    dispersion      FLOAT8,
    n_models        INT,
    agreement_score FLOAT8
);

hartonomous.compare.cross_model_divergence(
    entity_text  TEXT,
    model_a      TEXT,
    model_b      TEXT
) RETURNS FLOAT8;                               -- Hausdorff over firefly clouds

hartonomous.compare.model_audit(
    candidate_safetensors_path TEXT,
    benchmark_arenas           TEXT[]
) RETURNS TABLE (
    arena      TEXT,
    consensus  FLOAT8,
    candidate  FLOAT8,
    delta      FLOAT8
);
```

### Analyze

```sql
hartonomous.analyze.idiomaticity(
    compound        TEXT,
    measurement     TEXT DEFAULT 'centroid'      -- 'centroid', 'frechet', 'hausdorff'
) RETURNS FLOAT8;

hartonomous.analyze.frayed_edges(
    edge_type       TEXT,
    threshold       FLOAT8 DEFAULT 0.7,
    max_results     INT DEFAULT 100
) RETURNS TABLE (
    entity_a_hash   BYTEA,
    entity_a_text   TEXT,
    entity_b_hash   BYTEA,
    entity_b_text   TEXT,
    archetype_fit   FLOAT8
);

hartonomous.analyze.antipodal_violations(
    edge_type       TEXT DEFAULT 'antonym',
    expected_dist   FLOAT8 DEFAULT 3.14
) RETURNS TABLE (
    pair            JSONB,
    actual_distance FLOAT8,
    deviation       FLOAT8
);
```

### Recompose

```sql
hartonomous.recompose.distill_safetensors(
    target_arch_json    JSONB,
    arena_recipe        JSONB,
    significance_floor  FLOAT8 DEFAULT 0.5,
    output_path         TEXT
) RETURNS BIGINT;                                -- bytes written

hartonomous.recompose.refine_model(
    source_provenance  TEXT,                     -- e.g. 'huggingface_model:llama4-maverick'
    output_path        TEXT,
    significance_floor FLOAT8 DEFAULT 0.5
) RETURNS BIGINT;

hartonomous.recompose.generate_text(
    prompt          TEXT,
    arena_recipe    JSONB DEFAULT NULL,
    max_tokens      INT DEFAULT 500
) RETURNS TEXT;
```

### Provenance

```sql
hartonomous.provenance.attribution(
    response_entity_id  BYTEA
) RETURNS TABLE (
    source_provenance   TEXT,
    contribution_pct    FLOAT8,
    n_edges             INT
);

hartonomous.provenance.trustrank(
    provenance_code     TEXT
) RETURNS TABLE (
    arena_code          TEXT,
    avg_mu              FLOAT8,
    n_edges             INT
);

hartonomous.provenance.audit_chain(
    response_entity_id  BYTEA,
    depth               INT DEFAULT 5
) RETURNS JSONB;
```

### Lexical

```sql
hartonomous.lexical.senses_of(word TEXT, language TEXT DEFAULT 'eng')
    RETURNS TABLE (sense_id BYTEA, gloss TEXT, mu FLOAT8);

hartonomous.lexical.hypernym_chain(synset_id BYTEA, depth INT DEFAULT 10)
    RETURNS TABLE (depth INT, hypernym_id BYTEA, gloss TEXT);

hartonomous.lexical.synonyms(word TEXT, language TEXT DEFAULT 'eng')
    RETURNS TABLE (synonym_id BYTEA, mu FLOAT8);

hartonomous.lexical.etymology(word TEXT, language TEXT DEFAULT 'eng', max_depth INT DEFAULT 10)
    RETURNS TABLE (depth INT, ancestor_form TEXT, ancestor_language TEXT, attestation TEXT);
```

### Cross-lingual

```sql
hartonomous.cross_lingual.translation_pairs(
    text  TEXT,
    src_lang  TEXT,
    target_langs  TEXT[]
) RETURNS TABLE (lang TEXT, translation_text TEXT, mu FLOAT8);

hartonomous.cross_lingual.parallel_sentences(text TEXT, target_lang TEXT, max_results INT DEFAULT 10)
    RETURNS TABLE (sentence_id BYTEA, translation TEXT, attestation_count INT);
```

### Geometric

```sql
hartonomous.geometric.frechet(
    entity_a_hash BYTEA, entity_a_type INT,
    entity_b_hash BYTEA, entity_b_type INT
) RETURNS FLOAT8;

hartonomous.geometric.suffix_similar(text TEXT, max_results INT DEFAULT 20)
    RETURNS TABLE (similar_text TEXT, frechet_distance FLOAT8);

hartonomous.geometric.dispersion(entity_hash BYTEA, entity_type INT)
    RETURNS FLOAT8;
```

## Why this is one surface, not five

A naive product split would have:
- An "inference engine" with its own SDK
- A "model refinement service" with its own API
- A "model registry" for distilled models
- A "translation service"
- A "comparison/analysis" tool

Each would be a separate microservice with its own data model, deployment, ops, and integration story.

Hartonomous offers all five as SQL functions in one schema. Customer code:

```python
import psycopg
conn = psycopg.connect("postgresql://...")
cur = conn.cursor()

# Inference
cur.execute("SELECT * FROM hartonomous.inference.converse(%s)", ("What is a cat?",))
result = cur.fetchone()

# Refinement
cur.execute("SELECT hartonomous.recompose.refine_model(%s, %s, %s)",
            ("huggingface_model:llama4-maverick", "/output/refined.safetensors", 0.6))

# Translation
cur.execute("SELECT hartonomous.transform.translate(%s, %s)",
            ("Hello world", "es"))

# Cross-model analysis
cur.execute("SELECT * FROM hartonomous.compare.cross_model_consensus(%s)", ("cat",))
```

One database connection. One auth/authz boundary. One operational concern. The same infrastructure handles all five.

## Function authorship and namespacing

The cognitive surface is owned by Hartonomous. Customer extensions live in different schemas:

- `hartonomous.*` — substrate-canonical functions, supported and versioned
- `customer.*` — customer-authored functions on the same substrate (their own composition logic, custom recipes, domain-specific aggregations)
- `experimental.*` — substrate-team experimental functions, not yet stable
- `_internal.*` — substrate-team functions only callable from within trusted contexts

Versioning: each substrate release versions the cognitive surface. Function signatures are stable within major versions; new functions added; deprecated functions removed only at major version boundaries.

## Why the SQL surface is the right boundary

Three reasons:

1. **Customer integration cost is minimized.** Every modern language has a Postgres driver. Every BI tool, ETL platform, application framework can connect. The cognitive surface needs no separate SDK.

2. **Composability with existing data infrastructure.** Customers already running Postgres can layer the substrate alongside their own data. Joins between substrate state and customer business data are free (within the same database). Federated queries via foreign data wrappers extend this across databases.

3. **Audit and observability.** Postgres logging, query plans, statistics, replication, backups — all apply to the cognitive surface. No separate observability stack to operate.

The trade-off: the substrate's internal optimizations are bounded by Postgres's query planner. For the hot path (per-hop A\* traversal), the native extension's bulk-fetch SPI bypasses the planner. For everything else, the planner does its job.

## Cross-references

- Inference engine that powers `hartonomous.inference.*`: `10-architecture/07-inference-engine.md`
- Recomposers that power `hartonomous.recompose.*`: `10-architecture/06-recomposer-contract.md`
- Geometric primitives: `10-architecture/03-geometry-4d.md`
- Significance backing all rating-aware functions: `10-architecture/04-significance-glicko.md`
- Full function reference: `20-technical/08-cognitive-functions.md`
- Function-authoring checklist: `40-process/checklists/02-cognitive-function-checklist.md`
