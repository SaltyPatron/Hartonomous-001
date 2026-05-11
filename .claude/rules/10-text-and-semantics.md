---
description: Text and semantic decomposition rules for Hartonomous.
paths:
  - src/Hartonomous.Core/**
  - src/Hartonomous.Engine/**
  - src/Hartonomous.Decomposers/**
  - tests/**
  - docs/specs/decomposers/**
  - docs/specs/modalities/text.md
  - docs/specs/engine/**
---

## Text decomposition levels

The text stack has distinct, non-collapsible levels. Each maps to specific entity types in `substrate.entity_type` seeded by `sql/schema/seed/entity_type.sql`:

| Level | Entity type code | Description |
|-------|-----------------|-------------|
| 0 | `codepoint` | Unicode codepoint atoms. S3 position from UCA Fibonacci projection. Seeded by UCD decomposer. |
| 1 | `grapheme_cluster` | UAX #29 grapheme clusters (compositions of codepoints). |
| 2 | `word_form` | Attested surface forms. Created by WordNet, UD, OMW, Wiktionary, and Tatoeba decomposers. |
| 3 | `morpheme`, `lemma` | Morphological decomposition. Lemmas from UD, WordNet, Wiktionary. |
| 4 | `text_composition`, `paragraph`, `document` | Canonical text compositions emitted by the core text decomposer. |
| 5 | `synset` | WordNet semantic units. Connected via `has_sense`, `aligned_to_synset`, and semantic relation edges. |

UAX #29 (`break_property` in `codepoint_property` junction/reference infrastructure) is the boundary-detection layer only. Do not reduce Hartonomous text handling to segmentation alone. It does NOT perform morphological analysis, syntactic parsing, or semantic disambiguation.

## Lexicalized wholes versus compositional decomposition

Whole lexicalized forms and their decompositions can both coexist in the substrate. `highrise` as an attested whole form and `high` + `rise` as compositional parts are BOTH valid entities. The whole form's meaning is not reducible to the sum of its parts — see semantic regression case #2 in `.claude/skills/hartonomous-semantic-eval/cases.md`.

## One form, many senses

A single word-form entity (e.g., `minute`, `overload`) has ONE identity hash for the surface content. POS assignments, semantic synset edges, language evidence, and pronunciation variants are separate edges and junction entries, not separate entities. `entity_pos` carries Glicko-2 classification confidence; relation trust lives on `edge_significance`. See regression cases #1, #3.

## POS, sense, and language are infrastructure

POS, sense, language, and morph-feature assignments are infrastructure lookup surfaces with significance-bearing junction tables. They are not edge members in the entity substrate.

- Reference tables: `pos`, `deprel`, `morph_feature`, `sense`, `language`.
- Junction tables: `entity_pos`, `entity_language`, `entity_morph_feature`, plus typed substrate edges such as `has_sense` where relation content is attested.
- Fast indexed lookups ("Is 'rake' a noun?" = one JOIN against `entity_pos`). Not graph traversal.

## Terse lexical examples

Treat terse user examples (`overload`, `highrise`, `minute`, `king : queen :: man : woman`) as live semantic regression probes. Answer the substrate behavior path directly before summarizing architecture or retreating into documentation inventory mode.

## Decomposer contracts

All text decomposers extend `BaseDecomposer` (`src/Hartonomous.Core/Decomposition/BaseDecomposer.cs`):
- `ProvenanceCode`: maps to `substrate.provenance.code` for trust priors
- `Phases`: which `Phase` enum values this decomposer runs in (see `Phase.cs`: `UcdUca`, `Iso639`, `WordNetOmw`, `UniversalDeps`, `Wiktionary`, `Tatoeba`)
- `DecomposeCoreAsync()`: deterministic ingestion — records all candidates without disambiguation
- Content hashing only: `ComputeHash()`, `ComputeMerkleHash()`, `ComputeEdgeHash()`

Trust prior hierarchy (ingestion-time `mu` values):
- WordNet: 95,000 (`WordNetDecomposer.TrustPriorMu`)
- UD: 92,000 (`UdDecomposer.TrustPriorMu`)
- OMW curated: 90,000 / CLDR: 70,000 / Wikt: 50,000
- Wiktionary: 68,000 (`WiktionaryDecomposer.TrustPriorMu`)
- Tatoeba: 50,000 (`TatoebaDecomposer.TrustPriorMu`)

## Ingestion records, inference decides

Decomposers at ingestion time record ALL candidate senses, syntactic structures, and evidence edges without disambiguation (Law #8). Sense selection, role assignment, and meaning resolution happen at inference time via significance-weighted edge traversal in `Hartonomous.Engine`.
