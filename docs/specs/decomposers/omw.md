# OMW Decomposer Specification

## Identity

- **Decomposer class**: `OmwDecomposer` extends `BaseDecomposer`
- **Source path**: `external/omw` (git submodule from `https://github.com/omwn/omw-data.git`)
- **Trust prior**: High (academic consortium, per-language curators)
- **Provenance**: `omwn/omw-data` with per-language sub-provenance (e.g., `omwn/omw-data/jpn`, `omwn/omw-data/fra`)
- **Dependency**: Phase 2c prerequisite -- WordNet synsets must exist (OMW aligns TO Princeton WordNet synsets). ISO 639 language entities must exist for language tagging.

## What This Decomposer Creates

Cross-lingual synset alignments. For each language in OMW, maps words in that language to Princeton WordNet synset IDs via the Collaborative Interlingual Index (ILI). This gives the substrate language-agnostic semantic identity: the concept "dog" exists across Japanese (犬), French (chien), German (Hund), etc., all linked to the same synset.

## Source Format

OMW organizes data by language/project in subdirectories under `wns/`.

### Directory Structure (confirmed from git status)

```
wns/
  README
  citation.bib
  als/     -- Albanian (Tosk)
  arb/     -- Arabic
  bul/     -- Bulgarian
  cldr/    -- CLDR translations (multi-language, 150+ lang files)
  cow/     -- Chinese Open Wordnet (Mandarin)
  cwn/     -- Chinese Wordnet (Mandarin, qcn)
  dan/     -- Danish
  ell/     -- Greek
  en/      -- English WordNet documentation/metadata
  eng/     -- English (OMW version)
  fas/     -- Persian
  fin/     -- Finnish
  fra/     -- French
  heb/     -- Hebrew
  hrv/     -- Croatian
  isl/     -- Icelandic
  ita/     -- Italian
  iwn/     -- Italian WordNet (separate from ita)
  jpn/     -- Japanese
  mcr/     -- Multilingual Central Repository (cat, eus, glg, spa)
  msa/     -- Malay (ind, zsm)
  nld/     -- Dutch
  nor/     -- Norwegian (nno, nob)
  pol/     -- Polish
  por/     -- Portuguese
  ron/     -- Romanian
  slk/     -- Slovak (also lit for Lithuanian)
  slv/     -- Slovenian
  swe/     -- Swedish
  tha/     -- Thai
  wikt/    -- Wiktionary-derived (thousands of language files)
```

### Tab File Format

Each language directory contains `wn-data-{lang}.tab` files. Format:

```
synset_id<tab>relation<tab>word
```

Where:
- `synset_id` = Princeton WordNet offset + POS (e.g., `00001740-n`)
- `relation` = typically `lemma` or other relation types
- `word` = the word in that language

Some directories also contain:
- `{lang}-changes.tab` -- change history
- `{lang}2tab.py` -- conversion script (documents source format)
- `citation.bib` -- academic citation
- `LICENSE` -- per-language license terms
- `README` -- per-language documentation
- `log` -- conversion log

### CLDR Subdirectory

`wns/cldr/` contains 150+ files of format `wn-cldr-{lang}.tab` derived from Unicode CLDR translations. These are community-quality translations with lower precision than curated wordnets but massive breadth.

### Wiktionary Subdirectory

`wns/wikt/` contains thousands of files `wn-wikt-{lang}.tab` with Wiktionary-derived synset alignments. Even broader coverage but lowest precision among OMW sources.

### Core ILI

`etc/wn-core-ili.tab` -- the core Interlingual Index mapping that defines which synsets are considered "core concepts" shared across languages.

### Build Infrastructure

- `index.toml` -- project index listing all included wordnets
- `build.sh`, `build-en.sh`, `clean.sh`, `package.sh` -- build scripts
- `scripts/` -- Python conversion tools (tsv2lmf.py, wndb2lmf.py, build.py, etc.)
- `requirements.txt` -- Python dependencies
- `tests/` -- test suite

## Entity Model

Lemma entities are created in the entity table. Cross-lingual alignments to existing WordNet synsets are edges. Language assignments populate the `entity_language` junction table.

```
-- Entity table row:
entity: hash=BLAKE3('犬'), entity_type_id→entity_type('lemma')

-- Sequence (composition structure):
sequence: parent_id='犬', children=[U+72AC]  -- single CJK codepoint, already exists from UCD

-- Junction table entries:
entity_language: entity_id='犬', language_id→language('jpn')

-- Edges:
edge(type='aligned_to_synset', source=Entity('犬'), target=synset_02084071-n, provenance='omwn/omw-data/jpn')
edge(type='aligned_to_synset', source=Entity('chien'), target=synset_02084071-n, provenance='omwn/omw-data/fra')
```

The synset entity is NOT duplicated. It was created by the WordNet decomposer. OMW only adds lemma entities in other languages and alignment edges from those lemmas to the existing synsets.

## Trust Prior Differentiation

Not all OMW sources are equal:

| Source | Trust Level | Rationale |
|--------|------------|-----------|
| Curated wordnets (jpn, fra, fin, etc.) | High | Academic linguists curated |
| MCR (cat, eus, glg, spa) | High | Multilingual Central Repository, academic |
| CLDR translations | Medium | Community-maintained, standardized |
| Wiktionary-derived (wns/wikt/*) | Low-Medium | Automated extraction from community wiki |

Each source's provenance entity carries its trust prior, and significance ratings for alignment relations reflect this.

## Physicality

- Lemma entities: LINESTRINGZM trajectory from constituent codepoint S3 positions + centroid.
- Alignment edge geometries: LINESTRINGZM from lemma centroid to synset centroid.

## Analysis Passes

- `AlignmentConsistencyPass` -- verify that aligned words actually relate to the synset's semantic content (flag suspicious mappings)
- `CrossLingualCoveragePass` -- compute coverage statistics per language (which synsets are covered, which aren't)
- `CoreILICoveragePass` -- verify coverage of core ILI concepts per language

## Completeness Criteria

- Every `.tab` file in every language directory is processed.
- Every curated wordnet, CLDR translation, and Wiktionary-derived alignment is ingested.
- Each alignment is an edge from a language-specific lemma to an existing WordNet synset.
- No synset entities are duplicated (only edges added to existing ones).
- Per-source trust priors are set according to curation quality.
- License/citation data from each language directory is recorded in provenance.
- All language tags reference existing `language` reference table rows via `entity_language` junction.
- Core ILI concept coverage is tracked.
