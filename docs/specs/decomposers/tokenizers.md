# Tokenizers & Text Segmentation

**Status**: 🔜 M5e prerequisite (task #37)

Shared text-segmentation primitives used by:

- `TokenizerMappingPass` (model analysis — parses a model's trained tokenizer, maps its vocabulary into substrate entities).
- `TextDecomposer` (M6 runtime — ingests user-submitted text for inference-time querying).
- `SafetensorsRecomposer` (M8 distillation — constructs a student-model tokenizer from substrate coverage).
- `WiktionaryDecomposer`, `TatoebaDecomposer` (M5g/h seed decomposers that need normalized lemma forms while preserving raw content).

One implementation, four callers. All live under `Hartonomous.Core.Text.*` (not a decomposer-private namespace). Zero approximation — every primitive is exact, deterministic, Unicode-version-pinned.

---

## Design invariants

1. **Content is preserved byte-for-byte.** Segmentation operates on *views* over input bytes, never on rewritten copies. Any normalization (NFC/NFKC/casefold) produces an additional annotation layer — the original bytes and the normalized bytes coexist as related substrate entities. The substrate never silently substitutes normalized form for original.
2. **Unicode version is pinned.** The primitives target a single Unicode version declared at build time (`UnicodeVersion.Current = "16.0"` for the M5 cutover). Segmentation, casefold, and categorization tables are generated from that version's UCD at build time and embedded as static data. A different Unicode version yields a different `TextSegmentationProfile` entity hash, so upgrades are explicit substrate events.
3. **Determinism is absolute.** Same input bytes + same Unicode version = same segmentation, same token sequence, bit-for-bit. Law #6.
4. **No regex for UAX #29.** Regex patterns for grapheme clusters / word / sentence boundaries are incomplete or locale-dependent in every runtime. The primitives implement the UAX #29 state machines directly from the canonical breaking tables.
5. **Tokenizer formats are parsed, not emulated.** When we ingest HuggingFace `tokenizer.json`, we parse the declared pre-tokenizer / normalizer / model / post-processor chain and record it *as substrate entities*. We do not re-implement BPE merging at ingestion time unless the pass specifically requires it (for vocab coverage analysis). The tokenizer configuration is the content; the merge table is the content; the substrate records both.

---

## `Hartonomous.Core.Text.Segmentation` — UAX #29 primitives

```csharp
namespace Hartonomous.Core.Text.Segmentation;

public enum UnicodeVersion { U16_0 = 160 }

public static class GraphemeClusters
{
    /// <summary>
    /// Enumerate extended-grapheme-cluster boundaries over the input per UAX #29.
    /// Yields (ByteOffset, CodepointOffset, ByteLength, CodepointLength) tuples.
    /// Deterministic, Unicode-version-pinned.
    /// </summary>
    public static IEnumerable<GraphemeRange> Enumerate(ReadOnlySpan<byte> utf8);

    /// <summary>
    /// Count extended grapheme clusters without materializing ranges.
    /// </summary>
    public static long Count(ReadOnlySpan<byte> utf8);
}

public readonly record struct GraphemeRange(
    long ByteOffset, long CodepointOffset,
    int  ByteLength, int  CodepointLength);

public static class WordBoundaries
{
    /// <summary>
    /// UAX #29 word boundaries. Emits one range per word token (non-space,
    /// non-punctuation content between boundary points — the conventional
    /// "word" interpretation from UAX #29 §4.2).
    /// </summary>
    public static IEnumerable<WordRange> EnumerateWords(ReadOnlySpan<byte> utf8);

    /// <summary>
    /// All UAX #29 word boundary positions (including between whitespace and punctuation).
    /// </summary>
    public static IEnumerable<long> EnumerateBoundaries(ReadOnlySpan<byte> utf8);
}

public readonly record struct WordRange(long ByteOffset, int ByteLength, WordKind Kind);

public enum WordKind { AlphaNumeric, Numeric, Hiragana, Katakana, CjkIdeograph, Hangul, Emoji, Other }

public static class SentenceBoundaries
{
    public static IEnumerable<SentenceRange> Enumerate(ReadOnlySpan<byte> utf8);
}

public readonly record struct SentenceRange(long ByteOffset, int ByteLength);

public static class LineBreaks
{
    /// <summary>
    /// UAX #14 line-break opportunities. Used by recomposers that wrap output.
    /// Not consulted during decomposition — line break is presentation, not content.
    /// </summary>
    public static IEnumerable<LineBreakOpportunity> Enumerate(ReadOnlySpan<byte> utf8);
}

public readonly record struct LineBreakOpportunity(long ByteOffset, LineBreakClass Class);
public enum LineBreakClass { Direct, Indirect, Prohibited, Mandatory }
```

### Implementation notes

- State machines encoded as switch tables on `(prev_property, curr_property) → Break | NoBreak | ExtendBuffer`. Tables generated from UCD `GraphemeBreakProperty.txt`, `WordBreakProperty.txt`, `SentenceBreakProperty.txt` at build time via a small T4/source-generator step. Generated tables are committed — no runtime code generation.
- UTF-8 decoding is integrated: a single pass over input bytes yields `(codepoint, byteLength)` and feeds the state machine. No separate "decode to UTF-32 array, then segment" step — segmentation is streaming.
- Extended grapheme clusters are always "extended" (GB9/GB9a/GB9b). Legacy clusters are not offered; legacy is not useful for substrate identity.

---

## `Hartonomous.Core.Text.Normalization`

```csharp
namespace Hartonomous.Core.Text.Normalization;

public enum NormalizationForm { NFC, NFD, NFKC, NFKD }

public static class UnicodeNormalize
{
    /// <summary>
    /// Produce a normalized copy. Returns a new byte[] — the substrate never
    /// mutates original content. Deterministic, Unicode-version-pinned.
    /// </summary>
    public static byte[] ToForm(ReadOnlySpan<byte> utf8, NormalizationForm form);

    /// <summary>
    /// Fast check — is this input already in the requested form?
    /// </summary>
    public static bool IsForm(ReadOnlySpan<byte> utf8, NormalizationForm form);
}

public static class CaseFold
{
    /// <summary>
    /// Full casefold per UCD CaseFolding.txt (status C + F). Returns new byte[].
    /// </summary>
    public static byte[] Full(ReadOnlySpan<byte> utf8);

    /// <summary>Simple casefold (status C + S). Used where "one codepoint in, one codepoint out" is required (e.g., certain tokenizer normalizers).</summary>
    public static byte[] Simple(ReadOnlySpan<byte> utf8);
}
```

### Normalization as annotation, not mutation

The `TextDecomposer` creates a `text_content` entity hashed on raw UTF-8 bytes. When a downstream analysis pass normalizes (e.g., NFC for lemma matching), the normalized form is a *new* entity — `normalized_text_content` with signature `(original_hash, form, normalized_bytes)`. An edge `has_normalization(original, normalized, form)` records the relationship. Both entities coexist; substrate queries can traverse either direction.

This preserves Law #5 for modalities (bit-perfect round-trip on original bytes) while still making normalized forms available for efficient lemma lookup, case-insensitive search, etc.

---

## `Hartonomous.Core.Text.Tokenizers` — format parsers

Parsers for the tokenizer formats AI models ship with. Each parser consumes the tokenizer's config artifact(s) and emits a canonical `TokenizerModel` representation that `TokenizerMappingPass` walks.

### `TokenizerModel`

```csharp
public sealed record TokenizerModel(
    TokenizerKind Kind,                              // Bpe, WordPiece, SentencePiece, Tiktoken, …
    byte[] ConfigHash,                               // BLAKE3 of the canonicalized config — entity identity
    IReadOnlyList<Normalizer> Normalizers,           // ordered chain; byte-exact semantics captured
    IReadOnlyList<PreTokenizer> PreTokenizers,       // whitespace/punctuation splitters
    IReadOnlyList<PostProcessor> PostProcessors,     // BOS/EOS/CLS/SEP insertion
    IReadOnlyDictionary<int, VocabularyEntry> Vocab, // token id → entry
    IReadOnlyList<MergeRule> Merges,                 // BPE merge rules (empty for WordPiece/SP)
    SpecialTokens Specials);                         // bos/eos/pad/unk/mask ids

public enum TokenizerKind { Bpe, ByteBpe, WordPiece, SentencePiece, Tiktoken, CharLevel, Unknown }

public sealed record VocabularyEntry(
    int TokenId,
    byte[] TokenBytes,                               // raw bytes — may include ▁ marker, Ġ prefix, etc.
    bool IsSpecial);

public sealed record MergeRule(byte[] Left, byte[] Right, int Priority);

public sealed record Normalizer(string Kind, IReadOnlyDictionary<string, string> Parameters);
public sealed record PreTokenizer(string Kind, IReadOnlyDictionary<string, string> Parameters);
public sealed record PostProcessor(string Kind, IReadOnlyDictionary<string, string> Parameters);

public sealed record SpecialTokens(int? Bos, int? Eos, int? Pad, int? Unk, int? Mask, IReadOnlyList<int> Additional);
```

### Parsers

```csharp
public static class HuggingFaceTokenizerParser
{
    /// <summary>
    /// Parse `tokenizer.json` (the single-file HuggingFace tokenizers format).
    /// Canonicalizes the JSON (key ordering, whitespace stripping) before hashing
    /// so cosmetically different but semantically identical configs produce the
    /// same ConfigHash.
    /// </summary>
    public static TokenizerModel Parse(ReadOnlySpan<byte> tokenizerJsonUtf8);
}

public static class SentencePieceTokenizerParser
{
    /// <summary>Parse a SentencePiece .model file (protobuf).</summary>
    public static TokenizerModel Parse(ReadOnlySpan<byte> spModelBytes);
}

public static class WordPieceTokenizerParser
{
    /// <summary>Parse a WordPiece `vocab.txt` plus `tokenizer_config.json`.</summary>
    public static TokenizerModel Parse(
        ReadOnlySpan<byte> vocabTxtUtf8,
        ReadOnlySpan<byte> tokenizerConfigJsonUtf8);
}

public static class TiktokenTokenizerParser
{
    /// <summary>Parse a tiktoken .tiktoken file (base64-encoded byte-BPE merge table).</summary>
    public static TokenizerModel Parse(ReadOnlySpan<byte> tiktokenFileUtf8);
}
```

### Canonicalization

`tokenizer.json` files from HuggingFace vary in whitespace and field ordering even when semantically identical. `HuggingFaceTokenizerParser` canonicalizes:

1. Sort JSON object keys lexicographically at every level.
2. Strip insignificant whitespace.
3. Drop null-valued optional fields (treated as absent).
4. Normalize merge-rule representation (space-separated string → `{left, right}` pair).

The canonical form is what gets BLAKE3-hashed. Two HuggingFace tokenizers that differ only in JSON serialization produce the same `TokenizerModel.ConfigHash` and therefore deduplicate into one substrate entity. Two tokenizers that differ in normalizers, merges, or vocab produce different hashes.

---

## Tokenizer primitive — the re-tokenize operation

Some analysis passes need to replay the tokenizer on canonical substrate inputs (e.g., `VocabCoveragePass` tokenizes a substrate-owned lexical probe corpus). The primitive:

```csharp
namespace Hartonomous.Core.Text.Tokenizers;

public static class Tokenize
{
    /// <summary>
    /// Apply the full normalize → pre-tokenize → model → post-process chain and
    /// return token ids + byte-exact offsets into the original input. Deterministic.
    /// Offsets track back through normalization so the original byte range that
    /// produced each token is always recoverable.
    /// </summary>
    public static IReadOnlyList<TokenWithOffset> Encode(
        TokenizerModel tokenizer,
        ReadOnlySpan<byte> inputUtf8);

    /// <summary>
    /// Inverse — token ids back to the normalized byte stream. Depending on the
    /// tokenizer, this may or may not round-trip to the original bytes; if not,
    /// the original is still preserved in the substrate as the source entity.
    /// </summary>
    public static byte[] Decode(TokenizerModel tokenizer, ReadOnlySpan<int> tokenIds);
}

public readonly record struct TokenWithOffset(int TokenId, long OriginalByteOffset, int OriginalByteLength);
```

Encoding is implemented over the parsed `TokenizerModel` — not by shelling out to HuggingFace's Python tokenizers or linking its Rust library. Reasons:

- External tokenizer libraries introduce runtime version coupling that breaks Law #6 across deploys.
- We need exact offset tracking back through normalization, which HuggingFace's library supports partially and only for some tokenizer kinds.
- Implementing ~2,000 lines of deterministic BPE/WordPiece/SentencePiece in managed code is strictly cheaper than defending a Python bridge across Windows/Linux.

---

## Entity mapping

How primitives map to substrate entities and edges:

| Primitive output | Entity kind | Signature fields |
|---|---|---|
| `TokenizerModel.ConfigHash` | `tokenizer_model` | canonicalized config bytes |
| `VocabularyEntry` | `bpe_token` | (tokenizer_model hash, token bytes) |
| `MergeRule` | `bpe_merge_rule` | (tokenizer_model hash, left bytes, right bytes, priority) |
| `GraphemeRange` over substrate text | `grapheme_cluster` | (codepoints bytes) — not position-keyed |
| `SentenceRange` over substrate text | `sentence_content` | (sentence bytes) — position on `has_source` edge |
| Normalized form of content X | `normalized_text_content` | (original hash, NormalizationForm, normalized bytes) |
| Per-Unicode-version behavior | `text_segmentation_profile` | (Unicode version, UCD file hashes) |

All signatures follow the canonical signature builder pattern from `docs/specs/decomposers/analysis-passes.md` § *Canonical signatures*. Nothing is hashed over filenames, positions, tokenizer filenames, or model identity — placement edges carry that.

---

## Determinism & test matrix

- **Round-trip.** Every `Tokenize.Encode` → `Tokenize.Decode` pair on substrate content is tested. When round-trip loses information (normalizers that are lossy by design — NFKC, casefold, byte-fallback stripping), the test asserts the normalized form matches the post-normalize byte stream, not the original.
- **Unicode version sanity.** A corpus of ~10K UAX #29 test cases (from the official test data) is run; expected breakpoints must match exactly.
- **Cross-tokenizer identity.** Parse the same tokenizer with two cosmetic JSON variations; assert `ConfigHash` equal.
- **Prohibited dependencies.** No reference to `Microsoft.DeepDev.TokenizerLib`, `SharpToken`, `tiktoken-sharp`, or any other third-party tokenizer package. All parsing is first-party.

---

## Cross-references

- `docs/specs/modalities/text.md` — the 7-level text decomposition that consumes segmentation primitives at the word/codepoint boundary.
- `docs/specs/decomposers/analysis-passes.md` — `TokenizerMappingPass` and `VocabCoveragePass` both consume the `TokenizerModel` abstraction.
- `docs/specs/decomposers/safetensors.md` — ingestion-time call site.
- `docs/specs/decomposers/ucd-uca.md` — UCD seed decomposer provides the codepoint entities that `composed_of_codepoints` edges target.
- `docs/specs/decomposers/wordnet.md`, `.../wiktionary.md` — lemma-lookup callers of `UnicodeNormalize` and `CaseFold`.
- `docs/architecture.md` Law #5 (content preservation) and Law #6 (determinism) — the two invariants these primitives implement.
