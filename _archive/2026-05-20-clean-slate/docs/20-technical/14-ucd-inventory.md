# UCD / UCA Full Inventory and Decomposer Usage

**Status:** Canonical
**Last verified:** 2026-04-29 (every file listed confirmed present at `D:\Models\UCD\Public\UCD\latest\`)
**Audience:** UCD decomposer authors, text decomposer authors, anyone touching the substrate's foundational text layer.

---

## Why this document exists

I previously documented "UCD" as if it were `ucd.all.flat.xml` plus `allkeys.txt`. The user has the **full Unicode FTP mirror** at `D:\Models\UCD\Public\UCD\latest\` — roughly 80+ data files across 7 subdirectories. Each file contributes specific substrate value. Some of them are critical for decomposer correctness and were entirely missing from prior planning.

This document is the per-file inventory of what's there and how the substrate uses each piece. Decomposer authors should read this before planning what to ingest from UCD.

## Top-level structure

```
D:\Models\UCD\Public\UCD\latest\
├── ReadMe.txt                Top-level UCD readme
├── ucd\                      Main UCD data files (~50 files + 3 subdirs)
├── ucdxml\                   12 XML/ZIP files (machine-readable, all formats)
├── uca\                      Unicode Collation Algorithm data
├── emoji\                    Top-level emoji sequences (UTS #51)
├── idna\                     Internationalized Domain Names (UTS #46)
├── security\                 UTS #39 identifier security
└── charts\                   Reference PDF charts
```

## `ucd\` — Main UCD data files

The substrate's UCD decomposer ingests these directly. Each file is plain text, mostly semicolon-delimited rows with one entry per Unicode codepoint or codepoint range.

### Core property files

| File | Format | Substrate use |
|---|---|---|
| `UnicodeData.txt` | `code;name;general_category;combining_class;bidi_class;decomposition;decimal_digit;digit_value;numeric_value;mirrored;old_name;comment;uppercase;lowercase;titlecase` | THE primary codepoint properties source. Every codepoint atom's general category, combining class, name, bidi class, decomposition mapping, numeric value, case mappings come from here. Required for atom seeding. |
| `PropertyAliases.txt` | `long_name; abbreviated_name` | Property name canonicalization. Substrate uses canonical short names internally; long names exposed via cognitive surface. |
| `PropertyValueAliases.txt` | `property; value_long; value_short; value_aliases` | Property-value canonicalization (e.g., `gc;Lu;Uppercase_Letter` → "Lu"). Required for substrate to know that "Letter, uppercase" and "Lu" mean the same thing. |
| `Blocks.txt` | `start..end; block_name` | Codepoint→block mapping (`Basic_Latin`, `CJK_Unified_Ideographs`, etc.). Populates `junc.codepoint_property.block_id` and the block reference vocabulary. |
| `Scripts.txt` | `code_or_range; script_name` | Codepoint→script mapping (`Latin`, `Han`, `Arabic`, ...). Populates `junc.codepoint_property.script_id`. |
| `ScriptExtensions.txt` | `code_or_range; script_list` | Codepoints used in MULTIPLE scripts (e.g., punctuation shared across Latin/Cyrillic/Greek). Substrate represents this as multiple `entity_script` junction rows per codepoint. |
| `PropList.txt` | `code_or_range; binary_property` | Binary property assignments (Alphabetic, Lowercase, ID_Start, ID_Continue, Hex_Digit, Math, etc.). Each binary property is a junction row. |
| `DerivedAge.txt` | `code_or_range; unicode_version` | Which Unicode version introduced each codepoint. Useful for vintage-tracking arena (`unicode_age` arena). |
| `DerivedCoreProperties.txt` | `code_or_range; property` | Derived properties: Alphabetic, Lowercase, Uppercase, Math, ID_Start, ID_Continue, XID_Start, XID_Continue, Default_Ignorable_Code_Point, Grapheme_Extend, Grapheme_Base, Grapheme_Link, Cased, Case_Ignorable, Changes_When_Lowercased, etc. |
| `DerivedNormalizationProps.txt` | `code_or_range; norm_property` | Normalization-related derived properties: NFD_QC, NFC_QC, NFKD_QC, NFKC_QC, Full_Composition_Exclusion, Expands_On_NFC/NFD/NFKC/NFKD. **Critical for the substrate's NFC normalization step**. |
| `EastAsianWidth.txt` | `code_or_range; width` | East Asian width (W=wide, N=narrow, A=ambiguous, etc.). Drives terminal/display width calculation. |
| `LineBreak.txt` | `code_or_range; line_break_class` | UAX #14 line break class. Drives line-break decomposer if implemented. |
| `HangulSyllableType.txt` | `code_or_range; type` | L (leading jamo) / V (vowel jamo) / T (trailing jamo) / LV / LVT classification. **Critical for Hangul precomposed↔jamo decomposition**. |
| `Jamo.txt` | `code; jamo_short_name` | Hangul jamo short names for syllable name composition. |

### Bidi / RTL files

| File | Substrate use |
|---|---|
| `BidiBrackets.txt` | Bracket pairs (matching open/close) with bracket type. Substrate's bidi-aware text rendering uses these. |
| `BidiMirroring.txt` | Codepoints that should be mirrored in RTL contexts (parens, braces, etc.). |
| `BidiCharacterTest.txt` | Bidi conformance test fixtures. |
| `BidiTest.txt` | Additional bidi tests. |

### Case files

| File | Substrate use |
|---|---|
| `CaseFolding.txt` | Per-codepoint case folding mappings (used for case-insensitive comparison). Populates `case_folds_to` edges. |
| `SpecialCasing.txt` | Locale-specific and context-specific case mappings (Turkish dotless I, etc.). Edge cases the simple case_maps_to_* edges miss. |

### Decomposition / normalization files

| File | Substrate use |
|---|---|
| `CompositionExclusions.txt` | Codepoints excluded from canonical composition (kept as decomposed even in NFC). NFC algorithm needs this. |
| `NormalizationCorrections.txt` | Historical normalization corrections (rarely-needed errata). |
| `NormalizationTest.txt` | Conformance fixtures for NFC/NFD/NFKC/NFKD. **Mandatory test set for substrate's NFC normalization gate.** |
| `EquivalentUnifiedIdeograph.txt` | CJK ideograph equivalences (compatibility/unification mappings). |
| `StandardizedVariants.txt` | Standardized variation sequences (codepoint + variation selector → specific glyph). |

### Indic / Arabic shaping

| File | Substrate use |
|---|---|
| `ArabicShaping.txt` | Per-Arabic-codepoint joining type and joining group. Drives Arabic shaping rules. |
| `IndicPositionalCategory.txt` | Per-Indic-codepoint positional category (Top, Bottom, Left, Right, etc.). |
| `IndicSyllabicCategory.txt` | Per-Indic-codepoint syllabic category (Consonant, Vowel_Independent, Virama, etc.). |

### CJK / East Asian

| File | Substrate use |
|---|---|
| `CJKRadicals.txt` | Kangxi radical-stroke index (radical → CJK ideographs). |
| `EmojiSources.txt` | Mapping of Unicode emoji to legacy Japanese emoji sources (DoCoMo, KDDI, SoftBank). |
| `NushuSources.txt` | Sources for Nushu script characters. |
| `TangutSources.txt` | Sources for Tangut script characters. |
| `USourceData.txt` | CJK Unified Ideograph source data. |
| `USourceGlyphs.pdf` | CJK Unified Ideograph glyph reference (PDF). |
| `USourceRSChart.pdf` | Radical-Stroke chart for CJK Unified Ideographs (PDF). |
| `Unihan.zip` | Full CJK Unihan database (separately archived; very large). Contains Mandarin/Cantonese/Korean/Japanese/Vietnamese readings, kangxi index, frequency counts, etc. for ~90K CJK codepoints. |
| `Unikemet.txt` | Per-codepoint kemet (compatibility) properties. |

### Naming

| File | Substrate use |
|---|---|
| `NamesList.txt` | Human-readable annotations for codepoint charts (the official name list with cross-references and glyph variants). |
| `NamesList.html` | HTML version of the name list. |
| `NameAliases.txt` | Alternative names for codepoints (control names, formal aliases, abbreviations, figment names). |
| `NamedSequences.txt` | Multi-codepoint sequences with assigned names (e.g., LATIN CAPITAL LETTER A WITH ACUTE AND DOT ABOVE). |
| `NamedSequencesProv.txt` | Provisional named sequences (not yet stable). |
| `Index.txt` | Index of files in the UCD distribution. |

### Other

| File | Substrate use |
|---|---|
| `DoNotEmit.txt` | Codepoints that should not be emitted by editors (deprecated). |
| `VerticalOrientation.txt` | Per-codepoint vertical text orientation. Drives vertical-text layout. |
| `UCD.zip` | The entire UCD as a single ZIP for atomic download. |

## `ucd\auxiliary\` — UAX #29 segmentation property tables

These are THE rules driving the text decomposer's grapheme/word/sentence/line segmentation. **Mandatory** for the text decomposer.

| File | Substrate use |
|---|---|
| `GraphemeBreakProperty.txt` | Per-codepoint Grapheme_Cluster_Break property (CR, LF, Control, Extend, ZWJ, Regional_Indicator, Prepend, SpacingMark, L, V, T, LV, LVT, Extended_Pictographic, etc.). **Drives grapheme cluster decomposition.** |
| `GraphemeBreakTest.txt` | Conformance test fixtures for grapheme clustering. **Mandatory test set for substrate's grapheme decomposer gate.** |
| `GraphemeBreakTest.html` | Same fixtures, browsable HTML form. |
| `WordBreakProperty.txt` | Per-codepoint Word_Break property (CR, LF, Newline, Extend, ZWJ, Regional_Indicator, Format, Katakana, Hebrew_Letter, ALetter, Single_Quote, Double_Quote, MidNumLet, MidLetter, MidNum, Numeric, ExtendNumLet, WSegSpace, Extended_Pictographic, etc.). **Drives word boundary decomposition.** |
| `WordBreakTest.txt` | Conformance test fixtures. **Mandatory.** |
| `WordBreakTest.html` | HTML form. |
| `SentenceBreakProperty.txt` | Per-codepoint Sentence_Break property (CR, LF, Sep, Extend, Format, Sp, Lower, Upper, OLetter, Numeric, ATerm, STerm, Close, SContinue, etc.). **Drives sentence boundary decomposition.** |
| `SentenceBreakTest.txt` | Conformance test fixtures. **Mandatory.** |
| `SentenceBreakTest.html` | HTML form. |
| `LineBreakTest.txt` | UAX #14 line breaking conformance fixtures. |
| `LineBreakTest.html` | HTML form. |

## `ucd\extracted\` — Derived property views

These are pre-computed views of properties already present in the main UCD files. Useful for cross-validation during UCD decomposer testing.

| File | Source |
|---|---|
| `DerivedBidiClass.txt` | Per-codepoint bidi class, extracted |
| `DerivedBinaryProperties.txt` | Binary properties per codepoint, extracted |
| `DerivedCombiningClass.txt` | Canonical combining class, extracted |
| `DerivedDecompositionType.txt` | Decomposition type, extracted |
| `DerivedEastAsianWidth.txt` | East Asian width, extracted |
| `DerivedGeneralCategory.txt` | General category, extracted |
| `DerivedJoiningGroup.txt` | Arabic joining group, extracted |
| `DerivedJoiningType.txt` | Arabic joining type, extracted |
| `DerivedLineBreak.txt` | Line break class, extracted |
| `DerivedName.txt` | Codepoint names, extracted |
| `DerivedNumericType.txt` | Numeric type, extracted |
| `DerivedNumericValues.txt` | Numeric values, extracted |

For substrate purposes: ingest from the main UCD files; use these as decomposer-correctness regression tests (the derived files should match what the decomposer extracts from `UnicodeData.txt` etc.).

## `ucd\emoji\` — Emoji-specific UCD data

| File | Substrate use |
|---|---|
| `emoji-data.txt` | Per-codepoint emoji properties (Emoji, Emoji_Presentation, Emoji_Modifier, Emoji_Modifier_Base, Emoji_Component, Extended_Pictographic). Drives emoji-aware grapheme clustering. |
| `emoji-variation-sequences.txt` | Standardized emoji variation sequences (codepoint + VS-15 text presentation OR VS-16 emoji presentation). |
| `ReadMe.txt` | Documentation. |

## `ucdxml\` — Machine-readable XML formats

The full UCD as XML (one element per codepoint, all properties as attributes). **Easier to ingest than the parallel-file approach** for substrate's UCD decomposer if the decomposer is XML-aware.

| File | Format | Recommended use |
|---|---|---|
| `ucd.all.flat.xml` | Single `<ucd>` root, flat list of `<char>` elements with all properties as attributes | Fast streaming-XML ingestion. The original Fail_A choice. |
| `ucd.all.flat.zip` | Compressed form | Network/disk save. Decompress on ingestion. |
| `ucd.all.grouped.xml` | Same content, codepoints grouped by shared property values | More compact; requires merging logic at parse time. |
| `ucd.all.grouped.zip` | Compressed form |   |
| `ucd.nounihan.flat.xml` | Excludes CJK Unihan codepoints | Faster ingestion if Unihan-specific properties not needed (e.g., for non-CJK-focused substrate). |
| `ucd.nounihan.flat.zip` | Compressed |   |
| `ucd.nounihan.grouped.xml` | Excludes Unihan, grouped |   |
| `ucd.nounihan.grouped.zip` | Compressed |   |
| `ucd.unihan.flat.xml` | Unihan codepoints only | Use to add Unihan data after non-Unihan ingestion. |
| `ucd.unihan.flat.zip` | Compressed |   |
| `ucd.unihan.grouped.xml` | Unihan only, grouped |   |
| `ucd.unihan.grouped.zip` | Compressed |   |
| `ucdxml.readme.txt` | XML format documentation |   |

**Recommended for substrate UCD decomposer:** `ucd.all.flat.xml` (or its ZIP) — single streaming pass produces every codepoint atom with all properties. Full file is ~150MB uncompressed XML.

## `uca\` — Unicode Collation Algorithm

THE source for the substrate's S³ Super-Fibonacci spiral codepoint positions.

| File | Substrate use |
|---|---|
| `allkeys.txt` | DUCET (Default Unicode Collation Element Table). Per-codepoint primary/secondary/tertiary/quaternary collation weights. **Critical for S³ positioning** — sort codepoints by collation tuple → map to Super-Fibonacci spiral on S³. |
| `decomps.txt` | Decompositions used by UCA collation. Some codepoints' collation is computed via decomposition. |
| `ctt.txt` | CTT (Conformance Test Tables). |
| `CollationTest.zip` | UCA conformance test data. **Mandatory** for verifying S³ projection determinism: sort our way; sort with UCA library; assert agreement. |
| `ReadMe.txt` | Documentation. |

## `emoji\` — Top-level emoji (UTS #51 sequences)

These are at the UCD root, separate from `ucd\emoji\`.

| File | Substrate use |
|---|---|
| `emoji-sequences.txt` | Canonical emoji sequences (basic emoji, keycap sequences, flag sequences, modifier sequences). Each becomes a substrate composition entity. |
| `emoji-zwj-sequences.txt` | Zero-Width-Joiner emoji sequences (e.g., 👨‍👩‍👧‍👦 family emoji, 🏳️‍🌈 rainbow flag, professional emoji). **Critical** for grapheme clustering tests — these are multi-codepoint sequences that must cluster as ONE grapheme. |
| `emoji-test.txt` | Comprehensive emoji conformance test data. **Mandatory** for substrate's grapheme decomposer's emoji handling. |
| `ReadMe.txt` | Documentation. |

## `idna\` — Internationalized Domain Names (UTS #46)

If/when the substrate ingests URLs or domain-name content, IDNA tables drive correctness.

| File | Substrate use |
|---|---|
| `Idna2008.txt` | IDNA2008 codepoint mappings (PVALID/CONTEXTJ/CONTEXTO/DISALLOWED). |
| `IdnaMappingTable.txt` | UTS #46 mapping table (case folding, normalization, deviation handling for IDNA). |
| `IdnaTestV2.txt` | IDNA conformance tests. |
| `ReadMe.txt` | Documentation. |

## `security\` — UTS #39 identifier security

Useful for substrate analyses involving identifier confusability and security.

| File | Substrate use |
|---|---|
| `IdentifierStatus.txt` | Per-codepoint Restricted vs Allowed for identifiers. |
| `IdentifierType.txt` | Per-codepoint identifier classification (Recommended, Inclusion, Uncommon_Use, Technical, Obsolete, Aspirational, Limited_Use, Exclusion, Not_NFKC, Not_XID, Default_Ignorable, Deprecated, Not_Character). |
| `confusables.txt` | Visually-confusable codepoint pairs. **High-value substrate ingestion target**: produces `visually_confusable_with` edges between codepoint atoms. Enables anti-phishing and identifier-security cognitive functions. |
| `confusablesSummary.txt` | Summary of confusables data. |
| `intentional.txt` | Intentional homoglyph pairs (e.g., Latin/Cyrillic/Greek look-alikes). |
| `uts39-data-17.0.0.zip` | Bundled UTS #39 data archive. |
| `ReadMe.txt` | Documentation. |

## `charts\` — Reference PDFs

Not substrate-ingested directly (PDFs aren't AST-friendly), but useful as auditor-facing references and for human cross-checks.

| File | Substrate use |
|---|---|
| `CodeCharts.pdf` | The full Unicode code charts (every codepoint with glyph). Reference only. |
| `RSIndex.pdf` | Radical-Stroke index for CJK ideographs. |
| `RSIndex.txt` | Text form of RSIndex. |
| `Readme.txt` | Documentation. |
| `fr\` | French-language version subdirectory. |

## What the substrate ingests vs cross-references

### Ingested as substrate content

- All codepoint atoms (from `UnicodeData.txt` or `ucd.all.flat.xml`)
- All UCD properties as `junc.codepoint_property` rows: general_category, script, block, gcb, wb, sb, lb, combining_class, decomposition_type, decomposition_mapping, bidi_class, joining_type, joining_group, etc.
- Canonical decomposition mappings as `canonical_decomposition_of` edges
- Compatibility decomposition mappings as `compatibility_decomposition_of` edges
- Case folding mappings as `case_folds_to` edges
- Case mappings (lower/upper/title) as `case_maps_to_*` edges
- Hangul syllable type mappings (L+V+T → LVT) as substrate edges
- UCA collation tuples for S³ Super-Fibonacci positioning of every codepoint
- Confusable pairs as `visually_confusable_with` edges (from `security\confusables.txt`)
- Emoji ZWJ sequences as multi-codepoint composition entities
- Standardized variant sequences as compositions
- Bracket pairs (from `BidiBrackets.txt`) as `bracket_pair_with` edges
- Bidi-mirroring (from `BidiMirroring.txt`) as `bidi_mirrors_to` edges

### Cross-referenced for decomposer correctness gates (NOT ingested as substrate content)

- `NormalizationTest.txt` — substrate's NFC normalization MUST match
- `GraphemeBreakTest.txt` — substrate's grapheme decomposer MUST match
- `WordBreakTest.txt` — substrate's word decomposer MUST match
- `SentenceBreakTest.txt` — substrate's sentence decomposer MUST match
- `LineBreakTest.txt` — if line decomposer is implemented
- `BidiTest.txt`, `BidiCharacterTest.txt` — bidi correctness
- `emoji-test.txt` — grapheme decomposer's emoji handling
- `CollationTest.zip` — UCA Super-Fibonacci ordering correctness
- `IdnaTestV2.txt` — if IDNA support is added

These test files are gates, not data. Validate the decomposer against them; do not ingest them as substrate state.

### Optionally substrate-relevant (not in initial roadmap)

- `Unihan.zip` — full CJK Unihan database. Massive (~80K codepoints with dozens of properties each). Ingest if substrate needs deep CJK semantics (Mandarin/Cantonese/Korean/Japanese/Vietnamese readings, traditional ↔ simplified mappings, kangxi index entries).
- `ArabicShaping.txt`, `IndicPositionalCategory.txt`, `IndicSyllabicCategory.txt` — if substrate needs Arabic shaping or Indic syllable composition awareness. Add as incremental UCD coverage.
- `EmojiSources.txt`, `NushuSources.txt`, `TangutSources.txt` — historical/cultural sources; ingest if substrate covers those scripts deeply.

## Implementation note

The substrate's UCD decomposer makes a single streaming pass through `ucd.all.flat.xml` (or alternatively over the parallel `*.txt` files), producing:
1. ~150K codepoint atom entities (with content = LE32(codepoint))
2. ~150K `point4d` physicality rows on S³ (from UCA Super-Fibonacci)
3. ~150K `junc.codepoint_property` rows
4. Variable-count edges (decomposition, case folding, confusables, emoji sequences, brackets, mirroring) — depending on which subset is ingested

Total UCD ingestion is hundreds of thousands of edges, NOT millions. Bounded, fast, deterministic.

The `auxiliary/*Test.txt` and `NormalizationTest.txt` files are loaded into the test harness, NOT the substrate. They run as part of the M2 (UCD/UCA seed) and M3 (text decomposer) gates.

## What was previously hallucinated and is now corrected

The prior version of substrate documentation:
- Listed only `ucd.all.flat.xml` and `allkeys.txt` as UCD inputs.
- Did not mention auxiliary/ test files (which are mandatory for decomposer correctness).
- Did not mention the security/ directory (confusables data — high-value for substrate).
- Did not mention separate emoji files (top-level vs ucd\emoji\).
- Did not mention idna/, charts/, extracted/ subdirectories.
- Did not articulate the difference between substrate content and decomposer-correctness gates.

This document supersedes those gaps. The data-asset-paths reference (`50-reference/04-data-asset-paths.md`) and the implementation roadmap (`40-process/04-implementation-roadmap.md`) reference back here for the full UCD inventory.

## Cross-references

- Verified data asset paths: `50-reference/04-data-asset-paths.md`
- Identity (codepoint atoms as Merkle leaves): `10-architecture/02-identity-and-convergence.md`
- Geometry (UCA Super-Fibonacci → S³): `10-architecture/03-geometry-4d.md`
- Substrate Law 7 (language-agnostic by Unicode): `10-architecture/01-substrate-laws.md`
- Decomposer contract: `10-architecture/05-decomposer-contract.md`
- Implementation roadmap M2 (UCD/UCA seed) and M3 (text decomposer): `40-process/04-implementation-roadmap.md`

## External references

- Unicode Standard, latest version: <https://www.unicode.org/versions/latest/>
- UAX #29 (text segmentation): <https://unicode.org/reports/tr29/>
- UAX #10 (collation algorithm): <https://unicode.org/reports/tr10/>
- UAX #14 (line breaking): <https://unicode.org/reports/tr14/>
- UAX #15 (normalization): <https://unicode.org/reports/tr15/>
- UTS #39 (security): <https://unicode.org/reports/tr39/>
- UTS #46 (IDNA): <https://unicode.org/reports/tr46/>
- UTS #51 (emoji): <https://unicode.org/reports/tr51/>
