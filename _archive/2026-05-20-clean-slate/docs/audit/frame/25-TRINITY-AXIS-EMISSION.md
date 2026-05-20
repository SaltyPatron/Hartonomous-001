# Trinity-axis emission framing

User-articulated decomposer-emission taxonomy. Two ORTHOGONAL axes; collapsing them is a category error (the failure mode that produced the 90%-scope-loss earlier this conversation).

## Axis 1 — INPUT source category

Where the data comes from. Provenance dimension.

- **Seed corpora** (ships with the substrate): UCD, ISO 639, WordNet, OMW, UD, Wiktionary, Tatoeba
- **Per-customer corpus** (uploaded by a customer for refinement/agent work)
- **Ingested AI model checkpoint** (.safetensors / .pt / .bin / .ckpt from HuggingFace or elsewhere)
- **User session** (prompt, conversation turn, feedback event)
- **Runtime synthesis** (substrate-synthesized model export, recomposer output)

## Axis 2 — EMISSION shape category

What shape each piece of output lands in. Decomposer contract dimension.

- **App data** — canonical structural references the substrate uses (codepoint atoms, language entities, synset entities, lemma entities, deprel inventory, reference vocabularies). Inference spec uses the same term: "junction-table metadata describing what an entity CAN be" — POS possibilities, sense candidates, morph features, codepoint properties.
- **User data** — modality-bound content (text content, audio recordings, image pixels, video frames, code source, model tensor cells, prompts, conversation turns)
- **Substrate data** — arena-rated attestation evidence edges with provenance (`translates_to`, `has_sense`, `model_attention_pattern`, `has_audio_recording`, `model_cross_modal_alignment`, etc.). Inference spec uses the related term "seed edges" for substrate content edges that exist before inference runs.

## The orthogonality

The two axes are INDEPENDENT.

- A single seed corpus produces emissions across all three Axis-2 buckets.
- A single customer upload produces emissions across all three Axis-2 buckets.
- A single ingested AI model produces emissions across all three Axis-2 buckets.

The decomposer contract is about EMISSION shape (Axis 2). The INPUT source (Axis 1) gets recorded separately as provenance.

## Examples

**Wiktionary (Axis-1 = seed corpus)** emits:
- App data: lemma entities (canonical lexical references other decomposers use); references existing codepoint atoms + language_name entities
- User data: definition text, example sentences, etymological narrative text — text-modality content
- Substrate data: cross-lingual `translates_to` edges; etymological `derived_from` edges; phonetic `has_ipa_transcription` edges; per-language sense attestations under appropriate arenas

**Tatoeba (Axis-1 = seed corpus)** emits:
- App data: references existing language_name + codepoint entities
- User data: sentence text (text_composition / paragraph entities); audio recordings
- Substrate data: `translates_to` between sentences; `has_audio_recording`; per-arena attestations

**Safetensors (Axis-1 = ingested model)** emits:
- App data: references existing word_form entities via the model's tokenizer; references existing language_name / model_architecture references
- User data: tensor entities and tensor-cell content; tokenizer config text; model card text
- Substrate data: `model_attention_pattern`, `model_ffn_factor`, `model_concept_similarity`, `model_cross_modal_alignment` edges with sign-aware Glicko under arena-conditional priming; firefly POINTZM per token

## Per-decomposer contract template

Every per-decomposer doc factors emissions explicitly:

```
Decomposer: <name>
Axis 1 (input source category): <seed / per-customer / model / session / runtime>
Axis 2 (emission shapes):
  - app data refs/emissions: <list>
  - user data emissions: <list>
  - substrate data emissions: <list>
Falsification: <test that verifies decomposer's output is correct>
```

## Open question (pending user confirmation)

Are structural typed edges (uncontested, ship with canonical reference — `has_codepoint`, `has_lemma`, `has_iso_639_3_code`) **app data** OR **substrate data**?
- Reading (a) — both are substrate data; structural edges have one authoritative provenance with high initial mu, contested edges accumulate cross-source evidence. Simpler.
- Reading (b) — structural = app data; only arena-contested = substrate data. Cleaner separation, harder per-decomposer audit.

Current default during reading: (b) — substrate is specifically the consensus-resolution surface, not "everything edge-shaped." Subject to confirmation.
