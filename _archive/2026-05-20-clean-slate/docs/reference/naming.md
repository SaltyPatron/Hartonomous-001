# Naming Reference

Every naming convention in the repo. If your name doesn't match a row here, the name is wrong.

---

## C#

| Element | Convention | Example |
|---|---|---|
| Namespace | `Hartonomous.{Project}[.{Folder}[.{Subfolder}]]` | `Hartonomous.Core.Compute.Ingestion` |
| Class (concrete) | PascalCase | `SafetensorsDecomposer` |
| Class (abstract) | `Base` + PascalCase | `BaseDecomposer` |
| Interface | `I` + PascalCase | `IDecomposer` |
| Record | PascalCase | `EntityHandle` |
| Enum | PascalCase | `Phase` |
| Enum value | PascalCase | `Phase.UcdUca` |
| Public method | PascalCase | `DecomposeAsync` |
| Async method | + `Async` suffix | `DecomposeAsync` |
| Private field | `_` + camelCase | `_pipeline` |
| Local variable | camelCase | `entityCount` |
| Parameter | camelCase | `cancellationToken` |
| Constant | PascalCase | `DefaultBatchSize` |
| Generic type parameter | `T` or `T` + PascalCase | `T`, `TEntity` |
| Test class | `{ClassUnderTest}Tests` | `SafetensorsDecomposerTests` |
| Test method | `Method_Scenario_ExpectedResult` | `Decompose_EmptyInput_ReturnsZeroEntities` |
| Options class | `{Feature}Options` | `DatabaseOptions` |
| Config class | `{Feature}Config` | `DecomposerConfig` |
| Exception | `{What}Exception` | `ComputeAllocationException` |
| Native interop class | `{What}Native` | `Blake3Native` |
| Decomposer | `{Source}Decomposer` | `WordNetDecomposer` |
| Recomposer | `{Output}Recomposer` | `TextRecomposer` |
| Analysis pass | `{What}Pass` | `EmbeddingFireflyPass` |
| Phase enum value | PascalCase noun phrase | `WordNetOmw`, `ModelDecomp` |

## C# file names

| Element | Convention |
|---|---|
| File holding type `T` | `T.cs` (exactly) |
| Test file | `{TypeUnderTest}Tests.cs` |
| InternalsVisibleTo metadata | `InternalsVisibleTo.cs` |

---

## SQL

| Element | Convention | Example |
|---|---|---|
| Schema | `substrate` (only) for content; `monitor` for telemetry | `substrate.entity` |
| Table | snake_case noun, singular | `substrate.entity`, `substrate.edge_member` |
| Reference table | snake_case noun, singular | `substrate.pos`, `substrate.deprel` |
| Junction table | `entity_{class}` or `{a}_{b}` | `substrate.entity_pos`, `substrate.codepoint_property` |
| Partition | `{parent}_{code}` | `substrate.entity_codepoint`, `substrate.physicality_waveform` |
| Column | snake_case | `entity_id`, `edge_type_id`, `mu`, `sigma` |
| Primary key column | `id` (or composite as appropriate) | `id BIGSERIAL` |
| Foreign key column | `{referenced_table_singular}_id` | `entity_id`, `provenance_id` |
| Index | `{table}_{purpose}_idx` | `entity_word_hash_idx` |
| Unique index | `{table}_{cols}_uidx` | `entity_hash_type_uidx` |
| GiST index | `{table}_gist` | `physicality_waveform_gist` |
| BRIN index | `{table}_{col}_brin` | `physicality_waveform_m_brin` |
| Function | snake_case verb | `compute_centroid_4d`, `traverse_astar` |
| Procedure | snake_case verb | `ingest_entity_batch`, `update_significance` |
| View | snake_case noun | `entity_summary`, `phase_progress` |
| Trigger | `{table}_{event}_{action}_trg` | `entity_after_insert_emit_centroid_trg` |
| Domain | snake_case | `hash_value`, `significance_mu`, `tier_number` |
| Composite type | snake_case | `entity_record`, `edge_record` |
| Migration | `{NNNN}_{snake_case_intent}.{up,down}.sql` | `0042_add_lexicalized_compound_edge_type.up.sql` |

## SQL identifier codes (the `code` column on reference tables)

| Reference table | Code style | Example |
|---|---|---|
| `entity_type.code` | snake_case singular noun | `codepoint`, `word_form`, `tensor` |
| `edge_type.code` | snake_case verb-or-relation phrase | `has_sense`, `aligned_to_synset`, `in_model` |
| `edge_role.code` | snake_case role noun | `source`, `target`, `mediator` |
| `physicality_type.code` | snake_case noun | `waveform`, `embedding_firefly`, `weight_distribution` |
| `provenance.code` | snake_case organization or corpus | `princeton_wordnet`, `huggingface_model` |
| `pos.code` | UPPER_SNAKE (UD convention) | `NOUN`, `VERB`, `ADP` |
| `deprel.code` | lowercase:colon (UD convention) | `nsubj`, `obj`, `nmod:poss` |
| `language.iso639_3` | ISO 639-3 lowercase | `eng`, `deu`, `jpn` |
| `tensor_role.code` | snake_case | `query_projection`, `output_norm`, `embedding_table` |

---

## C / C++ (native)

| Element | Convention | Example |
|---|---|---|
| Public function | `htns_{module}_{verb}` | `htns_blake3_hash`, `htns_centroid_4d` |
| Internal function | `_htns_{module}_{verb}` (leading underscore) | `_htns_blake3_init` |
| Type | `htns_{name}_t` | `htns_point4d_t`, `htns_hash_value_t` |
| Macro | `HTNS_{NAME}` | `HTNS_HASH_BYTES`, `HTNS_MAX_VERTICES` |
| Constant | `HTNS_{NAME}` | `HTNS_VERSION_MAJOR` |
| Header guard | `HTNS_{MODULE}_H` | `HTNS_BLAKE3_H` |
| File (impl) | snake_case | `blake3.c`, `centroid_4d.c` |
| File (header) | snake_case | `blake3.h`, `centroid_4d.h` |
| SIMD-specialized impl | `{module}_{isa}.c` | `centroid_4d_avx2.c` |
| Test | `test_{module}.c` | `test_blake3.c` |

## PostgreSQL extension SQL

| Element | Convention | Example |
|---|---|---|
| Extension function | `{module}.{verb}` (schema-qualified) | `hartonomous.blake3_hash`, `hartonomous.traverse_astar` |
| Extension type | snake_case | `point4d`, `linestring4d` |
| Extension operator | standard symbols | `<->`, `<=>`, `&&` |
| Operator class | `{type}_ops` | `point4d_ops`, `linestring4d_ops` |

---

## Files and folders

| Element | Convention | Example |
|---|---|---|
| Repo root folder | lowercase | `src`, `sql`, `ext`, `docs`, `scripts`, `tests` |
| C# project folder | `Hartonomous.{Pascal}` | `Hartonomous.Decomposers` |
| C# subfolder | PascalCase | `Compute/Ingestion/`, `Decomposition/` |
| Decomposer folder | PascalCase singular | `WordNet/`, `Safetensors/`, `Tatoeba/` |
| SQL schema folder | lowercase plural | `domains/`, `composite-types/`, `reference/`, `junctions/` |
| Native module folder | lowercase | `ext/libhartonomous/src/`, `ext/hartonomous_pg/src/` |
| Recipe doc | `{NN}-{verb}-{noun}.md` | `08-add-decomposer.md` |
| Reference doc | `{name}.md` | `file-layout.md` |
| Script | `{Verb}.ps1` | `Migrate.ps1`, `All.ps1` |
| Module script | `Hartonomous.{Module}.psm1` | `Hartonomous.Docker.psm1` |

---

## Provenance codes (when adding new corpora)

Format: `{organization_or_curator}_{corpus_kind}` if disambiguation is needed; otherwise just `{organization}`.

| Kind | Example |
|---|---|
| Authoritative standard | `unicode_consortium`, `sil_international`, `iso_standard` |
| Academic curated | `princeton_wordnet`, `universaldependencies`, `omwn_consortium` |
| Community curated | `wiktextract`, `tatoeba`, `commoncrawl` |
| Model-derived | `huggingface_model`, `meta_llama`, `openai_gpt` |
| User input | `user_session`, `user_corpus_{name}` |
| System computed | `system_computed`, `system_inferred` |

---

## Migration intent strings

The `{snake_case_intent}` portion of a migration filename describes WHAT the migration does in 3–6 words.

| Pattern | Example |
|---|---|
| `add_{thing}` | `add_lexicalized_compound_edge_type` |
| `extend_{thing}_{aspect}` | `extend_codepoint_property_unicode_15_1` |
| `populate_{thing}` | `populate_dependency_trajectories` |
| `fix_{thing}_{aspect}` | `fix_srid_in_centroid_calculations` |
| `rename_{old}_to_{new}` | `rename_lexname_to_lexicographer_file` |
| `drop_{thing}` | `drop_legacy_geometry_column` |
| `seed_{corpus}` | `seed_iso639` |
