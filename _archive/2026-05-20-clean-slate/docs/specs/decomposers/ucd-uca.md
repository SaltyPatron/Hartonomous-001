# UCD/UCA Decomposer Specification

## Identity

- **Decomposer class**: `UcdUcaDecomposer` extends `BaseDecomposer`
- **Source path**: `D:\Models\UCD\Public\UCD\latest\`
- **Trust prior**: Authoritative (Unicode Consortium, version 17.0.0)
- **Provenance**: `unicode-consortium/ucd/17.0.0`
- **Dependency**: Phase 1 (core algebra must exist). This is the FIRST seed decomposer. All subsequent decomposers depend on the entities this one creates.

## What This Decomposer Creates

Tier-0 entities (codepoints) in the entity table, reference table rows for all Unicode property values, and `codepoint_property` junction table entries mapping each codepoint to its properties. Property values (General Category "Lu", Script "Latin", Block "Basic Latin") are **reference table rows**, not entities — shared across all codepoints that have those values via indexed junction table lookups.

Additionally: collation weights, normalization mappings, break properties, emoji sequences, security confusables, IDNA mappings, Unihan readings, named sequences, and the S3 Hopf/Fibonacci projection.

## Source Files and What Each Contains

### Primary: UCD XML (grouped)

**File**: `ucdxml/ucd.all.grouped.xml` (44MB) or `ucdxml/ucd.all.flat.xml` (228MB)

The grouped XML uses `<group>` elements with shared attribute defaults. Each `<char>` element inherits group attributes and overrides specific ones. Contains 108 distinct attributes across groups/chars.

**Group-level attributes (108 total, confirmed from data)**:

Character identity and naming:
- `cp` -- codepoint value (hex)
- `na` -- character name
- `na1` -- Unicode 1.0 name (legacy)
- `name-alias` children with `alias` and `type` (types: abbreviation, control, correction, figment)

General properties:
- `age` -- Unicode version when assigned
- `gc` -- General Category (Lu, Ll, Lt, Lm, Lo, Mn, Mc, Me, Nd, Nl, No, Pc, Pd, Ps, Pe, Pi, Pf, Po, Sm, Sc, Sk, So, Zs, Zl, Zp, Cc, Cf, Cs, Co, Cn)
- `blk` -- Block (ASCII, Latin_Extended_A, CJK_Unified_Ideographs, etc.)
- `sc` -- Script (Latn, Grek, Cyrl, Arab, Deva, Hang, Hani, etc.)
- `scx` -- Script Extensions (multiple scripts)

Numeric properties:
- `nt` -- Numeric Type (None, Decimal, Digit, Numeric)
- `nv` -- Numeric Value

Bidirectional properties:
- `bc` -- Bidi Class (L, R, AL, EN, ES, ET, AN, CS, NSM, BN, B, S, WS, ON, LRE, LRO, RLE, RLO, PDF, LRI, RLI, FSI, PDI)
- `Bidi_M` -- Bidi Mirrored (Y/N)
- `bmg` -- Bidi Mirroring Glyph
- `Bidi_C` -- Bidi Control (Y/N)
- `bpt` -- Bidi Paired Bracket Type (n, o, c)
- `bpb` -- Bidi Paired Bracket

Case mapping:
- `suc` -- Simple Uppercase Mapping
- `slc` -- Simple Lowercase Mapping
- `stc` -- Simple Titlecase Mapping
- `uc` -- Full Uppercase Mapping
- `lc` -- Full Lowercase Mapping
- `tc` -- Full Titlecase Mapping
- `scf` -- Simple Case Folding
- `cf` -- Full Case Folding
- `Cased` -- Is Cased (Y/N)
- `Upper` -- Is Uppercase (Y/N)
- `Lower` -- Is Lowercase (Y/N)
- `CWCF` -- Changes When Casefolded
- `CWCM` -- Changes When Casemapped
- `CWKCF` -- Changes When NFKC Casefolded
- `CWL` -- Changes When Lowercased
- `CWT` -- Changes When Titlecased
- `CWU` -- Changes When Uppercased

Normalization:
- `dt` -- Decomposition Type (none, canonical, font, noBreak, initial, medial, final, isolated, circle, super, sub, vertical, wide, narrow, small, square, fraction, compat)
- `dm` -- Decomposition Mapping
- `CE` -- Composition Exclusion
- `Comp_Ex` -- Full Composition Exclusion
- `NFC_QC` -- NFC Quick Check (Y/N/M)
- `NFD_QC` -- NFD Quick Check (Y/N)
- `NFKC_QC` -- NFKC Quick Check (Y/N/M)
- `NFKD_QC` -- NFKD Quick Check (Y/N)
- `NFKC_CF` -- NFKC Casefold
- `NFKC_SCF` -- NFKC Simple Casefold

Combining:
- `ccc` -- Canonical Combining Class (0-254)

Joining (Arabic/Syriac):
- `jt` -- Joining Type (U, C, T, D, L, R)
- `jg` -- Joining Group (No_Joining_Group, Ain, Alef, Beh, etc.)
- `Join_C` -- Join Control

Line/word/sentence/grapheme break:
- `lb` -- Line Break class
- `GCB` -- Grapheme Cluster Break
- `WB` -- Word Break
- `SB` -- Sentence Break

East Asian:
- `ea` -- East Asian Width (N, Na, A, W, F, H)

Hangul:
- `hst` -- Hangul Syllable Type (NA, L, V, T, LV, LVT)
- `JSN` -- Jamo Short Name

Indic:
- `InSC` -- Indic Syllabic Category
- `InPC` -- Indic Positional Category
- `InCB` -- Indic Conjunct Break

Boolean properties (Y/N each):
- `Alpha` -- Alphabetic
- `OAlpha` -- Other Alphabetic
- `AHex` -- ASCII Hex Digit
- `Hex` -- Hex Digit
- `Dash` -- Dash
- `Dep` -- Deprecated
- `Dia` -- Diacritic
- `DI` -- Default Ignorable
- `ODI` -- Other Default Ignorable
- `Ext` -- Extender
- `ExtPict` -- Extended Pictographic
- `Gr_Base` -- Grapheme Base
- `Gr_Ext` -- Grapheme Extend
- `OGr_Ext` -- Other Grapheme Extend
- `IDC` -- ID Continue
- `OIDC` -- Other ID Continue
- `XIDC` -- XID Continue
- `IDS` -- ID Start
- `OIDS` -- Other ID Start
- `XIDS` -- XID Start
- `IDSB` -- IDS Binary Operator
- `IDST` -- IDS Trinary Operator
- `IDSU` -- IDS Unary Operator
- `ID_Compat_Math_Start` -- ID Compat Math Start
- `ID_Compat_Math_Continue` -- ID Compat Math Continue
- `Ideo` -- Ideographic
- `UIdeo` -- Unified Ideograph
- `LOE` -- Logical Order Exception
- `Math` -- Math
- `OMath` -- Other Math
- `MCM` -- Modifier Combining Mark
- `NChar` -- Noncharacter Code Point
- `OLower` -- Other Lowercase
- `OUpper` -- Other Uppercase
- `Pat_Syn` -- Pattern Syntax
- `Pat_WS` -- Pattern White Space
- `PCM` -- Prepended Concatenation Mark
- `QMark` -- Quotation Mark
- `Radical` -- Radical
- `RI` -- Regional Indicator
- `SD` -- Soft Dotted
- `STerm` -- Sentence Terminal
- `Term` -- Terminal Punctuation
- `VS` -- Variation Selector
- `WSpace` -- White Space

Vertical/orientation:
- `vo` -- Vertical Orientation (R, U, Tu, Tr)

Emoji:
- `Emoji` -- Is Emoji
- `EPres` -- Emoji Presentation
- `EMod` -- Emoji Modifier
- `EBase` -- Emoji Modifier Base
- `EComp` -- Emoji Component

### Supplementary: Unihan XML

**Files**: `ucdxml/ucd.unihan.flat.xml` (53MB), `ucdxml/ucd.unihan.grouped.xml` (37MB)

CJK-specific data NOT fully present in the main XML. Includes:
- **Readings**: kMandarin, kCantonese, kJapaneseKun, kJapaneseOn, kKorean, kVietnamese, kHangul, kTang, kDefinition
- **Radical-stroke**: kRSUnicode, kTotalStrokes, kRSKangXi
- **Variants**: kSimplifiedVariant, kTraditionalVariant, kZVariant, kCompatibilityVariant, kSemanticVariant, kSpecializedSemanticVariant
- **Source references**: kIRG_GSource, kIRG_JSource, kIRG_KSource, kIRG_TSource, kIRG_VSource, etc.
- **Dictionary references**: kKangXi, kMorohashi, kNelson, kHanYu, kDaeJaweon, etc.
- **Encoding**: kBigFive, kCCCII, kCNS1986, kCNS1992, kEACC, kGB0, kGB1, kGB3, kGB5, kGB7, kGB8, kGSR, kJIS0213, kJis0, kJis1, kKPS0, kKPS1, kKSC0, kKSC1, kMainlandTelegraph, kTaiwanTelegraph, kXerox

Each reading/variant/source is a SEPARATE edge, not a column. CJK variants (e.g., simplified→traditional) are edges in the edge table.

### Supplementary: UCA Collation

**File**: `uca/allkeys.txt` (2.3MB, ~40K entries)

Collation Element Table (DUCET). Each entry maps a codepoint (or sequence) to collation weights:
- Primary weight (0200..73C2, 29123 values)
- Secondary weight (0020..0127, 264 values)
- Tertiary weight (0002..001F, 30 values)

Format: `codepoint ; [.primary.secondary.tertiary]`

These weights are critical for the S3 projection -- UCA ordering determines geometric position.

**File**: `uca/decomps.txt` (633KB) -- collation decomposition mappings
**File**: `uca/ctt.txt` (4MB) -- collation test data

### Supplementary: Break Properties

**Files** in `ucd/auxiliary/`:
- `GraphemeBreakProperty.txt` (99KB) -- grapheme cluster break assignments
- `WordBreakProperty.txt` (114KB) -- word break assignments
- `SentenceBreakProperty.txt` (221KB) -- sentence break assignments
- `GraphemeBreakTest.txt` (127KB) -- grapheme break test cases
- `WordBreakTest.txt` (322KB) -- word break test cases
- `SentenceBreakTest.txt` (88KB) -- sentence break test cases
- `LineBreakTest.txt` (3.2MB) -- line break test cases

These define the language-agnostic decomposition rules for text segmentation. The test files validate the implementation.

### Supplementary: Emoji

**Files** in `emoji/`:
- `emoji-sequences.txt` (195KB) -- multi-codepoint emoji sequences (flags, keycaps, etc.)
- `emoji-zwj-sequences.txt` (277KB) -- zero-width-joiner sequences (family, profession, etc.)
- `emoji-test.txt` (669KB) -- complete emoji test/display data

**File** in `ucd/emoji/`:
- `emoji-data.txt` (107KB) -- per-codepoint emoji property assignments
- `emoji-variation-sequences.txt` (38KB) -- variation selector sequences for emoji

### Supplementary: Security

**Files** in `security/`:
- `confusables.txt` (746KB) -- visually confusable character pairs across scripts
- `confusablesSummary.txt` (754KB) -- summary of confusable mappings
- `IdentifierStatus.txt` (148KB) -- identifier restriction status per codepoint
- `IdentifierType.txt` (527KB) -- identifier type classification
- `intentional.txt` (7KB) -- intentionally confusable sequences

### Supplementary: IDNA

**Files** in `idna/`:
- `IdnaMappingTable.txt` (787KB) -- internationalized domain name mapping rules
- `Idna2008.txt` (209KB) -- IDNA 2008 property values

### Supplementary: Other UCD text files NOT in XML

- `Blocks.txt` -- block range definitions
- `CJKRadicals.txt` -- CJK radical mappings
- `CaseFolding.txt` -- case folding rules
- `SpecialCasing.txt` -- context-dependent case mappings
- `CompositionExclusions.txt` -- composition exclusion list
- `DerivedAge.txt` -- version-of-assignment per codepoint
- `DerivedCoreProperties.txt` -- derived property values
- `DerivedNormalizationProps.txt` -- derived normalization properties
- `ArabicShaping.txt` -- Arabic shaping properties
- `BidiBrackets.txt` -- bidi bracket pairs
- `BidiMirroring.txt` -- bidi mirroring mappings
- `EastAsianWidth.txt` -- east Asian width assignments
- `HangulSyllableType.txt` -- Hangul syllable type assignments
- `IndicPositionalCategory.txt` -- Indic positional categories
- `IndicSyllabicCategory.txt` -- Indic syllabic categories
- `Jamo.txt` -- Jamo short names
- `LineBreak.txt` -- line break property values
- `NameAliases.txt` -- character name aliases
- `NamedSequences.txt` -- officially named sequences
- `PropList.txt` -- miscellaneous property assignments
- `PropertyAliases.txt` -- property name aliases
- `PropertyValueAliases.txt` -- property value aliases
- `Scripts.txt` -- script assignments
- `ScriptExtensions.txt` -- script extension values
- `StandardizedVariants.txt` -- standardized variation sequences
- `VerticalOrientation.txt` -- vertical orientation values
- `DoNotEmit.txt` -- characters that should not be emitted
- `NushuSources.txt` -- Nushu character sources
- `TangutSources.txt` -- Tangut character sources
- `USourceData.txt` -- CJK unified ideograph source data
- `Unikemet.txt` -- Egyptian hieroglyph data
- `Index.txt` -- character name index

### Supplementary: Extracted properties

**Files** in `ucd/extracted/`:
- `DerivedBidiClass.txt` -- derived bidi class values
- `DerivedBinaryProperties.txt` -- derived binary properties
- `DerivedCombiningClass.txt` -- derived combining class values
- `DerivedDecompositionType.txt` -- derived decomposition types
- `DerivedEastAsianWidth.txt` -- derived east Asian width
- `DerivedGeneralCategory.txt` -- derived general categories
- `DerivedJoiningGroup.txt` -- derived joining groups
- `DerivedJoiningType.txt` -- derived joining types
- `DerivedLineBreak.txt` -- derived line break properties
- `DerivedName.txt` -- derived character names
- `DerivedNumericType.txt` -- derived numeric types
- `DerivedNumericValues.txt` -- derived numeric values

## Entity Model

Every codepoint becomes a tier-0 entity. Unicode property values populate **reference tables** (not the entity table). Property assignments populate the **`codepoint_property` junction table** for fast indexed lookups. Structural relationships (case mappings, normalization, confusables) become **edges**.

Example for codepoint U+0041 (LATIN CAPITAL LETTER A):

```
-- Entity table row:
entity: hash=BLAKE3(0x0041), entity_type_id→entity_type('codepoint')

-- Reference table rows (populated once, shared across all codepoints):
general_category: code='Lu', group_code='L', description='Letter, uppercase'
script:           code='Latin'
block:            code='Basic_Latin'

-- Junction table entries (fast application-layer lookups):
codepoint_property: entity_id=U+0041, general_category_id→'Lu', script_id→'Latin',
                    block_id→'Basic_Latin', GCB→'XX', WB→'LE', SB→'UP',
                    bidi_class→'L', ea_width→'Na', line_break→'AL',
                    age→'1.1', is_alphabetic=true, is_cased=true,
                    is_grapheme_base=true, is_id_start=true

-- Edges (structural relationships — traversable, significance-weighted):
edge(type='maps_to_lowercase', source=U+0041, target=U+0061)
edge(type='case_folds_to', source=U+0041, target=U+0061)
edge(type='has_name', source=U+0041, target=Entity("LATIN CAPITAL LETTER A"))
edge(type='has_collation_weight', source=U+0041, target=collation_element(0E33,0020,0008))
  -- collation_element is a composition entity (entity_type='collation_element')
  -- composed of primary/secondary/tertiary weight values as number compositions

-- Physicality:
physicality: entity_id=U+0041, type='s3_position',
             geom=POINTZM(x, y, z, m)  -- from UCA collation weight ordering
```

**Reference table rows** (like "Lu", "Latin", "Basic_Latin") are NOT entities. They are small, indexed classification values populated during this phase and read on every subsequent operation. "Lu" is ONE row in `general_category` referenced by every uppercase letter via the `codepoint_property` junction table — a direct indexed JOIN, not graph traversal.

**Edges** are for structural relationships that benefit from significance-weighted traversal: case mappings, normalization chains, confusable pairs, variant relationships.

Boolean properties that are false/N are NOT stored (sparsity law). Only true/Y values are recorded in the junction table.

## S3 Projection

The S3 position for each codepoint is computed from UCA collation weights using the Super-Fibonacci spiral algorithm:

1. Order all assigned codepoints by UCA primary weight (then secondary, then tertiary for ties).
2. Assign each codepoint an index `i` in this ordered sequence.
3. Compute S3 coordinates using Super-Fibonacci:
   - `s = i + 0.5`
   - `r = sqrt(s/n)`
   - `R = sqrt(1.0 - s/n)`
   - `alpha = 2*pi*s / phi` where `phi = sqrt(2)`
   - `beta = 2*pi*s / psi` where `psi = 1.533751168755204288118041`
   - `PointZM(r*sin(alpha), r*cos(alpha), R*sin(beta), R*cos(beta))`

This ensures:
- UCA-adjacent codepoints are S3-adjacent (letters with letters, case pairs nearby, accents near base).
- Even distribution across S3 (no dead zones, Fibonacci property).
- Deterministic (same codepoint -> same position, always).

## Analysis Passes

- `NormalizationValidationPass` -- verify all NFC/NFD/NFKC/NFKD quick-check values against test data
- `BreakPropertyValidationPass` -- verify grapheme/word/sentence break assignments against test files
- `ConfusablesPass` -- extract and store visual confusability relations
- `EmojiSequencePass` -- extract multi-codepoint emoji compositions as substrate entities
- `NamedSequencePass` -- extract officially named sequences as substrate entities
- `CollationWeightPass` -- extract and validate collation weights per codepoint
- `CaseMappingPass` -- validate case mapping chains (upper->lower->fold consistency)

## Completeness Criteria

- Every assigned codepoint in Unicode 17.0.0 has an entity in the entity table.
- All reference tables populated: `general_category` (30 values), `script` (160+ values), `block` (300+ values), `break_property` (all GCB/WB/SB/LB values).
- Every non-default property value for every codepoint has a `codepoint_property` junction table entry.
- All structural relationships (case mappings, normalization, confusables, variants) stored as edges.
- All Unihan readings, variants, and source references are separate edges.
- All collation weights from allkeys.txt are stored.
- All emoji sequences (single, ZWJ, keycap, flag) are compositions in the entity table.
- All security confusables are stored as edges.
- All IDNA mappings are stored as edges.
- All break property assignments are stored in the junction table and validated against test files.
- All named sequences are stored as compositions.
- S3 POINTZM computed for every codepoint via UCA Fibonacci projection.
- ZERO property values stored as entities. All go in reference tables + junction tables.
