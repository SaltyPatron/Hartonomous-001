# Decomposers — Implementation Guide

**Status**: ✅ Complete

Implementation-level guidance for each decomposer. Domain specs in [specs/decomposers/](../decomposers/) define WHAT data comes out. This spec defines HOW the C# code works.

---

## Common Patterns

### Source File Discovery

Every decomposer receives its source directory via `DecomposerConfig.SourceDirectory`. The decomposer enumerates files within that directory using known filename patterns:

```csharp
// WordNet example:
protected override IReadOnlyList<string> GetSourcePaths()
    => Directory.GetFiles(_config.SourceDirectory, "data.*")
        .Concat(Directory.GetFiles(_config.SourceDirectory, "index.*"))
        .ToList();
```

No magic discovery. No recursive search (unless the format requires it, e.g., UD treebanks in subdirectories). Each decomposer knows its source format.

### Parsing

All parsers are hand-written in `Hartonomous.Decomposers/Parsers/`. No third-party parsing libraries. Each parser reads from a `Stream` or `TextReader` and yields parsed entries:

```csharp
// Generic pattern for line-oriented formats:
internal static async IAsyncEnumerable<ParsedEntry> ParseAsync(
    string filePath,
    [EnumeratorCancellation] CancellationToken ct)
{
    await using var reader = new StreamReader(filePath, Encoding.UTF8);
    while (await reader.ReadLineAsync(ct) is { } line)
    {
        if (line.Length == 0 || line[0] == '#') continue;
        yield return ParseLine(line);
    }
}
```

### Idempotency

All decomposers are idempotent by design:
- Entities are deduplicated on BLAKE3 hash (`ON CONFLICT DO NOTHING`).
- Edges are deduplicated on edge hash.
- Junction entries are deduplicated on `(entity_id, reference_id)`.
- Re-running a decomposer on the same data produces zero new rows — all operations resolve to existing entries.

### Hash Contracts

| Entity Type | Hash Input | Hash Method |
|-------------|-----------|-------------|
| Codepoint | Codepoint integer value (4 bytes, big-endian) | `ComputeHash(bytes)` |
| Grapheme cluster | Ordered codepoint hashes (Merkle) | `ComputeMerkleHash(childHashes)` |
| Word form | Ordered grapheme cluster hashes (Merkle) | `ComputeMerkleHash(childHashes)` |
| Lemma | Lowercase canonical form (UTF-8 bytes) | `ComputeHash(string)` |
| Synset | WordNet synset offset + POS tag (e.g., `"02084071-n"`) | `ComputeHash(string)` |
| Word sense | Synset hash + lemma hash (Merkle) | `ComputeMerkleHash([synsetHash, lemmaHash])` |
| UD sentence | Ordered token hashes (Merkle) | `ComputeMerkleHash(tokenHashes)` |
| UD token | Token form string (UTF-8 bytes) — same as word_form | `ComputeHash(string)` |
| Tatoeba sentence | Sentence text (UTF-8 bytes) | `ComputeHash(string)` |
| Tensor | Canonical content prefix (kind="tens", dtype, rank, shape) + raw tensor bytes streamed via Blake3 hasher (NOT layer path or model name — placement metadata lives on edges, not in the hash) | `Blake3Hasher.Stream(prefix + bytes)` per `ModelPassOrchestrator.HashTensorStreaming` |
| Model architecture | Model name (e.g., `"Qwen/Qwen2.5-Coder-32B-Instruct"`) | `ComputeHash(string)` |
| Language name | ISO 639-3 code (e.g., `"eng"`) | `ComputeHash(string)` |
| Audio recording | File content hash (full file bytes) | `ComputeHash(fileBytes)` |
| Audio chunk | Parent hash + time offset (Merkle with timestamp bytes) | `ComputeMerkleHash(...)` |
| Text document | Ordered sentence hashes (Merkle) | `ComputeMerkleHash(sentenceHashes)` |
| Image | Pixel grid hash (Merkle of row hashes) | `ComputeMerkleHash(rowHashes)` |
| Video frame | Image hash + timestamp bytes (Merkle) | `ComputeMerkleHash([imageHash, timestampBytes])` |
| Video | Ordered frame hashes (Merkle) | `ComputeMerkleHash(frameHashes)` |

---

## Per-Decomposer Implementation

### WordNet (Princeton WordNet 3.1)

**Source**: `D:\Models\princeton-wordnet`

**Files**: `data.noun`, `data.verb`, `data.adj`, `data.adv`, `index.noun`, `index.verb`, `index.adj`, `index.adv`, `index.sense`, `adj.exc`, `noun.exc`, `verb.exc`, `adv.exc` (29 files total).

**Parser**: `WordNetDbParser` — reads the WordNet database file format (fixed-width header + variable-length record per line). The format is documented in `wn(5)` man page.

**Phase**: `WordNetOmw` (Phase 2c).

**Decomposition sequence**:
1. Parse `data.*` files → create `synset` entities (one per synset offset+POS).
2. Parse word entries within each synset → create `lemma` entities.
3. Parse `index.sense` → create `word_sense` entities (lemma + synset pairing).
4. Parse pointer records within `data.*` → create semantic relation edges (`hypernym`, `hyponym`, `antonym`, `meronym_part`, `meronym_substance`, `meronym_member`, `holonym_part`, `holonym_substance`, `holonym_member`, `similar_to`, `entailment`, `cause`, `also_see`, `domain_topic`, `domain_region`, `domain_usage`).
5. Populate junctions: `entity_sense` (entity → sense with mu), `entity_pos` (lemma → POS).
6. Parse `*.exc` → create `inflected_form` entities + `has_form` edges.

**Edge type mapping**:

| WordNet Pointer | Edge Type Code |
|-----------------|---------------|
| `@` (hypernym) | `hypernym` |
| `~` (hyponym) | `hyponym` |
| `!` (antonym) | `antonym` |
| `#p` (part meronym) | `meronym_part` |
| `#s` (substance meronym) | `meronym_substance` |
| `#m` (member meronym) | `meronym_member` |
| `%p` (part holonym) | `holonym_part` |
| `&` (similar to) | `similar_to` |
| `*` (entailment) | `entailment` |
| `>` (cause) | `cause` |
| `^` (also see) | `also_see` |
| `;c` (domain topic) | `domain_topic` |

**Volume**: ~117,659 synsets, ~155,327 lemmas, ~206,941 word senses, ~370,000 semantic edges.

---

### Open Multilingual Wordnet (OMW)

**Source**: `external/omw`

**Files**: TSV files per language, extracted from OMW distribution.

**Parser**: `TsvParser` — generic tab-separated value parser.

**Phase**: `WordNetOmw` (Phase 2c, after WordNet).

**Dependency**: WordNet must be ingested first. OMW creates cross-lingual edges FROM new language-specific lemma entities TO existing WordNet synsets.

**Decomposition sequence**:
1. For each language file: parse tab-separated entries.
2. Create `lemma` entities for each non-English lexical entry.
3. Resolve target synset by WordNet offset+POS (must already exist).
4. Create `aligned_to_synset` edges (lemma → synset).
5. Create `translation_link` edges between language-specific lemmas and English lemmas sharing the same synset.
6. Populate junctions: `entity_language` (lemma → language).

---

### Universal Dependencies (UD)

**Source**: `D:\Models\ud-treebanks`

**Files**: `*.conllu` files in per-treebank subdirectories.

**Parser**: `ConllUParser` — CoNLL-U format parser. Handles multi-word tokens, empty nodes, enhanced dependencies.

**Phase**: `UniversalDeps` (Phase 2d).

**Decomposition sequence**:
1. Enumerate treebank directories. Each directory = one treebank (language + genre).
2. For each `.conllu` file: parse sentences.
3. Per sentence: create `ud_sentence` composition entity (Merkle hash of token hashes).
4. Per token: create `ud_token` entity (= `word_form` — same hash, same entity).
5. Create sequence entries (sentence → tokens in order).
6. Per dependency arc (HEAD → DEPREL → dependent): create syntactic edge with deprel-specific edge type code.
7. Populate junctions: `entity_pos` (token → UPOS), `entity_morph_feature` (token → morphological features from FEATS column), `entity_language` (sentence → language from treebank metadata).

**Edge type mapping**: Each UD deprel value (`nsubj`, `obj`, `amod`, `advmod`, `nmod`, `det`, `case`, `conj`, `cc`, `mark`, `punct`, `root`, etc.) maps to its own edge type code in the `edge_type` table with `category = 'syntactic'`. These are created by the UD decomposer, not by seed scripts (per seed-scripts.md decision).

---

### Wiktionary

**Source**: `D:\Models\wiktionary`

**Files**: JSONL files from Wiktextract (Tatu Ylönen's structured Wiktionary extraction).

**Parser**: `WiktextractParser` — line-by-line JSON parsing. Each line is one entry as a JSON object.

**Phase**: `Wiktionary` (Phase 2e).

**Decomposition sequence**:
1. Parse JSONL entries.
2. Per entry: create `wikt_sense` entity for each definition/gloss.
3. Create `lemma` entities (deduplicated against existing WordNet/OMW lemmas).
4. Create edges: `has_sense` (lemma → wikt_sense), `has_form` (lemma → inflected forms from Wiktionary paradigm tables), `etymological_origin` (between lemmas with etymology data), `translation_of` (cross-lingual from translations section).
5. Populate junctions: `entity_pos`, `entity_language`.

---

### Tatoeba

**Source**: `D:\Models\tatoeba`

**Files**: `sentences.csv`, `links.csv`, `sentences_with_audio.csv`, `audio/` directory.

**Parser**: `TsvParser`.

**Phase**: `Tatoeba` (Phase 2f).

**Decomposition sequence**:
1. Parse `sentences.csv` → create `tatoeba_sentence` entities.
2. Decompose each sentence into word entities (using UAX #29 word boundaries).
3. Create sequence entries (sentence → words in order).
4. Parse `links.csv` → create `translation_of` edges between sentence pairs.
5. Parse `sentences_with_audio.csv` → create `audio_recording` entities from `audio/` files.
6. Create `recording_of` edges (audio → sentence).
7. Populate junctions: `entity_language`.

---

### Unicode (UCD / UCA)

**Source**: `D:\Models\UCD`

**Files**: `UnicodeData.txt`, `PropertyAliases.txt`, `PropertyValueAliases.txt`, `allkeys.txt`, `GraphemeBreakProperty.txt`, `WordBreakProperty.txt`, `SentenceBreakProperty.txt`, `Scripts.txt`, `Blocks.txt`.

**Parser**: `UnicodeDataParser` — semicolon-separated fields with range entries.

**Phase**: `UcdUca` (Phase 2a).

**Decomposition sequence**:
1. Parse `UnicodeData.txt` → create `codepoint` entities (one per assigned codepoint, ~150K).
2. Compute S3 Fibonacci projection from UCA collation order (via `allkeys.txt`). Create `s3_position` physicalities (POINTZM on S3 surface).
3. Populate `codepoint_property` junction (general_category, script, block, break properties from respective files).
4. Create `maps_to_lowercase`, `maps_to_uppercase`, `maps_to_titlecase` edges from case mapping fields.
5. Create composition entities for compatibility decomposition mappings.

---

### ISO 639 (Language Codes)

**Source**: `D:\Models\ISO639`

**Files**: `iso-639-3.tab`, `iso-639-3-macrolanguages.tab`, `iso-639-3_Name_Index.tab`.

**Parser**: `TsvParser`.

**Phase**: `Iso639` (Phase 2b).

**Decomposition sequence**:
1. Parse `iso-639-3.tab` → create `language_name` entities + populate `language` reference table.
2. Parse `iso-639-3-macrolanguages.tab` → create `macrolanguage_of` edges.
3. Parse name index → create `language_name` entities for alternative names + `alternate_name_of` edges.

---

### SafeTensors (Model Weights)

**Source**: `D:\Models\hub`

**Files**: `*.safetensors` files within model directories (Hugging Face Hub cache layout).

**Parser**: `SafetensorsHeaderParser` — reads the 8-byte header length prefix, parses the JSON header to get tensor metadata (name, dtype, shape, offsets), then reads tensor data by offset.

**Phase**: `ModelDecomp` (Phase 3).

**Decomposition sequence (corrected per [`docs/00-substrate-spec.md`](../../00-substrate-spec.md) §V):**
1. Enumerate model directories in the hub cache.
2. Per model: create `model_architecture` composition entity (a real structural artifact entity per spec §II.1).
3. Parse safetensors header → for each tensor, hash bytes via canonical streaming BLAKE3 (`ModelPassOrchestrator.HashTensorStreaming`) → create `tensor` entity (a real structural artifact entity).
4. Create `in_model` and `has_tensor` edges (tensor ↔ model architecture).
5. Populate `tensor_tensor_role` junction (tensor → role classification from layer name pattern matching via `TensorClassifier`: `q_proj` → `AttentionQuery`, `k_proj` → `AttentionKey`, etc.).
6. Populate `model_architecture_class` junction.
7. **For each tensor, dispatch by `TensorRole` to the appropriate layer-type decomposer** (per [`docs/specs/decomposers/layer-type-library.md`](../decomposers/layer-type-library.md)). Each layer-type decomposer emits typed attestation EDGES between existing content entities (typically two `word_form` tokens), with `attestation_type` (per `sql/schema/seed/attestation_type.sql`) on the rating event distinguishing the kind of model evidence (`model_attention_qk_pattern`, `model_ffn_full_path`, etc.). Working template: `src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs`.
8. Per-tensor analysis surfaces (sparsity profile, SVD spectrum, weight distribution, eigenvalue spectrum, etc.) attach as physicality on the tensor entity (transitionally as separate analysis-surface entities; spec §X migrates them onto tensor physicality).

> **Architectural correction:** Step 7 previously read "perform SVD, create `attention_pattern` entities from significant singular vectors." That phantom-entity emission shape was fixed by the 2026-05-08 architectural correction (phantom types removed from `sql/schema/seed/entity_type.sql`; entity_type.sql now has 23 real content types). Per-role units of Track 2 transformation tensors manifest as **typed attestation edges between existing content entities**, never as synthetic `attention_pattern` / `attention_head` / `ffn_neuron` / etc. entities. See AP-25 in `.claude/rules/45-anti-patterns.md` and spec §III, §XII.

**Large binary handling**: `SafetensorsHeaderParser` uses `Memory<byte>` and reads tensor data via file offset — no loading entire files into memory. Tensor data is processed in chunks corresponding to individual weight matrices.

---

## Runtime Decomposers

Runtime decomposers handle arbitrary input content at inference/ingestion time (after the seed type system is in place). Unlike seed decomposers which process fixed datasets, runtime decomposers accept any content of their modality. All four extend `BaseDecomposer` and share the same idempotency and hash contract guarantees.

Full decomposition pipeline specifications are in the modality domain specs. This section documents the C# implementation contracts.

### TextDecomposer

**Class**: `TextDecomposer` extends `BaseDecomposer`
**Domain spec**: [text.md](../modalities/text.md)
**Phase**: Runtime (after all seed phases complete)

Decomposes arbitrary text into the substrate using a 7-level pipeline: raw bytes → codepoints → grapheme clusters → words → morphemes → lemmas/senses → syntax → semantic analysis passes → physicality. Uses Tree-sitter for structural parsing (Markdown, code, prose) and UAX #29 for character-level segmentation. All candidate senses are linked — no disambiguation at ingestion.

**Dependencies**: UCD (character identity), WordNet/OMW (sense candidates), UD (syntactic patterns), Wiktionary (morphology/lemmatization).

### ImageDecomposer

**Class**: `ImageDecomposer` extends `BaseDecomposer`
**Domain spec**: [image.md](../modalities/image.md)
**Phase**: Runtime

Decomposes raster images into the substrate. Structurally decomposes the image format itself (JPEG markers, PNG chunks, TIFF IFDs) into typed entities. Pixel values decompose to codepoint-based number compositions via cascade compression. Analysis passes (EdgeDetection, Texture, HOG, DCT, ConnectedComponent, Contour, ColorHistogram, PerceptualHash) are all pre-computed at ingestion.

**Dependencies**: UCD (codepoints for numeric compositions), type system (image-specific types).

### AudioDecomposer

**Class**: `AudioDecomposer` extends `BaseDecomposer`
**Domain spec**: [audio.md](../modalities/audio.md)
**Phase**: Runtime

Decomposes audio into the substrate. Waveform stored as LinestringZM geometry (X=time, Y=amplitude, Z=frequency overlay, M=significance). Analysis passes include spectral (FFT, STFT, MFCC, Chromagram), temporal (pitch tracking, onset detection, silence detection, beat detection, formants), speech-specific (VAD, diarization, phoneme segmentation), and music-specific (key detection, tempo, chord recognition). All pre-computed at ingestion.

**Dependencies**: UCD (codepoints for numeric compositions), type system (audio-specific types).

### VideoDecomposer

**Class**: `VideoDecomposer` extends `BaseDecomposer`
**Domain spec**: [video.md](../modalities/video.md)
**Phase**: Runtime

Composes `ImageDecomposer` and `AudioDecomposer` — does NOT reimplement their pipelines. Demuxes container format (MP4, MKV, AVI, etc.) into video/audio/subtitle streams. Delegates frame decomposition to `ImageDecomposer`, audio to `AudioDecomposer`, subtitles to `TextDecomposer`. Adds video-specific analysis passes: SceneChangeDetection, MotionVector, TemporalCoherence, ShotBoundary, AudioVisualAlignment, OpticalFlowSummary.

**Dependencies**: `ImageDecomposer`, `AudioDecomposer`, `TextDecomposer`, type system (video-specific types).

---

## Decomposer Index

| Decomposer | Class | Phase | Source Format | Parser | Entity Types Created | Estimated Volume |
|-----------|-------|-------|--------------|--------|---------------------|-----------------|
| UCD/UCA | `UcdUcaDecomposer` | 2a | Semicolon-separated | `UnicodeDataParser` | codepoint, collation_element | ~150K entities, ~150K physicalities |
| ISO 639 | `Iso639Decomposer` | 2b | TSV | `TsvParser` | language_name | ~8K entities |
| WordNet | `WordNetDecomposer` | 2c | WordNet DB | `WordNetDbParser` | synset, lemma, word_sense, inflected_form | ~480K entities, ~370K edges |
| OMW | `OmwDecomposer` | 2c | TSV | `TsvParser` | lemma | ~500K entities, ~500K edges |
| UD | `UdDecomposer` | 2d | CoNLL-U | `ConllUParser` | ud_sentence, ud_token | ~2M entities, ~3M edges |
| Wiktionary | `WiktionaryDecomposer` | 2e | JSONL | `WiktextractParser` | wikt_sense, lemma, inflected_form | ~5M entities, ~8M edges |
| Tatoeba | `TatoebaDecomposer` | 2f | CSV/audio | `TsvParser` | tatoeba_sentence, audio_recording | ~10M entities, ~20M edges |
| SafeTensors | `SafetensorsDecomposer` | 3 | Binary | `SafetensorsHeaderParser` | tensor, model_architecture, attention_pattern | Varies per model |
| Text | `TextDecomposer` | Runtime | Any text | Tree-sitter + UAX #29 | grapheme_cluster, word_form, morpheme, text compositions | Varies per input |
| Image | `ImageDecomposer` | Runtime | Raster image | Format-specific | pixel_region, patch, contour, image compositions | Varies per input |
| Audio | `AudioDecomposer` | Runtime | Audio files | Format-specific | audio_chunk, spectral entities, temporal events | Varies per input |
| Video | `VideoDecomposer` | Runtime | Video files | Container demux | scene, shot, frame sequences (delegates to Image/Audio) | Varies per input |
