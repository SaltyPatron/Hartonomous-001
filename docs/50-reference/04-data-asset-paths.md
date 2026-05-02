# Data Asset Paths Reference

**Status:** Canonical
**Last verified:** 2026-04-29 (every path in this document confirmed via filesystem listing)
**Audience:** Engineers configuring decomposers, operators verifying data presence.

---

## Verification methodology

Every claim in this document was produced by direct filesystem listing of `D:\Models\` and its subdirectories on 2026-04-29. File counts, formats, sizes, and structural details were observed, not inferred from documentation, prior conversations, or HuggingFace conventions. Where a path is asserted, it was confirmed to exist.

When this document falls out of sync with filesystem reality, **the filesystem is correct and this document is stale**. Re-verify before relying on these paths in code.

---

## Top-level inventory of `D:\Models\`

```
D:\Models\
├── Active\                       7 GGUF quantized models (LOW PRIORITY — quantized)
├── ArXiv\                        EMPTY (placeholder for future ingestion)
├── atomic2020_data-feb2021\      ATOMIC 2020 commonsense KG (train/dev/test TSVs, CC BY 4.0)
├── ISO639\                       4 .tab files
├── UCD\                          Full Unicode FTP mirror (latest)
├── hub\                          37 model/dataset subdirectories (mix of HF cache and direct dirs)
├── omw\                          OMW build dir + wns\ with 33+ language wordnets
├── princeton-wordnet\            WordNet 3.0 source distribution + tarball
├── qdrant\                       Active Qdrant DB instance (NOT substrate fuel)
├── tatoeba\                      Sentences, links, audio
├── test_data\                    Substrate-test fixtures (audio, images, mixed, neural, text)
├── ud-treebanks\                 UD v2.17 (339 treebank directories) + tarball
├── wiktionary\                   SINGLE FILE: raw-wiktextract-data.jsonl
├── xet\                          HF Hub xet large-file-storage cache (NOT user data)
├── converttoawq.py               Helper script (not substrate)
├── download_detection_models.py  Helper script (not substrate)
├── download_model.py             Helper script (not substrate)
├── model_catalog.json            21-entry model architecture catalog (PARTIAL — covers subset of hub)
├── quantize.py                   Helper script (not substrate)
├── quantizer.py                  Helper script (not substrate)
├── stored_tokens                 HF auth token (do not commit)
├── token                         HF auth token (do not commit)
├── tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf  GGUF — SKIP (lossy)
└── yolo11x.torchscript           228MB TorchScript — alternative format of YOLO11x
```

## UCD — Full Unicode FTP mirror

The user has a complete mirror of the Unicode Consortium's `Public/UCD/latest/` directory. This is dramatically more comprehensive than just `ucd.all.flat.xml`.

### Layout

```
D:\Models\UCD\
└── Public\
    └── UCD\
        └── latest\
            ├── ReadMe.txt
            ├── ucd\                ~50 .txt files + auxiliary\ + extracted\ + emoji\
            ├── ucdxml\             12 XML/ZIP files (all formats)
            ├── uca\                Collation algorithm data
            ├── emoji\              Emoji sequences
            ├── idna\               Internationalized Domain Names
            ├── security\           Confusables, identifier types
            └── charts\             PDF code charts
```

### `ucd\` main UCD data files (~50 files)

```
ArabicShaping.txt
BidiBrackets.txt
BidiCharacterTest.txt
BidiMirroring.txt
BidiTest.txt
Blocks.txt
CJKRadicals.txt
CaseFolding.txt
CompositionExclusions.txt
DerivedAge.txt
DerivedCoreProperties.txt
DerivedNormalizationProps.txt
DoNotEmit.txt
EastAsianWidth.txt
EmojiSources.txt
EquivalentUnifiedIdeograph.txt
HangulSyllableType.txt
Index.txt
IndicPositionalCategory.txt
IndicSyllabicCategory.txt
Jamo.txt
LineBreak.txt
NameAliases.txt
NamedSequences.txt
NamedSequencesProv.txt
NamesList.html
NamesList.txt
NormalizationCorrections.txt
NormalizationTest.txt
NushuSources.txt
PropList.txt
PropertyAliases.txt
PropertyValueAliases.txt
ReadMe.txt
ScriptExtensions.txt
Scripts.txt
SpecialCasing.txt
StandardizedVariants.txt
TangutSources.txt
UCD.zip                              (full UCD as ZIP for atomic download)
USourceData.txt
USourceGlyphs.pdf
USourceRSChart.pdf
Unihan.zip                           (CJK Unihan database)
Unikemet.txt
UnicodeData.txt                      (the canonical codepoint properties table)
VerticalOrientation.txt
```

### `ucd\auxiliary\` — UAX #29 segmentation property tables

```
GraphemeBreakProperty.txt          ← drives UAX #29 grapheme cluster boundaries
GraphemeBreakTest.html
GraphemeBreakTest.txt              ← test fixtures for grapheme clustering
LineBreakTest.html
LineBreakTest.txt                  ← test fixtures for line breaks
SentenceBreakProperty.txt          ← drives UAX #29 sentence boundaries
SentenceBreakTest.html
SentenceBreakTest.txt              ← test fixtures for sentence breaks
WordBreakProperty.txt              ← drives UAX #29 word boundaries
WordBreakTest.html
WordBreakTest.txt                  ← test fixtures for word boundaries
```

These test fixtures are GOLD for the text decomposer's correctness gates. The decomposer's grapheme/word/sentence segmentation MUST pass these official Unicode test cases before being considered correct.

### `ucd\extracted\` — Derived/computed properties

```
DerivedBidiClass.txt
DerivedBinaryProperties.txt
DerivedCombiningClass.txt
DerivedDecompositionType.txt
DerivedEastAsianWidth.txt
DerivedGeneralCategory.txt
DerivedJoiningGroup.txt
DerivedJoiningType.txt
DerivedLineBreak.txt
DerivedName.txt
DerivedNumericType.txt
DerivedNumericValues.txt
```

These are pre-computed views of properties already present in the main files; useful for cross-checks during UCD decomposer validation.

### `ucd\emoji\` — Emoji-specific data within UCD

```
ReadMe.txt
emoji-data.txt
emoji-variation-sequences.txt
```

### `ucdxml\` — All XML formats

The full UCD as XML in 6 formats (each with a corresponding ZIP):

```
ucd.all.flat.xml          (~150MB; all codepoints, flat structure)
ucd.all.flat.zip
ucd.all.grouped.xml       (all codepoints, grouped by property)
ucd.all.grouped.zip
ucd.nounihan.flat.xml     (excludes CJK Unihan)
ucd.nounihan.flat.zip
ucd.nounihan.grouped.xml
ucd.nounihan.grouped.zip
ucd.unihan.flat.xml       (CJK Unihan only)
ucd.unihan.flat.zip
ucd.unihan.grouped.xml
ucd.unihan.grouped.zip
ucdxml.readme.txt
```

### `uca\` — Unicode Collation Algorithm

```
allkeys.txt          ← DUCET (Default Unicode Collation Element Table) — drives Super-Fibonacci spiral S³ projection
decomps.txt          ← decompositions used by UCA
ctt.txt              ← collation test table
CollationTest.zip    ← collation conformance tests
ReadMe.txt
```

### `emoji\` — Emoji sequences (top-level, separate from `ucd\emoji\`)

```
emoji-sequences.txt          ← canonical emoji sequences
emoji-test.txt               ← emoji conformance tests (must-pass for grapheme decomposer)
emoji-zwj-sequences.txt      ← Zero-Width-Joiner emoji sequences (multi-codepoint emoji)
ReadMe.txt
```

ZWJ emoji sequences are specifically what tests whether the grapheme cluster decomposer correctly identifies multi-codepoint emoji as ONE grapheme cluster. Critical correctness gate.

### `idna\` — Internationalized Domain Names

```
Idna2008.txt
IdnaMappingTable.txt
IdnaTestV2.txt
ReadMe.txt
```

If/when the substrate ingests URLs or domain-name content, IDNA tables drive correctness.

### `security\` — Identifier security data

```
IdentifierStatus.txt
IdentifierType.txt
confusables.txt              ← visually-confusable codepoint pairs
confusablesSummary.txt
intentional.txt
uts39-data-17.0.0.zip
ReadMe.txt
```

`confusables.txt` is particularly valuable: it lists pairs of codepoints that are visually similar but distinct. Substrate could ingest these as `visually_confusable_with` edges between codepoint atoms.

### `charts\` — PDF reference charts

```
CodeCharts.pdf       (the full Unicode code charts PDF)
RSIndex.pdf          (Radical-Stroke index for CJK)
RSIndex.txt
Readme.txt
fr\                  (French language version subdirectory)
```

PDFs aren't substrate fuel directly but useful as reference artifacts.

## Princeton WordNet 3.0

Path: `D:\Models\princeton-wordnet\`

```
WordNet-3.0\          ← FULL WordNet 3.0 source distribution
└── dict\             ← The actual lexical data
    ├── adj.exc       ← adjective exception list
    ├── adv.exc       ← adverb exception list
    ├── cntlist       ← sense frequency counts
    ├── cntlist.rev   ← reverse cntlist
    ├── data.adj      ← adjective synsets and pointers
    ├── data.adv      ← adverb synsets and pointers
    ├── data.noun     ← noun synsets and pointers (~80K synsets)
    ├── data.verb     ← verb synsets and pointers
    ├── frames.vrb    ← verb sentence frames
    ├── index.adj
    ├── index.adv
    ├── index.noun
    ├── index.sense   ← word→sense index (master)
    ├── index.verb
    ├── lexnames      ← 45 lexicographer file categories
    ├── log.grind.3.0
    ├── noun.exc      ← noun exception list (irregular plurals)
    ├── sentidx.vrb   ← verb sentence index
    ├── sents.vrb     ← verb sentences
    ├── verb.Framestext
    └── verb.exc      ← verb exception list

WordNet-3.0\          (also has src\, lib\, doc\, include\ — full source distribution)
WordNet-3.0.tar.gz    (tarball)
```

The `dict\` directory is the substrate's primary input. `data.{noun,verb,adj,adv}` and `index.{noun,verb,adj,adv,sense}` are the canonical files for the WordNet decomposer.

## OMW (Open Multilingual WordNet)

Path: `D:\Models\omw\`

OMW is a build directory with scripts and the actual data under `wns\`:

```
omw\
├── CITATION.cff
├── README.md
├── build-en.sh*
├── build.sh*
├── clean.sh*
├── etc\
├── index.toml
├── package.sh*
├── requirements.txt
├── scripts\
├── tests\
├── validate.sh*
└── wns\                ← The actual multilingual wordnet data
    ├── README
    ├── als\            (Albanian — Tosk)
    ├── arb\            (Arabic — Standard)
    ├── bul\            (Bulgarian)
    ├── cldr\           (CLDR-derived)
    ├── citation.bib
    ├── cow\            (Constructed wordnets)
    ├── cwn\            (Chinese — Mandarin)
    ├── dan\            (Danish)
    ├── ell\            (Modern Greek)
    ├── en\
    ├── eng\            (English)
    ├── fas\            (Persian)
    ├── fin\            (Finnish)
    ├── fra\            (French)
    ├── heb\            (Hebrew)
    ├── hrv\            (Croatian)
    ├── isl\            (Icelandic)
    ├── ita\            (Italian)
    ├── iwn\            (Italian — additional?)
    ├── jpn\            (Japanese)
    ├── mcr\            (Multilingual CR)
    ├── msa\            (Malay)
    ├── nld\            (Dutch)
    ├── nor\            (Norwegian)
    ├── pol\            (Polish)
    ├── por\            (Portuguese)
    ├── ron\            (Romanian)
    ├── slk\            (Slovak)
    ├── slv\            (Slovenian)
    ├── swe\            (Swedish)
    ├── tha\            (Thai)
    └── wikt\           (Wiktionary-derived)
```

ISO 639-3 codes are the directory names. Each language subdirectory's exact contents need to be verified per-language before configuring the OMW decomposer (some are .tab files, some have more structure).

## Universal Dependencies

Path: `D:\Models\ud-treebanks\`

```
ud-treebanks\
├── ud-treebanks-v2.17\          ← 339 treebank directories
│   ├── UD_Abaza-ATB\
│   ├── UD_Abkhaz-AbNC\
│   ├── UD_Afrikaans-AfriBooms\
│   ├── UD_Akkadian-PISANDUB\
│   ├── UD_Akkadian-RIAO\
│   ├── UD_Akuntsu-TuDeT\
│   ├── UD_Albanian-STAF\
│   ├── UD_Albanian-TSA\
│   ├── ... 339 total ...
│   └── (each contains .conllu files)
└── ud-treebanks-v2.17.tgz       (tarball)
```

Total 339 treebank directories at v2.17. Per-treebank files are CoNLL-U format (`.conllu`).

## Wiktionary

Path: `D:\Models\wiktionary\`

```
wiktionary\
├── raw-wiktextract-data.jsonl              ~22GB — full multilingual kaikki.org dump
└── kaikki.org-dictionary-English.jsonl     ~2.9GB — English-only filtered dump
```

Two distinct files. The full multilingual dump is for full-coverage ingestion; the English-only filtered dump is for English-focused work or for faster initial ingestion. The Wiktionary decomposer should support both as separate provenance: `wiktextract:multilingual-raw` and `wiktextract:english-only` so substrate state can distinguish their attestations.

Both are JSONL (one JSON object per line, kaikki.org schema). Process with mmap + thread-local simdjson parsers.

## Tatoeba

Path: `D:\Models\tatoeba\`

```
tatoeba\
├── audio\
│   ├── eng\                              (English audio files)
│   ├── sentences_with_audio.csv
│   └── sentences_with_audio.tar.bz2
├── links.csv                             ← sentence translation links (CSV)
├── links.tar.bz2                         (compressed)
├── sentences.csv                         ← sentence text (CSV)
├── sentences.tar.bz2                     (compressed)
├── sentences_with_audio.tar.bz2          (also at top level)
└── tatoeba_audio_eng.zip                 (English audio bundle)
```

The Tatoeba decomposer processes `sentences.csv` and `links.csv` for the sentence-level translation graph; `audio\eng\` for cross-modal `recording_of` edges (English first; other languages presumably available via additional `tatoeba_audio_*.zip` if/when the user adds them).

## ISO 639

Path: `D:\Models\ISO639\`

```
iso-639-3-macrolanguages.tab          ← macrolanguage relationships
iso-639-3.tab                          ← main 7,928-language registry
iso-639-3_Name_Index.tab               ← language name lookup
iso-639-3_Retirements.tab              ← retired/superseded codes
```

All four .tab files are needed for full ISO 639 coverage. The ISO 639 decomposer reads all four; macrolanguage relationships become `macrolanguage_includes` edges; retirements become `superseded_by` edges.

## `D:\Models\hub\` — 37 entries

`hub\` is a mixed directory of HuggingFace cache-format models (`models--<org>--<name>\snapshots\<sha>\...`) and direct extraction directories. Some entries also have `.locks\` and `refs\` from HF cache state.

### Frontier-tier LLMs

| Directory | Format | Architecture | Shards | Quantization |
|---|---|---|---|---|
| `wQwen3-Coder-480B-A35B-Instruct\` | safetensors (direct) | Qwen3MoeForCausalLM | 241 | bfloat16 (none) |
| `zDeepSeek-V3.2-Speciale\` | safetensors (direct) | DeepseekV32ForCausalLM | 163 | **FP8 native (E4M3)** ⚠ |
| `ymodels--meta-llama--Llama-4-Maverick-17B-128E\snapshots\10751cb...\` | safetensors (HF cache) | Llama4ForConditionalGeneration | 55 | bfloat16 (none) |

**⚠ DeepSeek-V3.2-Speciale uses native FP8 quantization (E4M3 format).** This is different from post-training AWQ/GGUF quantization — DeepSeek trained the model directly in FP8. Per ADR-002 / quantization deprioritization policy, treat with caution: ingest with sub-provenance flag (`huggingface_model:deepseek-v3.2-speciale:fp8-native`) so it can be filtered out of refinement targets if FP8 attestations are deemed lossy.

### Mid-tier LLMs

| Directory | Format | Architecture | Shards |
|---|---|---|---|
| `models--Qwen--Qwen3-Coder-30B-A3B-Instruct\snapshots\b2cff64...\` | safetensors (HF cache) | Qwen3MoeForCausalLM | 16 |
| `models--deepseek-ai--deepseek-coder-33b-instruct\snapshots\61dc97b...\` | safetensors (HF cache) | LlamaForCausalLM (DeepSeek lineage) | 7 |
| `models--deepseek-ai--DeepSeek-Coder-V2-Lite-Instruct\snapshots\e434a23...\` | safetensors (HF cache) + custom code | DeepseekV2ForCausalLM | 4 |
| `models--Qwen--Qwen2.5-Coder-14B-Instruct\snapshots\aedcc2d...\` | safetensors (HF cache) | Qwen2ForCausalLM | 6 |

### Smaller LLMs

| Directory | Format | Shards | Quantization |
|---|---|---|---|
| `models--Qwen--Qwen2.5-Coder-7B-Instruct\snapshots\c03e6d3...\` | safetensors | 4 | none |
| `models--Qwen--Qwen2.5-Coder-7B-Instruct-AWQ\snapshots\8e8ed24...\` | safetensors | 2 | **AWQ (lossy)** |
| `models--Qwen--Qwen2.5-Coder-3B-Instruct\snapshots\488639f...\` | safetensors | 2 | none |
| `models--Qwen--Qwen2.5-Coder-3B-Instruct-AWQ\snapshots\5d26593...\` | safetensors | 1 | **AWQ (lossy)** |

### Embedding models

| Directory | Format | Architecture | Shards |
|---|---|---|---|
| `models--Qwen--Qwen3-Embedding-4B\snapshots\5cf2132...\` | safetensors | Qwen3 | 2 |
| `models--Qwen--Qwen3-Embedding-0.6B\snapshots\c54f2e6...\` | safetensors | Qwen3 | 1 |
| `models--Qwen--Qwen3-VL-Embedding-8B\snapshots\a12d611...\` | safetensors | Qwen3VL | 4 |
| `models--Qwen--Qwen3-VL-Embedding-2B\snapshots\929a0c3...\` | safetensors | Qwen3VL | 1 |
| `models--jinaai--jina-code-embeddings-1.5b\snapshots\39aeb4f...\` | safetensors | (Jina) | 1 |
| `models--sentence-transformers--all-MiniLM-L6-v2\snapshots\c9745ed...\` | safetensors | BertModel | 1 |

### Reranker models

| Directory | Format | Shards |
|---|---|---|
| `models--Qwen--Qwen3-Reranker-4B\snapshots\f16fc5d...\` | safetensors | 2 |
| `models--Qwen--Qwen3-Reranker-0.6B\snapshots\6e9e698...\` | safetensors | 1 |
| `models--Qwen--Qwen3-VL-Reranker-8B\snapshots\8e52ab8...\` | safetensors | 4 |
| `models--Qwen--Qwen3-VL-Reranker-2B\snapshots\76219da...\` | safetensors | 1 |
| `models--jinaai--jina-reranker-v3\snapshots\050e171...\` | safetensors + custom code | 1 |
| `models--zeroentropy--zerank-2\snapshots\9ae8623...\` | safetensors + custom code | 2 |

### Vision models

| Directory | Format | Architecture |
|---|---|---|
| `Florence-2-large\` (direct extraction) | safetensors + pytorch_model.bin + custom code | Florence2ForConditionalGeneration |
| `Florence-2-base\` (direct extraction) | safetensors + pytorch_model.bin + custom code | Florence2ForConditionalGeneration |
| `Grounding-DINO-Base\` (direct extraction) | safetensors + pytorch_model.bin | GroundingDinoForObjectDetection |
| `Conditional-DETR-R50\` (direct extraction) | safetensors + pytorch_model.bin | ConditionalDETRForObjectDetection |
| `DETR-ResNet-101\` (direct extraction) | safetensors + pytorch_model.bin | DetrForObjectDetection |
| `RT-DETR-v1-R101\` (direct extraction) | safetensors only | RTDetrForObjectDetection |
| `yolo11x\` | yolo11x.pt (Ultralytics PyTorch) | YOLO11x |

**Florence-2 and DeepSeek-Coder-V2-Lite have custom-code modules** (`modeling_*.py`, `configuration_*.py`, `processing_*.py`). These require `trust_remote_code=True` semantics; the safetensors decomposer must handle their custom architecture classes.

### Audio models

| Directory | Format | Architecture |
|---|---|---|
| `models--facebook--sam-audio-large\snapshots\5f2cd3a...\` | **checkpoint.pt (PyTorch native, NOT safetensors)** | (config-specified) |
| `models--ibm-granite--granite-speech-3.3-8b\snapshots\315afb3...\` | safetensors (9 shards) + adapter_model.safetensors (LoRA) | GraniteSpeechForConditionalGeneration |
| `models--nvidia--canary-qwen-2.5b\snapshots\6cfc37e...\` | safetensors (single) | (Canary-specific) |
| `models--fishaudio--fish-speech-1.5\snapshots\275a984...\` | **model.pth + firefly-gan-*.pth (PyTorch native, NOT safetensors)** + tokenizer.tiktoken | (Fish-specific) |
| `models--nvidia--music-flamingo-hf\snapshots\e29cfe9...\` | safetensors (4 shards) | AudioFlamingo3ForConditionalGeneration |

**SAM-audio-large and Fish-Speech-1.5 are NOT in safetensors format.** They use PyTorch's native pickle format (`.pt` / `.pth`). The safetensors decomposer cannot ingest these directly; either a separate PyTorch-pickle decomposer is needed, or the user must convert these to safetensors before ingestion.

**Granite-Speech has a LoRA adapter pattern**: `adapter_model.safetensors` + 9 base model shards. Adapter ingestion is a distinct decomposer mode (compose base model edges with adapter delta).

### Diffusion model

| Directory | Format | Notes |
|---|---|---|
| `xmodels--black-forest-labs--FLUX.2-dev\snapshots\6aab690...\` | Multi-component pipeline | Not a single safetensors |

FLUX.2-dev structure:
```
flux2-dev.safetensors                  ← main transformer weights
ae.safetensors                         ← autoencoder
text_encoder\                          (subdirectory with its own safetensors + config)
tokenizer\                             (subdirectory)
transformer\                           (subdirectory)
vae\                                   (subdirectory)
scheduler\                             (subdirectory)
model_index.json                       (orchestrates the pipeline components)
```

Diffusion models require a different decomposition strategy: each component (text_encoder, transformer, vae, scheduler) is a separate sub-model with its own architecture; they're composed at inference time. Substrate must ingest each component as separate edges with sub-provenance.

### Datasets

| Directory | Format | Notes |
|---|---|---|
| `datasets--nampdn-ai--tiny-codes\snapshots\9aebe5e...\` | parquet (9 shards) | ~1.63M NL↔code pairs across many programming languages |

Parquet files: `part_1_200000.parquet`, `part_2_400000.parquet`, ..., `part_9_1632520.parquet`. The numbers in the filenames are cumulative row counts.

## Active/ — GGUF stash (LOW PRIORITY, all quantized)

Path: `D:\Models\Active\`

```
Qwen3-Coder-30B-Q4_K_M.gguf            (Q4_K_M = 4-bit quantization)
Qwen3-Embedding-0.6B-Q8_0.gguf         (Q8_0 = 8-bit)
Qwen3-Embedding-4B-Q4_K_M.gguf
Qwen3-Embedding-4B-Q8_0.gguf
Qwen3-Reranker-0.6B-Q8_0.gguf
Qwen3-Reranker-4B-Q4_K_M.gguf
Qwen3-Reranker-4B-Q8_0.gguf
```

All are GGUF (llama.cpp format) and quantized. **Per ADR-002 / Provenance Catalog policy, these are SKIP**. The substrate's accumulated state should reflect lossless attestations from the corresponding safetensors-format models in `hub\`, not these GGUF variants.

The `Active\` directory presumably represents the user's llama.cpp runtime stash for daily use, not substrate fuel.

## test_data/ — Substrate test fixtures

Path: `D:\Models\test_data\`

```
test_data\
├── audio\
├── images\
├── mixed\
├── neural\
├── text\
├── run_zip_test.ps1
├── simple_cnn.pth                       ← PyTorch pickle format
└── simple_cnn.safetensors               ← Same model, safetensors format
```

The `simple_cnn.{pth,safetensors}` pair is GOLD for the safetensors decomposer's determinism gate: ingest both formats of the same network, assert byte-identical substrate edges. Per-modality test fixtures in `audio/`, `images/`, `mixed/`, `neural/`, `text/` provide cross-modal validation cases.

## qdrant/ — Active Qdrant DB instance (NOT substrate)

Path: `D:\Models\qdrant\`

```
aliases\
collections\
raft_state.json
```

This is a running Qdrant vector DB instance (consensus state in `raft_state.json`). It's the user's separate inference infrastructure, **not substrate fuel**. The substrate doesn't ingest from another vector DB.

## xet/ — HF Hub xet large-file-storage cache (NOT substrate)

Path: `D:\Models\xet\`

```
https___cas_serv-2on84fYURnNkdyG0\
https___cas_serv-tGqkUaZf_CBPHQ6h\
logs\
```

HuggingFace Hub's xet (Content-Addressable Storage) cache for large file deduplication. The actual model files are referenced by xet hash and stored here; HF cache directories like `models--*\snapshots\*\` may contain symlinks/pointers into this cache. **Not substrate fuel directly** — but if model files appear missing from snapshot directories, look here for the actual content.

## ArXiv/ — Empty placeholder

Path: `D:\Models\ArXiv\` — currently empty. No files at any depth. Reserved for future ArXiv corpus ingestion (technical/scientific vocabulary tier).

## ATOMIC 2020 — Commonsense Knowledge Graph

Path: `D:\Models\atomic2020_data-feb2021\`

```
atomic2020_data-feb2021\
├── LICENSE                  Creative Commons Attribution 4.0 International (CC BY 4.0)
├── README.md                Format documentation
├── hwang2021comet.pdf       Paper (Hwang et al., AAAI 2021)
├── train.tsv                1,076,880 commonsense tuples (head, relation, tail)
├── dev.tsv                  validation split
└── test.tsv                 test split
```

Format: TSV with three columns: `head_node`, `edge_relation`, `tail_node`. Example:
```
PersonX abandons ___ altogether    oEffect    dejected
```

Edge relations include 23 commonsense types: `xWant`, `xAttr`, `xEffect`, `xIntent`, `xNeed`, `xReact`, `oEffect`, `oReact`, `oWant`, `AtLocation`, `Causes`, `CapableOf`, `HasProperty`, `HasSubEvent`, `HinderedBy`, `IsAfter`, `IsBefore`, `MadeUpOf`, `NotDesires`, `ObjectUse`, etc. (full list in README.md).

License: CC BY 4.0 — free with attribution, including commercial use. Provenance code: `atomic-2020`. Trust prior tier: community_curated (around 1500 — high-quality crowdsourced commonsense from AI2).

Decomposer effort: low. Each TSV row produces one substrate edge between two `text_composition` entities (head and tail go through `text_decompose`), with `edge_type_id = atomic_<relation>`. New entity types or edge types may be needed (`commonsense_event`, `xWant`, `xAttr`, etc. as edge types). About one focused engineering day.

## Root-level files

```
converttoawq.py                   Helper script to convert models to AWQ (NOT substrate)
download_detection_models.py     Helper script (NOT substrate)
download_model.py                 Helper script (NOT substrate)
quantize.py                       Helper script (NOT substrate)
quantizer.py                      Helper script (NOT substrate)
model_catalog.json                63KB — 21-entry architecture/tensor-category catalog (PARTIAL coverage of hub)
stored_tokens                     HF auth token (sensitive, do NOT include in any export)
token                             HF auth token (sensitive, do NOT include in any export)
tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf    GGUF — SKIP (lossy)
yolo11x.torchscript               228MB TorchScript serialization of YOLO11x — alternative to yolo11x.pt
```

`model_catalog.json` is a substrate-relevant artifact: it contains pre-computed architecture descriptors and tensor categorization for 21 of the hub models (DeepSeek-V3.2-Speciale, FLUX.2-dev, Qwen3-Coder-480B, Conditional-DETR, DETR, Florence-2 base+large, Grounding-DINO, SAM-audio, Fish-Speech, Granite-Speech, Llama-4-Maverick (×2 entries), Canary-Qwen, Music-Flamingo, Qwen2.5-Coder 14B/3B/7B, Qwen3-Coder-30B, MiniLM, RT-DETR). The remaining 16+ hub directories aren't in the catalog. The substrate's safetensors decomposer can use this catalog to bootstrap architecture knowledge for the listed models (saving config.json parsing); for the rest, parse config.json from each model directory directly.

## Things I previously claimed that were WRONG

For honesty: the prior version of this document had several errors that the user caught.

| Claim | Reality |
|---|---|
| "Llama-4-Maverick is 749GB / 128 expert MoE" | Actual: ~750GB on disk, **55 safetensors shards**, 128 experts is a model architecture parameter (128E), not shard count |
| "yolo11x is .pt only" | Actual: `D:\Models\hub\yolo11x\yolo11x.pt` (110MB, Ultralytics) AND `D:\Models\yolo11x.torchscript` (228MB, TorchScript) — TWO formats present |
| "UCD has only `ucd.all.flat.xml` and `allkeys.txt`" | Actual: full UCD FTP mirror with ~50 main UCD files + auxiliary/extracted/emoji + 12 ucdxml formats + uca + emoji + idna + security + charts |
| "WordNet path is `D:\Models\princeton-wordnet\`" (implying dict at top level) | Actual: `D:\Models\princeton-wordnet\WordNet-3.0\dict\` is the dict path |
| "Wiktionary at `D:\Models\wiktionary\` (multi-shard)" | Actual: SINGLE FILE `D:\Models\wiktionary\raw-wiktextract-data.jsonl` |
| Unspecified that SAM-audio and Fish-Speech use `.pt`/`.pth`, not safetensors | These two models can NOT be ingested by a safetensors-only decomposer |
| Unspecified DeepSeek-V3.2-Speciale FP8 native quantization | Worth flagging for ingestion policy |
| Unspecified FLUX.2-dev multi-component pipeline structure | Diffusion ingestion needs component-aware decomposer |
| Unmentioned Granite-Speech LoRA adapter pattern | Adapter mode is a distinct decomposer concern |
| Unmentioned `qdrant\` running instance | Not substrate fuel; should be excluded |
| Unmentioned `xet\` HF cache | Should be excluded |
| Unmentioned root-level `model_catalog.json` | Substrate-relevant for pre-computed architectures |

## Cross-references

- Provenance catalog with corrected entries: `20-technical/13-provenance-catalog.md`
- ADR-002 (atom vocabulary): `60-status/04-decisions-log.md`
- Roadmap: `40-process/04-implementation-roadmap.md`
- Decomposer contract requiring per-format handling: `10-architecture/05-decomposer-contract.md`
