# Tatoeba Decomposer Specification

## Identity

- **Decomposer class**: `TatoebaDecomposer` extends `BaseDecomposer`
- **Source path**: `D:\Models\tatoeba\`
- **Trust prior**: Medium (community-contributed sentences and translations, volunteer audio recordings)
- **Provenance**: `tatoeba.org` with per-contributor sub-provenance for audio
- **Dependency**: Phase 2e (Wiktionary seeded -- Tatoeba sentences reference words/senses that should already exist). ISO 639 for language tags. UCD for codepoint decomposition.

## What This Decomposer Creates

Attested usage: real sentences written by real humans, translation pairs across languages, and audio recordings of sentences. This provides usage evidence, translation attestation, and speech grounding for the substrate.

## Source Files

### `sentences.csv` (738MB, 13,262,153 rows confirmed)

Tab-separated, 3 columns, UTF-8. No header row.

| Column | Description | Example |
|--------|-------------|---------|
| sentence_id | Integer unique ID | `1` |
| lang | ISO 639-3 code | `cmn`, `eng`, `fra`, `deu` |
| text | Sentence text in UTF-8 | `我們試試看！`, `This is a pen.` |

### `links.csv` (442MB, 27,628,074 rows confirmed)

Tab-separated, 2 columns. No header row. Translation pair linkages.

| Column | Description | Example |
|--------|-------------|---------|
| sentence_id | Source sentence ID | `1` |
| translation_id | Target sentence ID | `2481` |

Links are directional. Both directions may be present (1->2481 and 2481->1) but this is not guaranteed. The decomposer must handle both symmetric and asymmetric links.

### `audio/sentences_with_audio.csv` (72MB, 1,238,048 rows confirmed)

Tab-separated, 3 columns. No header row.

| Column | Description | Example |
|--------|-------------|---------|
| sentence_id | Sentence ID that has audio | `1` |
| audio_id | Audio recording ID | `1276691` |
| contributor | Username of the recorder | `LeviHighway` |

### Audio Files

MP3 files organized by language and contributor under `audio/eng/tatoeba_audio_eng/audio/`. 4 top-level contributor directories observed. ~299,856 MP3 files total (from inventory).

Audio file path pattern: `audio/eng/tatoeba_audio_eng/audio/{contributor_prefix}/{filename}.mp3`

## Entity Model

### Sentences

```
-- Entity table row:
entity: hash=BLAKE3('tatoeba_sentence_1'), entity_type_id→entity_type('tatoeba_sentence')

-- Junction table entry:
entity_language: entity_id=tatoeba_sentence_1, language_id→language('cmn')

-- Edges:
edge(type='has_text', source=tatoeba_sentence_1, target=Entity('我們試試看！'))
edge(type='has_tatoeba_id', source=tatoeba_sentence_1, target=Entity(1))

-- The text "我們試試看！" is itself decomposed:
entity: hash=BLAKE3('我們試試看！'), entity_type_id→entity_type('text_composition')
  sequence: [我, 們, 試, 試, 看, ！]  // codepoint references, 試 deduplicated
  // Morphological/syntactic edges added by cross-referencing UD/Wiktionary seed data
```

### Translation Links

Translation links are n-ary edges connecting sentence entities across languages.

```
-- Edge (n-ary translation link):
edge(type='translation_link', members=[
    (entity=tatoeba_sentence_1, role='source'),
    (entity=tatoeba_sentence_2481, role='target')
], provenance='tatoeba.org')
```

When sentence 1 (cmn) links to sentence 2481 (eng), the substrate has a typed cross-lingual edge between the two text compositions.

### Audio

```
-- Entity table row:
entity: hash=BLAKE3(audio_1276691), entity_type_id→entity_type('audio_recording')

-- Edges:
edge(type='recording_of', source=audio_recording_1276691, target=tatoeba_sentence_1)
edge(type='has_contributor', source=audio_recording_1276691, target=Entity('LeviHighway'))

-- Physicality:
physicality: entity_id=audio_recording_1276691, type='audio_waveform', geom=LINESTRINGZM
```

Audio recordings are decomposed into:
- Raw waveform as LINESTRINGZM (amplitude over time in PostGIS geometry)
- Alignment edge from audio entity to sentence entity
- Contributor attribution as provenance

## Audio Decomposition Detail

Each MP3 file is decoded to PCM samples, then:

1. **Waveform representation**: amplitude samples as LinestringZM where X=time (sample index / sample_rate), Y=amplitude (normalized float). Z and M available for frequency/significance.

2. **Ingestion-time analysis passes** (all pre-computed, all stored as edges and physicality rows):
   - `FFTPass` -- frequency spectrum per windowed segment
   - `MFCCPass` -- mel-frequency cepstral coefficients for speech characterization
   - `PitchTrackingPass` -- F0 contour as LinestringZM (pitch over time)
   - `OnsetDetectionPass` -- syllable/word onset boundaries
   - `SilenceDetectionPass` -- pause boundaries (marks word/phrase breaks)
   - `SpectralCentroidPass` -- spectral centroid over time
   - `FormantPass` -- formant frequencies (F1, F2, F3) for vowel characterization

3. **Forced alignment** (where possible): align audio onsets to word boundaries in the corresponding sentence text. This creates edges from audio time-regions to specific word entities in the sentence composition.

## Cross-References

- Sentence language codes reference `language` reference table rows via `entity_language` junction.
- Sentence text is decomposed into codepoint compositions (UCD entities).
- Words in sentences cross-reference Wiktionary/WordNet lemma entities where they match via edges.
- Translation links corroborate OMW cross-lingual synset alignments.
- Audio forced alignment connects audio segments to specific word/morpheme entities via edges.

## Significance

- Trust prior: Medium (community-contributed).
- Sentence significance derived from:
  - Number of translations (more translated = higher quality sentence).
  - Number of audio recordings (recorded sentences are validated by contributors).
  - Language coverage (sentences in rare languages have higher value for coverage).
- Audio significance derived from contributor reputation and recording quality metrics (SNR from spectral analysis).

## Streaming Strategy

- `sentences.csv` at 738MB can be batch-processed in chunks (e.g., 100K sentences per batch).
- `links.csv` at 442MB same approach.
- Audio files processed one at a time (each is a separate MP3 decode + analysis pipeline).
- Checkpoint after each batch or each audio file for resumability.
- Sentences must be ingested before links (links reference sentence IDs).
- Audio ingestion references sentence IDs from sentences_with_audio.csv.

## Completeness Criteria

- All 13,262,153 sentences ingested with language tags and text decomposition.
- All 27,628,074 translation links ingested as cross-lingual edges.
- All 1,238,048 audio entries linked to their sentences.
- All available MP3 audio files decoded, analyzed (FFT, MFCC, pitch, onsets, silence, formants), and stored as LINESTRINGZM + analysis edges.
- Forced alignment between audio and sentence text where achievable.
- Cross-references to ISO 639, WordNet/Wiktionary lemmas, and OMW alignments.
- Per-contributor provenance for audio recordings.
- ZERO opaque blobs. Audio is waveform geometry. Text is codepoint compositions.
