# Audio Modality Decomposer Specification

## Identity

- **Decomposer class**: `AudioDecomposer` extends `BaseDecomposer`
- **Purpose**: Decompose any audio content into the substrate. Waveform as LinestringZM geometry, spectral/temporal analysis as edges, all pre-computed for lookup-only inference.
- **Dependency**: UCD seed (codepoints for numeric compositions). Type system (audio-specific types registered).

## Decomposition Pipeline

### Level 0: Decode to PCM Samples

1. Detect audio format (MP3, WAV, FLAC, OGG, AAC, etc.) from header bytes.
2. Decode to raw PCM samples: sample_rate, channels, bit_depth, duration.
3. Record format metadata as edges on the audio entity.
4. For multi-channel audio (stereo, surround), each channel is decomposed independently.

### Level 1: Waveform as LinestringZM

Each audio channel becomes a LinestringZM in PostGIS geometry:
- **X** = time (sample index / sample_rate, giving seconds)
- **Y** = amplitude (normalized float64, -1.0 to 1.0)
- **Z** = available for frequency-domain overlay or channel index
- **M** = available for significance/confidence

For long audio, segment into fixed-duration chunks (e.g., 1 second per LinestringZM at 16kHz = 16,000 points per linestring). Chunks compose into the full waveform via sequence relations.

Cascade compression: silent regions (amplitude near zero) compress via RLE. Sustained tones (repeating waveform patterns) compress via pattern references.

### Level 2: Spectral Decomposition

All pre-computed at ingestion. All stored as entities and edges.

- **FFTPass**: frequency spectrum per windowed segment. Each spectrum is a composition of frequency-magnitude pairs. Dominant frequencies are edges with magnitude as significance.
- **STFTPass**: time-frequency representation (spectrogram). Each time-frequency bin is an entity. The spectrogram is a 2D composition.
- **MFCCPass**: mel-frequency cepstral coefficients per frame. Each MFCC vector is a composition. Used for speech/music characterization.
- **ChromagramPass**: pitch class profiles per frame (musical pitch regardless of octave). Each chromagram frame is a composition.

### Level 3: Temporal Feature Extraction

- **PitchTrackingPass**: F0 (fundamental frequency) contour over time as LinestringZM (X=time, Y=frequency in Hz). Stored as a physicality on the audio entity.
- **OnsetDetectionPass**: syllable/note onset timestamps. Each onset is a typed event entity at a specific time position.
- **SilenceDetectionPass**: silence/pause regions with start/end times. Each silence segment is a typed entity. These mark word/phrase/sentence boundaries in speech.
- **BeatDetectionPass**: rhythmic beat positions for music. Each beat is a typed event entity.
- **FormantPass**: formant frequencies (F1, F2, F3, F4) over time, each as LinestringZM. Formant trajectories characterize vowels and speaker identity.
- **SpectralCentroidPass**: spectral centroid over time as LinestringZM. Characterizes "brightness" of the sound.
- **SpectralBandwidthPass**: spectral bandwidth over time.
- **SpectralRolloffPass**: frequency below which a configurable percentage of spectral energy is concentrated.
- **ZeroCrossingPass**: zero-crossing rate over time. Distinguishes voiced/unvoiced segments.
- **HarmonicPercussiveSeparationPass**: separates harmonic (pitched) and percussive (transient) components. Each component stored as a separate waveform entity.

### Level 4: Speech-Specific Analysis (when applicable)

If the audio is detected as speech (from spectral characteristics):
- **VoiceActivityDetectionPass**: which segments contain speech vs background.
- **SpeakerDiarizationPass**: which segments belong to which speaker. Each speaker is a typed entity.
- **PhonemeSegmentationPass**: approximate phoneme boundaries (from spectral transitions + onset detection).
- **ProsodicAnalysisPass**: intonation contour, stress patterns, speech rate. Each as edges.

### Level 5: Music-Specific Analysis (when applicable)

If the audio is detected as music:
- **KeyDetectionPass**: musical key (C major, A minor, etc.) as typed entity.
- **TempoEstimationPass**: beats per minute as edge.
- **ChordRecognitionPass**: chord progression over time. Each chord is a typed composition of pitch classes.
- **InstrumentDetectionPass**: identified instruments as typed entities (from model-derived edges in the substrate if music models have been ingested).

### Level 6: Physicality

- Waveform: LinestringZM per channel per chunk (primary representation).
- Pitch contour: LinestringZM (X=time, Y=Hz).
- Formant trajectories: LinestringZM per formant (F1, F2, F3, F4).
- Spectral centroid: LinestringZM (X=time, Y=Hz).
- All geometric representations GiST-indexed.
- Audio entity centroid derived from waveform geometry.

### Level 7: Cross-Modal Relations

If the audio has associated text (lyrics, transcript, caption):
- Text decomposed by `TextDecomposer`.
- Forced alignment creates edges from audio time regions to specific word/phoneme entities.
- Tatoeba audio alignment data (from seed) provides pre-existing alignment patterns.

## PostGIS Operations on Audio

With waveform as LinestringZM, PostGIS functions operate on audio directly:

- `ST_Length(waveform)` = duration (total time span).
- `ST_NPoints(waveform)` = sample count.
- `ST_LineSubstring(waveform, start_frac, end_frac)` = extract time segment.
- `ST_Simplify(waveform, tolerance)` = downsample/smooth waveform.
- `ST_FrechetDistance(waveform_a, waveform_b)` = waveform shape similarity.
- `ST_HausdorffDistance(waveform_a, waveform_b)` = another similarity metric.
- `ST_DWithin(point, waveform, distance)` = find waveform segments near a point in time-amplitude space.
- `ST_Intersection(waveform, time_range)` = clip waveform to time range.

## Cascade Compression

- Silence regions (near-zero amplitude): RLE compresses to one "silence" entity with duration count.
- Sustained tones (repeating waveform pattern): pattern entity referenced with period count.
- Common spectral patterns (speech formant shapes, musical chords): shared composition entities.

## Round-Trip

`AudioRecomposer` reconstructs audio:
1. Read format metadata (sample_rate, channels, bit_depth, target format).
2. Walk LinestringZM waveform(s), extract amplitude values at time positions.
3. Convert to PCM samples.
4. Encode to target format.
5. For lossless formats (WAV, FLAC): bit-perfect round-trip.
6. For lossy formats (MP3): the decomposer structurally decomposes the format — frame headers, bitrate/sample-rate parameters, Huffman-coded spectral data, ID3 tags, Xing/LAME headers — into typed entities. Round-trip means the recomposer walks the structural entity tree and reconstructs a valid MP3. No binary blobs stored.
