# Hartonomous Semantic Evaluation Rubric

A semantic explanation, plan, review, or implementation path passes only if it does all of the following:

## Pass criteria

1. **Answers the user's concrete example or claim directly before abstracting.** If the user says "overload", the response addresses how "overload" exists in the substrate before discussing architecture.

2. **Names the relevant decomposition levels or substrate layers.** Uses the correct entity type codes (`codepoint`, `word_form`, `text_composition`, `synset`, etc.) from `sql/schema/seed/entity_type.sql`.

3. **States which facts belong to which layer:**
   - Entity content → `substrate.entity` (atoms and compositions, BLAKE3 hash-only identity)
   - Entity classification → `substrate.entity_classification(entity_hash, entity_type_id, provenance_id)`
   - Edge content → `substrate.edge` + `substrate.edge_member` (typed n-ary relations, trajectory geometry, significance)
   - Physicality → `substrate.physicality` (POINTZM/LINESTRINGZM/MULTILINESTRINGZM, GiST-indexed)
   - Reference vocabulary → `pos`, `deprel`, `sense`, `language`, etc.
   - Junction surfaces → `entity_pos`, `entity_language`, `entity_morph_feature`, etc.
   - Reconstruction metadata → `substrate.sequence.ordinal`, `provenance`, edges like `has_source` and `in_model`

4. **Preserves the distinction between infrastructure and source content.** Reference tables and junctions enable lookups; they are not the same as entity/edge substrate content.

5. **Preserves the distinction between identity and reconstruction.** BLAKE3 hashes (`BaseDecomposer.ComputeHash()`, `ComputeMerkleHash()`, `ComputeEdgeHash()`) cover content only. Position, filename, tensor name, ordinal never enter the hash.

6. **Preserves the distinction between ingestion and inference.** Ingestion (`src/Hartonomous.Decomposers/`) records all candidates deterministically. Inference (`src/Hartonomous.Engine/`) traverses and reweights existing edges. Inference does not create knowledge edges.

7. **Grounds measurable claims in exact computation** when the repo or tools can provide the answer. No estimated migration counts, file counts, or completion percentages.

8. **Names the concrete repo artifacts** (files, methods, migrations, test projects) that enforce the decision.

9. **Per-role units of Track 2 transformation tensors are described as typed attestation EDGES between existing content entities, NOT as synthetic per-role-unit entities.** A pass on case 11 (per-role units) requires using the corrected attestation-edge framing — `model_attention_pattern`, `model_concept_similarity`, `model_ffn_factor` edges between `word_form` entities, with `attestation_type` (per `sql/schema/seed/attestation_type.sql`) on the rating event distinguishing the kind of model evidence. Naming any phantom entity type (`ffn_neuron`, `attention_head`, `attention_pattern`, `embedding_position`, `logit_projection`, `moe_route`, `moe_expert_neuron`, `attention_archetype`, `svd_rank_component`, `codec_codevector`, `audio_codec_filter`, `bbox_projection`, `class_projection`, `conformer_component`, `conv_filter`, `diffusion_component`, `lora_component`, `modality_basis_vector`, `moe_route_direction`, `object_query_slot`, `vision_feature_direction`, `residual_direction`, `archetype`) as a load-bearing concept is a fail.

10. **Cross-model corroboration is described as separate `attestation_type`-distinguished rating events on the same edge hash.** A pass on case 12 requires distinguishing row-identity dedup (skip duplicate edge INSERT via `ON CONFLICT DO NOTHING`) from rating-event dedup (the second model's emission MUST fire a Glicko event on the existing edge with its own `attestation_type`). Treating the second emission as silently-discarded duplicate is a fail.

11. **Fireflies are described as a derived value-add side-channel, NOT the inference mechanism.** A pass on case 13 requires stating that fireflies are POINTZM physicalities attached to existing word_form entities (one per ingested model per token), enabling cross-model consensus visualization and conventional embedding queries with consensus weighting — but inference produces answers via A* over attestation edges (per `35-inference-and-godel.md`). Treating firefly proximity as an inference primitive is a fail.

12. **Decomposers are described as organized by tensor layer-type, NOT by downstream modality.** A pass on case 14 requires referring to layer-type decomposers (universal: `AttentionQkvLayerDecomposer`, `FfnLayerDecomposer`, etc.; specialist: `CrossAttentionLayerDecomposer`, `ConvLayerDecomposer`, etc.) per [`docs/specs/decomposers/layer-type-library.md`](../../../docs/specs/decomposers/layer-type-library.md). Organizing by modality (`TextModelDecomposer`, `VisionModelDecomposer`, etc.) is a fail.

13. **Cross-modal binding is described via `CrossAttentionLayerDecomposer` producing edges between content entities of different modalities.** A pass on case 15 requires stating that vision-language and diffusion-text-conditioning models decompose by composition of layer-type + content + cross-attention decomposers; the substrate's text consensus surface is anchored on the same `word_form` entities that text-only models contribute to. Inventing a parallel "vision_token" / "audio_token" entity universe disconnected from text is a fail.

14. **The Build-a-bear recomposer is described as synthesis-from-consensus across all ingested models, NOT single-source phantom-scatter round-trip.** Per spec §VI: user specifies an arbitrary `TargetArchitectureSpec` (any combination of MoE / LoRA / layer count / hidden dim / modality mix); per-layer-type synthesizers (reciprocal of layer-type decomposers) project substrate consensus into the target tensor basis with honest abstention on under-attested cells. Describing the recomposer as round-tripping a single source's stored phantoms is a fail.

## Common failure patterns

| Pattern | What goes wrong | What should happen |
|---------|-----------------|-------------------|
| Graph flattening | Describes Hartonomous as a "knowledge graph" or "ontology" | It's a substrate with typed n-ary edges, Glicko-2 significance, trajectory geometry, and Fréchet distance. Not triples. Not SPARQL. |
| Vector/embedding talk | Uses "embedding", "cosine similarity", "ANN", "nearest neighbor" | No embeddings. No ANN. Distance is Glicko-2 significance on edges + Fréchet/Hausdorff on S3 geometric coordinates. |
| RAG confusion | Describes retrieval + generation pipeline | No forward pass. No generation model. Inference IS traversal. |
| Classification-as-entity | Pushes POS/sense/language into `substrate.entity` | Classifications live in reference tables and junctions. |
| Placement in hash | Includes position, filename, ordinal in identity hash | `ComputeHash()` accepts only content bytes. `ComputeEdgeHash()` accepts only `(edge_type_id, participant_hashes)`. |
| Inference creates edges | Says inference "discovers" or "creates" new relationships | Inference traverses and reweights existing edges. New edges come from ingestion only. |
| Plan-only stall | Stops at analysis when implementation was feasible | Carry through to code/docs/validation. |
| Estimate instead of compute | Says "approximately 20 schema files" when the repo can provide the exact count | Count exactly from `sql/schema/` or the current target directory before claiming it. |
