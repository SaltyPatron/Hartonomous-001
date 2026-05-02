# Implementation Status

**Status:** Living document — update on every milestone close
**Last verified:** 2026-04-29 (initial)

This document tracks per-component implementation status against the milestones in `40-process/04-implementation-roadmap.md`. Update on every PR that closes a milestone or revises completion status.

---

## Milestone status

| Milestone | Status | Gate passing | Notes |
|---|---|---|---|
| M0 — Foundations | Not started | F1–F5 | Schema migrations + extension build |
| M1 — Identity layer | Not started | S1 | BLAKE3, entity/edge insert, dedup |
| M2 — UCD/UCA seed | Not started | (codepoint count) | Foundational atoms |
| M3 — Text decomposer | Not started | (NFC equivalence) | Universal text path |
| M4 — ISO 639 + WordNet + OMW | Not started | (synset count) | Lexical backbone |
| M5 — UD treebanks | Not started | (deprel count) | Syntactic skeleton |
| M6 — A\* + Glicko + cognitive surface | Not started | (inference latency) | Inference loop |
| M7 — First model ingestion | Not started | (model edge count) | Refinement primer |
| M8 — Recomposer engine | Not started | R1–R6 | Refinement output |
| M9 — Quality validation | Not started | P1 | First commercial gate |
| M10 — Wiktionary + Tatoeba + tiny-codes | Not started | (breadth metrics) | Coverage expansion |
| M11 — Multi-model ingestion | Not started | (provenance diversity) | Consensus substrate |
| M12 — Laplace-Linguistics-7B | Not started | P2 | First original product |
| M13 — Inference-as-service | Not started | P3 | Productization |
| M14 — Custom architecture | Not started | (customer engagement) | Third commercial product |

## Component status

### Native extension (`hartonomous_pg`)

| Component | Status | Tests passing |
|---|---|---|
| BLAKE3 SIMD | Not started | — |
| point4d / linestring4d / box4d types | Not started | — |
| GiST opclass for point4d | Not started | — |
| GiST opclass for linestring4d | Not started | — |
| 4D distance / centroid | Not started | — |
| 4D Fréchet | Not started | — |
| 4D Hausdorff | Not started | — |
| Super-Fibonacci spiral | Not started | — |
| Hilbert-4D | Not started | — |
| Glicko-2 update | Not started | — |
| traverse_astar | Not started | — |
| Laplacian eigenmap (firefly) | Not started | — |
| NFC normalization | Not started | — |

### Decomposers

| Decomposer | Status | Gates passing |
|---|---|---|
| UcdUcaDecomposer | Not started | — |
| Iso639Decomposer | Not started | — |
| Text decomposer (universal) | Not started | — |
| WordNetDecomposer | Not started | — |
| OmwDecomposer | Not started | — |
| UdDecomposer | Not started | — |
| WiktionaryDecomposer | Not started | — |
| TatoebaDecomposer | Not started | — |
| SafetensorsDecomposer | Not started | — |
| TinyCodesDecomposer | Not started | — |
| TextCorpusDecomposer | Not started | — |

### Recomposers

| Recomposer | Status | Gates passing |
|---|---|---|
| SafetensorsRecomposer (decoder-only) | Not started | — |
| SafetensorsRecomposer (MoE) | Not started | — |
| SafetensorsRecomposer (vision transformer) | Not started | — |
| SafetensorsRecomposer (diffusion) | Not started | — |
| TextRecomposer | Not started | — |
| WaveformRecomposer | Not started | — |
| ImageRecomposer | Not started | — |
| TreeSitterRecomposer | Not started | — |

### Cognitive surface

| Function category | Status |
|---|---|
| inference.* | Not started |
| transform.* | Not started |
| generate.* | Not started |
| compare.* | Not started |
| analyze.* | Not started |
| recompose.* | Not started |
| provenance.* | Not started |
| lexical.* | Not started |
| cross_lingual.* | Not started |
| geometric.* | Not started |

### Schema

| Schema element | Status |
|---|---|
| ref tables (entity_type, edge_type, etc.) | Not started |
| junc tables | Not started |
| substrate.entity (partitioned) | Not started |
| substrate.edge (partitioned) | Not started |
| substrate.edge_member | Not started |
| substrate.physicality (partitioned) | Not started |
| substrate.entity_significance (partitioned) | Not started |
| substrate.edge_significance (partitioned) | Not started |
| staging tables | Not started |
| monitor tables | Not started |

## Notes on inherited state

- **Fail_A repo state (`d:/Repositories/Laplace/Fail_A/`):** C# .NET 10 + native ext. Documentation references this for prior decisions but Success_C is a fresh implementation. Any code reuse from Fail_A requires explicit Architecture-Decision-Record entry.

- **Fail_B repo state (`d:/Repositories/Laplace/Fail_B/`):** C++ core + PG ext + thin C# CLI. Some code may be salvageable for the native extension (BLAKE3 wrappers, S3 geometry). Reuse is per-component, not wholesale.

- **`D:\Models\` asset state:** verified present as of 2026-04-29 via direct filesystem listing. Top-level directories: `hub/` (37 entries — 35 model dirs + 1 dataset dir + 1 .locks/), `UCD/` (full Unicode FTP mirror at `Public/UCD/latest/` with ucd/ + ucdxml/ + uca/ + emoji/ + idna/ + security/ + charts/), `ISO639/` (4 .tab files), `princeton-wordnet/WordNet-3.0/dict/`, `omw/wns/` (33+ language wordnets), `ud-treebanks/ud-treebanks-v2.17/` (339 treebank dirs), `wiktionary/raw-wiktextract-data.jsonl` (single file), `tatoeba/` (sentences.csv + links.csv + audio/), `Active/` (7 GGUF — SKIP), `qdrant/` (Qdrant DB instance — NOT substrate fuel), `xet/` (HF cache — NOT substrate fuel), `test_data/` (substrate test fixtures), `ArXiv/` (empty). Plus root-level files: `model_catalog.json`, `yolo11x.torchscript`, `tinyllama-*.gguf` (SKIP), HF auth tokens, helper Python scripts. Aggregate hub/ size approaches multiple TB based on individual safetensors shard sums but exact total not measured. See `50-reference/04-data-asset-paths.md` for the full verified inventory.

- **Active substrate state:** As of 2026-04-29, Anthony has rebuilt the docker image and is mid-seeding Fail_A's substrate (UCD/UCA + ISO 639 done, WordNet in progress). Success_C has no substrate state yet — it is a fresh implementation effort.

## Update protocol

When a milestone or component changes status:

1. Update its row in the table above.
2. Update the `Last verified` line at the top.
3. If a gate began passing, link the test result.
4. If a gate began failing after previously passing, document the regression in `60-status/03-known-issues.md`.

## Cross-references

- Roadmap: `40-process/04-implementation-roadmap.md`
- Validation gates: `40-process/02-validation-gates.md`
- Known issues tracker: `60-status/03-known-issues.md`
- Decisions log: `60-status/04-decisions-log.md`
