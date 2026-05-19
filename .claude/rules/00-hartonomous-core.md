---
description: Hartonomous substrate — core invariants and normative pointers. Always-on overlay.
---

## Core invariants — enforce in every code change

**Entity table**: `substrate.entity` stores atoms and compositions. Semantic identity is `hash substrate.hash_value`; the physical PostgreSQL PK includes `partition_bucket` only for hash-bucket partitioning. No `id`, no `entity_type_id`. Structural classifications go in `substrate.entity_classification(entity_hash, entity_type_id, provenance_id)`. Classification codes have bounded reference rows and, where attested, content-hashed entity targets reached by typed edges; authoritative classification consensus lives on `substrate.edge_significance`.

**Entities vs content — two trees, one vocabulary**: Entities are the *building blocks* — reusable identities referenced by many trajectories. Entity-tier types: `codepoint`, `grapheme_cluster`, `word_form`, `morpheme`, `lemma`, `synset`, `collation_element`, `language_name`, `model_architecture`, `tensor`, `tokenizer_model`. Content is the *trajectory* through entities. Content-tier types: `text_composition`, `paragraph`, `document`, `audio_recording`, `audio_chunk`, `pixel_region`, `video_frame`. `whale` is one word_form entity referenced ~1500 times by Moby Dick's content trajectory; Moby Dick is content whose Merkle identity IS its walk through word_form entities. Both live in `substrate.entity` by BLAKE3 hash, both have physicality (entity physicality = the brick's own internal structure; content physicality = the trajectory through entity bricks), both can be edge participants. Conflating them ("everything is a composition") loses the load-bearing distinction that makes cross-source consensus accumulate on entities while content trajectories anchor to provenance via `has_source` edges.

**No placement in hash**: `ComputeHash` accepts content bytes only. `ComputeMerkleHash` accepts ordered child hashes. `ComputeEdgeHash` accepts `(edge_type_id, participant_hashes)`. Position, ordinal, filename, tensor name, model id, line number NEVER enter the hash. Placement lives in the composition's `LINESTRINGZM` physicality vertex stream (Y mantissa = `bb_pack_ordinal_rle(ordinal, rle_count)`), on typed edges (`has_source`, `in_model`, edge member role position), on model-source tables, or on provenance — never in identity. Same content in two places = one entity referenced from two trajectories, not two entities. The geometry IS the indexed child manifest.

**No raw PostGIS on physicality**: Use `substrate.st_4d_*` / `substrate.st_s3_*`. `ST_Distance`, `ST_Centroid`, `ST_FrechetDistance`, `ST_HausdorffDistance` drop the M dimension and are forbidden on `substrate.physicality`.

**Phantom per-role-unit entities are deprecated** (2026-05-08 correction, AP-25). Types `attention_head`, `ffn_neuron`, `embedding_position`, `attention_pattern`, `moe_expert_neuron`, `lora_component`, `conv_filter`, `bbox_projection`, `class_projection`, `svd_rank_component`, `codec_codevector`, and all other per-role-unit synthetic types must NOT be emitted by new code. Track 2 transformation tensors (FFN, attention QKV, MoE, LoRA) manifest as typed attestation EDGES between existing content entities. Working template: `src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs`.

**One ingestion pipeline**: `StreamingIngestionPipeline.cs` owns channels, batching, transactions, and significance priming. Every decomposer is a pure streaming producer that calls `IRecordSink.EmitAsync` and does nothing else. No decomposer-private channels, no decomposer-phase-wide `ResolveEntityIdsAsync`, no two-pass join accumulation.

**Seed decomposers use core decomposers**: All text-bearing content from any decomposer routes through `CanonicalTextDecomposer.Emit`. No decomposer calls `ComputeHash(string)` on multi-character text to produce a text_composition-tier atom. Same string anywhere = one hash.

**Arenas are open vocabulary**: Significance priming cross-products against all arenas at insert time with no WHERE filter on context code. Never hardcode the starter arena list.

**Inference traverses, does not invent**: Engine traverses and reweights existing edges via Glicko-2. It does not emit new structural knowledge edges. Session-scoped output composition entities (with `user_session` provenance) are the only new entities inference creates.

**Tensor decomposition is layer-type-based, not modality or architecture-based** (AP-26, AP-30, AP-32): 4 primitives (`Linear`, `LocalKernel`, `Normalization`, `Lookup`) + ~13 tuples + per-architecture TupleResolver data tables. One pass per primitive (4 files) + one pass per tuple-attestation kind (5 files) + the resolver = ~10 total decomposer files. New architecture = new resolver table row, not a new decomposer file.

**Sign is load-bearing** (AP-31): Glicko `score = value > 0 ? 1.0 : 0.0; weight = Math.Abs(value)`. Never call `Math.Abs` on a signed tensor projection and treat only the magnitude. Negative correlation is load-bearing evidence.

**Threshold-only LTH discrimination at ingest** (AP-33): Per-tensor adaptive magnitude floor decides signal vs. jitter. No top-K truncation. Every cell above floor is a winning ticket; every cell below is gradient-descent noise that encodes no learned function.

**No activation-based ingestion** (AP-34): No synthetic prompts, no forward passes, no GPU at ingest. The trained tensor's own distribution IS the activation pattern. Read weights directly, apply the tensor's math (Q^T·K, FFN response, embedding cosine, conv response), threshold, emit.

**Unicode + ISO is the TEXT-tier lynchpin** (not a universal reduction target): The universal absorbent property is the universal SHAPE (mantissa-packed `LINESTRINGZM` content trajectories + typed edges), NOT atom-reduction. Per rule 15: tier-T composition LINESTRINGZM walks through tier-(T−1) entity hash refs, bottoming out at the modality's own atom POINTZM — codepoint (text), audio sample (audio), pixel intensity (image), tensor cell (model). Per seed/entity_type.sql: image entity tier is `pixel_region` / `visual_concept` / `object_query`; audio is `audio_recording` / `audio_chunk` / `codec_codevector`; text is `codepoint` / `grapheme_cluster` / `word_form` / `morpheme` / `lemma` / `synset` / `collation_element` / `language_name` / `text_composition` / `paragraph` / `document`. Cross-modal grounding is typed edges BETWEEN modality-native content entities — CLIP/BLIP/Florence emit `model_cross_modal_alignment(word_form, pixel_region)`; Whisper emits `(word_form, audio_chunk)`. Reducing audio/image/video to text encodings is lazy binary-blob storage with text-flavored framing — banned. Substrate vocabulary for what conventional ML calls "tokens" is `word_form` (or the applicable entity_type); model tokenizers are model-source metadata, not substrate identity. Unicode + ISO is the lynchpin specifically FOR TEXT — text is the cross-reference surface every text-handling source returns to. Reference: [[project-unicode-iso-as-lynchpin]] memory.

**XML-flat for per-codepoint UCD pre-gen** (not grouped): `ucd.all.flat.xml` is self-contained per-char with no group-inheritance state machine. Parser simplicity wins over grouped's compressed-size advantage. `ext/libhartonomous/codegen/gen_ucd_flat.c` (renamed from `gen_ucd_grouped.c`) walks flat XML to emit all ~100 UAX #44 attributes. Reference: [[project-pre-gen-not-substrate-ingestion]] memory + UAX #42.

**No modality-specific attestation_type** (AP-38): `substrate.attestation_type` has 3 generic rows — `positive_evidence` / `negative_evidence` / `neutral_evidence`. Sign discrimination ONLY. Source + domain discrimination lives on `(provenance, arena)`, not on `attestation_type`. POS / sense / language / morph / model_attention compete on the same `substrate.edge_significance` rows under different arenas.

**No orchestration-boundary backfill** (AP-37): Drain completion triggers post-passes (`PopulateEdgeTrajectoriesAsync` + `PrimeAllSignificanceAsync` invoked inside `StreamingIngestionPipeline.DrainPendingAsync`) independent of runner phases. Decomposers run independently; the pipeline ingests / corroborates / primes continuously. P1f-followup target: fully inline at INSERT-SELECT with no NULL-geom window even briefly.

**No bit-perfect export**: The substrate is the consensus surface, not the archive. No round-trip obligation for any source. Build-a-bear synthesizes NEW outputs from accumulated multi-source consensus. Re-emission gives canonical-decomposition output (denser than any source on signal, sparser than any source on noise via LTH), not byte-equal-to-the-specific-file-ingested.

## Normative specs — when any rule, plan, or code disagrees, the spec is correct

- `docs/00-substrate-spec.md` — substrate model (four pillars, attestation edges, Glicko-2 surfaces, layer-type decomposer library, Build-a-bear synthesis, fireflies, sparse honest recording, phantom debt deprecation list)
- `docs/01-tensor-primitive-spec.md` — canonical tensor form (4 primitives, ~13 tuples, per-architecture TupleResolver tables, sign-bearing attestations, decomposer library shape)

## Schema source of truth

`sql/schema/bootstrap.sql` + included files under `sql/schema/`. `sql/migrations.archive/` is audit-only. Recompute counts from seed files; never republish from stale docs.

## Anti-patterns

`.claude/rules/45-anti-patterns.md` — 38 documented APs with citations (AP-1..AP-38, including AP-37 no-phase-backfill and AP-38 no-modality-specific-attestation-type). Read before planning any ingestion / inference / synthesis work.

## Finish work

Drive tasks end-to-end. Stop only on verified completion, a true blocker (missing credential, destructive-action authorization, genuine outcome-affecting ambiguity), or explicit user stop. "I could imagine multiple approaches" is not ambiguity — it is a decision in scope. Done = compiles + tests pass + behavior verified against the user's actual goal. If a fix's bug pattern recurs elsewhere, find and fix all instances. Recompute facts from source; never republish from stale summaries.
