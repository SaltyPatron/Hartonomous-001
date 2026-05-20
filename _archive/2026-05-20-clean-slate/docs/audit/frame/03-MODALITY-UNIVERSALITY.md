# The substrate is universal across modalities

Source: `.claude/rules/25-physicality-4d.md`, spec §II.1 + §IX, `docs/specs/modalities/*.md`.

## Per-modality tier ladders

| Modality | Atom POINTZM | Tier-up composition geometry |
|---|---|---|
| Text | codepoint via Super-Fibonacci on S³ + UCD bitmask in M | grapheme → word → lemma → sentence → paragraph → document, each LINESTRINGZM of prior-tier centroids; documents may use MULTILINESTRINGZM for chapter branches |
| Audio | sample value with time-since-trigger on an axis | frame → chunk → utterance → recording, LINESTRINGZM or MULTILINESTRINGZM for polyphonic |
| Image | pixel region with 2D position + intensity + class | region → composition → image, POLYGONZM / MULTIPOLYGONZM for closed regions |
| Video | frame with 2D pixel + time + luminance / salience | frame → shot → scene → film, mixed subtypes |
| FFT / spectrogram | (time, frequency, magnitude, phase) per bin | per-band trajectory → full spectrogram, LINESTRINGZM / MULTILINESTRINGZM |
| Sequence (DNA, protein, MIDI, code tokens) | per-position embedding with axis-encoded position | k-mer → segment → full sequence, LINESTRINGZM |
| Model weights | per-tensor entity POINTZM; attestation edges are LINESTRINGZMs through content-entity centroids | per-layer trajectory → architecture, mixed subtypes |
| Application telemetry | event vertex with embedded content + time | call chain → request trace → session, LINESTRINGZM / MULTILINESTRINGZM |

The tier ladder is per-modality and unbounded. Tier-T entity's LINESTRINGZM has vertices that are tier-(T−1) entities' POINTZM centroids; each of those POINTZMs is the centroid aggregate of THAT entity's own LINESTRINGZM, recursively. Chain bottoms out at the modality's atom projection.

## Cross-modal binding via shared content entities + cross-attention edges

A model with cross-attention layers binds two content streams (text + non-text). The cross-attention QK math operates between tokens of the two streams. Decomposer emits typed bridge edges between content entities of the two modalities.

| Model | Stream A | Stream B | Bridge edge |
|---|---|---|---|
| CLIP | word_form (text encoder) | pixel_region (vision encoder) | `model_cross_modal_alignment` |
| BLIP | word_form | pixel_region | `model_cross_modal_alignment` |
| Flamingo | word_form (LM) | pixel_region (vision encoder) | `model_cross_modal_alignment` |
| Florence | word_form | pixel_region | `model_cross_modal_alignment` |
| Flux DiT | word_form (text encoders) | image-token-position (DiT latent) | `model_cross_modal_alignment` |
| SDXL | word_form (text encoders) | image-token-position (U-Net latent) | `model_cross_modal_alignment` |
| Whisper | word_form (decoder) | audio_chunk (encoder) | `model_acoustic_alignment` (future) |
| MusicGen | word_form (text encoder) | music_token (codec) | `model_audio_text_conditioning` (future) |

When multiple vision-language models agree that a particular visual concept binds to a particular text concept (e.g. images of dogs activate `word_form:dog`), they attest on the SAME bridge edge. CLIP + BLIP + Florence all firing `model_cross_modal_alignment(word_form:dog, visual_concept:dog-image-cluster)` accumulate evidence; consensus tightens. Same cross-model corroboration pattern as text-only attestations, extended across content modalities.

## Modality-agnostic decomposer pipeline

Single `StreamingIngestionPipeline` owns channels, batching, transactions, significance priming. All decomposers (text, code, model, modality, seed) are pure producers calling `IRecordSink.EmitAsync` and doing nothing else. No decomposer-private channels, no decomposer-phase-wide `ResolveEntityIdsAsync`, no two-pass join accumulation.

The substrate's "vocabulary for what conventional ML calls 'tokens'" is `word_form` (or whichever entity_type applies — `morpheme` / `codepoint` / `grapheme_cluster`). Each model's tokenizer is model-source METADATA mapping content hashes ↔ per-model integer IDs, NOT substrate identity.

## Application telemetry as substrate content

Event traces, request chains, sessions all decompose to LINESTRINGZM trajectories. Fault/security/regression/anomaly detection all become Fréchet against reference trajectory shapes. The substrate's geometric primitives work for ANY domain that has trajectories — categorical search (label match, regex match) misses everything that doesn't wear the right tag; substrate's geometry-first approach finds it anyway.

## The "Unicode + ISO is text-tier lynchpin, NOT universal reduction target"

The universal absorbent property is the universal SHAPE (mantissa-packed LINESTRINGZM content trajectories + typed edges between content-addressed entities), NOT atom-reduction across modalities. Per rule 15: every tier-T composition's LINESTRINGZM walks through tier-(T−1) entity hash refs, bottoming out at the modality's own atom POINTZM with real content-derived coords.

Cross-modal grounding is typed attestation EDGES between content entities of different modalities, each content-addressed in its OWN modality. Reducing audio to "text encodings" or images to "binary blobs with text-tagged metadata" is lazy binary-blob storage smuggled in with text-flavored framing — banned.

Cross-references:
- `frame/02-SUBSTRATE-MODEL.md` — the four pillars these tiers populate
- `frame/04-DECOMPOSER-ARCHITECTURE.md` — modality-agnostic decomposer pipeline mechanism
- `frame/26-MANTISSA-EXPLOITATION.md` — per-modality axis conventions
- `frame/06-EMBEDDING-PHYSICALITY-FIREFLIES.md` — cross-modal fireflies (per-model embedding POINTZM per token)
