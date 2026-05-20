# Unicode + ISO substrate ingestion correction — end-to-end (one delivery)

## Context

Three data tiers — must not conflate:

- **App data** (infrastructure for performance): pre-gen mmap'd blob, reference vocabularies (`entity_type`, `edge_type`, `provenance`, `significance_context`, `attestation_type`, `general_category`, `script`, `block`, `break_property`, `language`, `pos`, `deprel`, `morph_feature`, `lexname`, etc.), per-codepoint property junctions (`codepoint_property`), SQL populate functions for ref vocab + property junctions, bootstrap schema. These make the app run.
- **Substrate data** (the AI's knowledge — content-addressed Merkle DAG with attestations): codepoint entities, multi-cp content sequences (NamedSequences, emoji sequences), semantic edges (case mappings, decompositions, confusables, IDNA, bidi mirroring, Unihan readings, IVD variants), cross-source/cross-version/cross-collection attestation events, ISO cross-link edges (script/region/encoding_position), WordNet/Wiktionary/UD/Tatoeba semantic attestations, model-derived attestation edges. This is what the AI IS.

  **Physicality is ONE table partitioned by physicality_type_id, with three reading conventions per partition:**
  - **entity_atom partition** (codepoint, audio sample, pixel, tensor cell): POINTZM with real content-derived coordinates. For codepoint: S³ position by UCA rank from `hartonomous_ucd_cp_centroid`. Consumer reads the 4 doubles directly as spatial coordinates.
  - **content_trajectory partition** (text_composition, paragraph, document, audio_chunk, audio_recording, pixel_region, video_frame): LINESTRINGZM (or MULTILINESTRINGZM for branching) where every vertex is a mantissa-packed child reference — (X, Z) carry child BLAKE3 hash bits 0-51 / 52-103 via `bb_pack_hash_lo` / `bb_pack_hash_hi`; Y carries `bb_pack_ordinal_rle(ordinal, rle_count)`; M carries packed metadata. Consumer unpacks vertices to recover child entity references; the geometry IS the indexed child manifest — there is no separate `sequence` or `composition_child` table.
  - **embedding_firefly partition** (per-model per-token firefly POINTZM in the 4D jar, attached to word_form entities): POINTZM with real embedding-jar coordinates (Laplacian eigenmap λ₂/λ₃/λ₄ + L2-norm salience), post-Procrustes alignment to canonical anchor frame.

  **Recursive Merkle DAG = recursive LINESTRINGZM through mantissa-packed child refs, bottoming out at modality atom POINTZM.** Moby Dick document → LINESTRINGZM through chapter entities → LINESTRINGZM through paragraph entities → ... → word_form LINESTRINGZM through codepoint entities → codepoint POINTZM with real S³ coords. Same shape for audio (recording → chunk → sample), image (image → pixel_region → pixel), video (video_frame trajectory → pixel_region per frame → pixel). "whale" appearing ~1500 times in Moby Dick = ONE word_form entity referenced from 1500 vertex positions across the document's nested trajectories.
  
  ONE physicality emit per content entity, regardless of how many children it walks through. No separate per-child rows.
- **User data** (session-scoped under `user_session` provenance): prompts (text/image/audio/video) and uploads, decomposed through the same content paths.

Practitioner decides the tier boundary — single-source definitional facts can stay in app data (reference vocab + property junctions); contested / multi-source / cross-version / cross-collection facts go in substrate data with attestation events accumulating per arena.

The substrate's Unicode/ISO foundation is broken end-to-end, on BOTH layers, and the layers were drifting in isolation.

**Pre-gen layer (build-time, `scripts/build/generate_unicode_tables.py`)** parses 13 separate .txt files (UnicodeData.txt, GraphemeBreakProperty.txt, WordBreakProperty.txt, SentenceBreakProperty.txt, LineBreak.txt, emoji-data.txt, DerivedCoreProperties.txt for InCB, Scripts.txt, Blocks.txt, CaseFolding.txt, EastAsianWidth.txt, HangulSyllableType.txt, DerivedNormalizationProps.txt for Full_Composition_Exclusion). The canonical rule (`.claude/rules/00-hartonomous-core.md` and root CLAUDE.md) is that `ucd.all.flat.xml` is the single source for per-codepoint UAX #44 properties; .txt parsing for things flat XML already carries is duplication and drift. Worse, the .txt set the pre-gen reads omits properties that flat XML carries directly: script_extensions (scx), full case mapping (lc/uc/tc/cf), bidi class (bc), bidi mirroring (Bidi_M, bmg), joining type/group (jt/jg), Indic syllabic/positional categories (InSC, InPC), name aliases, age, and the ~70 other UAX #44 attributes. The pre-gen's perf-cache role is legitimate (client-side microsecond mmap'd lookups for `hartonomous_ucd_cp_centroid` / `PhysicalityEmitter.CodepointS3Position` / `substrate.text_decompose` internals), but the input shape is wrong.

**Substrate-ingest layer (runtime, `UcdUcaDecomposer` + 12 `populate_*_from_ext` PG SRFs)** reads from the (wrong-shape) pre-gen blob via 12 thin C# passes that dispatch to PG functions. Every other decomposer in the codebase (WordNet, Wiktionary, UD, Tatoeba — verified via `src/Hartonomous.Decomposers/Wiktionary/WiktionaryDecomposer.cs:153-200`, `Tatoeba/TatoebaDecomposer.cs:83-200`, `WordNet/WordNetDecomposer.cs:94-199`) parses source files directly and emits through `IIngestionPipeline.CreateBatch(provenance) → IIngestionBatch.AddEntity/AddEdge/AddJunction → SubmitBatchAsync(batch, ct)`. UcdUcaDecomposer alone bypasses bulk-pre-dedupe (AP-19), drain-completion priming (AP-37), sign-aware Glicko (AP-31), and the universal cross-source attestation surface (AP-38).

**Substrate extracts semantic facts; source file formats are thrown away.** Per `feedback-no-bit-perfect-export`: the substrate is a consensus surface, not an archive. Decomposers extract the meaning (codepoint properties, case mappings, sense relations, attestation edges) and emit it as substrate entities + edges + junctions. The XML structure, .conllu file layout, IVD file format, Unihan schema, Wiktionary wikitext — none of that is preserved as substrate content. Only the semantic facts survive.

**Coverage gaps that exist because the substrate-ingest layer never had real producers:**
- 18 UCD versions staged at `/vault/Data/Unicode/Public/UCD/{ver}/` (5.2.0 through 17.0.0; spec calls for 30); ucdxml/ucd.all.flat.zip at 17.0.0 only. Cross-version semantic attestation absent — we'd extract per-(cp, property) attestation per version and fire events; the version XML structure is not preserved. `unicode_version_consensus` arena named in CLAUDE.md but not in `significance_context.sql` seed.
- IVD 5 collections (adobe-japan1, hanyo-denshi, krname, moji_joho, msarg) staged at `/vault/Data/Unicode/ivd/` — extract per-(codepoint, collection, variant-glyph-id) attestation; throw away the collection file format.
- Unihan readings (Mandarin/Cantonese/Japanese/Vietnamese) at `/vault/Data/Unicode/Public/UCD/17.0.0/ucdxml/ucd.unihan.flat.zip` — extract per-(codepoint, language, reading) attestation; throw away the Unihan XML.
- ISO 15924 (scripts), ISO 3166 (regions), BCP 47 (language tags), CLDR (per-locale) — absent. `Iso639Decomposer` exists and seeds 7,928 language rows (provenance `sil_international`, mu 95000) but doesn't fire cross-link attestation events on Unicode entities, on scripts, or on regions. Extract the cross-link facts (language↔script, language↔region, BCP47 tag↔component-codes, locale↔codepoint coverage) as edges; throw away the registry file formats.
- Encoding-standard attestation (ASCII / ISO 8859 family / EBCDIC variants / Windows code pages / KOI8 / GB18030 / JIS / etc.) — from-zero greenfield. Substrate's "universal absorbent" property requires multi-encoding attestation on codepoints. Extract `has_encoding_position(codepoint, encoding_standard, byte_seq)` per standard; the encoding spec itself is not preserved.

**Doc drift compounds it:** `docs/00-substrate-spec.md` and `docs/01-tensor-primitive-spec.md` carry 27 phantom `attestation_type` names (`model_attention_qk_pattern`, `model_ffn_full_path`, `model_input_embedding`, `model_lm_head_projection`, `model_moe_router`, `model_moe_expert_response`, `model_lora_adapter_evidence`, `model_position_embedding`, `model_layer_norm_evidence`, etc.) that don't exist in seed. The 2026-05-14 P1d collapse to 3 generic rows (positive_evidence / negative_evidence / neutral_evidence) is done in `sql/schema/seed/attestation_type.sql` but not propagated. Spec also still lists `entity_pos.mu` and `pattern_deprel.mu` as separate Glicko surfaces (00-spec lines 97, 153-154) when the AP-8 correction calls for unified `edge_significance`.

**Intended outcome (one delivery, gated end-to-end):** Pre-gen parses flat XML for everything flat XML carries, with secondary parsers only for what flat XML doesn't cover. Substrate ingestion goes through `IIngestionPipeline` producers for the whole Unicode/ISO/CLDR/encoding-standard family, mirroring the Wiktionary/WordNet/UD/Tatoeba pattern exactly. Per-version + per-collection + per-language + per-encoding cross-source attestation events fire on contested surfaces. Cross-link edges (Unicode ↔ ISO 15924 ↔ ISO 639 ↔ ISO 3166 ↔ BCP 47 ↔ CLDR ↔ encoding standards) emit naturally because every decomposer participates in attestation on entities its source touches. `populate_*_from_ext` substrate-ingestion SRFs retire (reference-vocabulary populate functions stay). Docs propagate the P1d collapse. The whole substrate is queryable for the universal cross-source consensus surface this foundation defines.

**Single delivery — not a phase sequence with stopping points.** The substrate works end-to-end as one system. Pieces of this plan execute in dependency order for buildability, but no portion of this plan is shippable on its own — the substrate either has the corrected Unicode/ISO foundation or it doesn't. The verification gate is the substrate working end-to-end with the full attestation surface populated, not per-phase row-count parity (that's diagnostic, not completion).

---

## Execution sequence (dependency order — all required)

### Step A: Pre-gen rewrite to parse flat XML

The pre-gen (build-time C-blob bake for client-side microsecond lookups) must parse `ucd.all.flat.xml` for all per-codepoint UAX #44 attributes, with secondary parsers for what flat XML doesn't carry. The blob's CONSUMER is legitimate (microsecond mmap'd lookups for query/inference hot paths); the blob's INPUT is currently wrong.

**Edits to `scripts/build/generate_unicode_tables.py`:**

- **Delete** the 13 per-cp .txt parser invocations from `main()` (lines 1627-1654 reference): `parse_unicode_data(UnicodeData.txt)`, `parse_ranged_property(GraphemeBreakProperty.txt)`, `parse_ranged_property(WordBreakProperty.txt)`, `parse_ranged_property(SentenceBreakProperty.txt)`, `parse_ranged_property(LineBreak.txt)`, `parse_ranged_property(emoji-data.txt)`, the inline DerivedCoreProperties.txt InCB parser, `parse_ranged_property(Scripts.txt)`, `parse_case_folding(CaseFolding.txt)`, `parse_ranged_property(EastAsianWidth.txt)`, `parse_ranged_property(HangulSyllableType.txt)`, `parse_codepoint_set_property(DerivedNormalizationProps.txt, "Full_Composition_Exclusion")`
- **Add** `parse_ucd_flat_xml(zip_path)` function that:
  - Opens `<ucd_root>/ucdxml/ucd.all.flat.zip` via `zipfile.ZipFile` and streams the inner `ucd.all.flat.xml` member
  - Iterates with `xml.etree.ElementTree.iterparse(stream, events=('end',))` calling `element.clear()` after each `<char>` to bound memory
  - Handles both `<char cp="...">` per-cp elements and `<char first-cp="..." last-cp="...">` range elements (UAX #42 §4)
  - Handles `<reserved>`, `<noncharacter>`, `<surrogate>` element types for unassigned codepoints
  - Handles the default namespace `xmlns="http://www.unicode.org/ns/2003/ucd/1.0"` (Python ElementTree returns `{http://www.unicode.org/ns/2003/ucd/1.0}char` as the element tag; either strip the namespace prefix or match with the full QName)
  - Extracts every per-cp attribute the script previously got from .txt files PLUS attributes flat XML carries that the .txt parsers missed: `scx` (script extensions), `bc` (bidi class), `Bidi_M` (bidi mirrored), `bmg` (bidi mirroring glyph), `jt` (joining type), `jg` (joining group), `InSC` (Indic syllabic), `InPC` (Indic positional), `age` (Unicode version introduced), `lc`/`uc`/`tc`/`cf` (full case mappings), `Comp_Ex` (full composition exclusion), `NFC_QC`/`NFD_QC`/`NFKC_QC`/`NFKD_QC` (normalization quick checks), `XO_NFC`/`XO_NFD`/`XO_NFKC`/`XO_NFKD` (expanding on normalization), `hst` (Hangul syllable type), `vo` (vertical orientation), `Emoji`/`EPres`/`EMod`/`EBase`/`EComp` (emoji properties), `Cased`/`CI`/`CWL`/`CWU`/`CWT`/`CWCF`/`CWKCF` (case property flags), `na`/`na1` (names), child `<name-alias>` elements (correction/control/alternate/figment/abbreviation aliases)
  - Returns one unified dict-of-dicts `Dict[int, Dict[str, Any]]` indexed by codepoint, plus a derived `full_comp_exclusion: Set[int]` from the `Comp_Ex` attribute
- **Edit** `main()` to call `parse_ucd_flat_xml(ucd_root / 'ucdxml' / 'ucd.all.flat.zip')` once, replacing the 13 per-cp `.txt` parser calls. Subsequent dict-shaped consumers (`udata.get(cp, {}).get(...)`, `gcb_map.get(cp)`, etc.) read from the unified dict
- **Keep** secondary parsers for what flat XML doesn't carry: `parse_uca_allkeys(uca/allkeys.txt)`, `parse_named_sequences(NamedSequences.txt)`, `parse_emoji_sequences(emoji-sequences.txt)`, `parse_emoji_zwj_sequences(emoji-zwj-sequences.txt)`, `parse_standardized_variants(StandardizedVariants.txt)`, `parse_confusables(security/confusables.txt)`, `parse_idna_mapping(security/IdnaMappingTable.txt)`, `parse_cjk_radicals(CJKRadicals.txt)`, `parse_blocks(Blocks.txt)` — flat XML's per-cp `blk` attribute would aggregate to the same data but Blocks.txt explicitly lists the ranges and is simpler to keep
- **Update** the `_default_ucd_root()` documentation comment + emitted C code generation paths if attribute names differ
- **Update** the script header docstring (lines 1-39) to reflect XML-as-canonical-source

**Verify:** existing pre-gen byte-identical output for the subset of properties both paths cover (run the new path, diff `ext/hartonomous_pg/src/generated/pg_unicode_props.c` against the previous version on the properties that overlap); NEW properties (script_extensions, full case mappings, bidi mirroring, Indic categories, age, name aliases, full emoji properties) populate the generated tables.

### Step B: Doc cleanup — propagate P1d 27→3 attestation_type collapse

Stale spec references mislead every future session. Land doc cleanup so the new code grounds against accurate spec.

| File | Line(s) | Change |
|---|---|---|
| `docs/01-tensor-primitive-spec.md` | 119 (§III.1 example) | Rewrite to use `positive_evidence`/`negative_evidence`/`neutral_evidence`. Remove `model_attention_qk_pattern`, `model_ffn_full_path`, `model_input_embedding`, `model_lm_head_projection`, `model_moe_router`, `model_moe_expert_response`, `model_lora_adapter_evidence`, `model_position_embedding` name list. Note: "score = value > 0 ? 1.0 : 0.0; weight = abs(value); provenance discriminates source; arena discriminates domain. Kind-of-evidence metadata (per-layer-type, per-tuple-slot) lives on `EdgeRatingEvent` attribution fields (`PrimitiveCode`, `TupleCode`, `SlotCode`, `ModelSourceId`, `TensorHash`), not on `attestation_type`." |
| `docs/01-tensor-primitive-spec.md` | 327-353 (§IV table) | Replace each row's attestation_type column with `positive_evidence` (or `negative_evidence` for inherently sign-bearing rows). Add new column "EdgeRatingEvent metadata" listing `(PrimitiveCode, TupleCode, SlotCode)` per row |
| `docs/01-tensor-primitive-spec.md` | 411-413 (§V code block comments) | Same phantom-attestation-name cleanup |
| `docs/01-tensor-primitive-spec.md` | 464-477 (§IX.1) | Mark ✅ COMPLETE; note P1d (2026-05-14) collapsed 27→3; the 5 new types §IX.1 originally proposed move to EdgeRatingEvent attribution fields, not new attestation_type rows |
| `docs/00-substrate-spec.md` | 97 | Remove "Glicko-2 confidence on junction-bearing classifications lives on `entity_pos.mu` and `pattern_deprel.mu`" sentence. Note unified-edge_significance per P1g; junction `mu` columns are transitional analytics caches |
| `docs/00-substrate-spec.md` | 119 (§III.1) | Same phantom-attestation-name cleanup as 01-spec line 119 |
| `docs/00-substrate-spec.md` | 145-164 (§IV four-surfaces table) | Reduce to two surfaces: `entity_significance` + `edge_significance`. Note `entity_pos.mu`/`pattern_deprel.mu` as transitional analytics caches |
| `docs/specs/csharp/decomposers.md` | 256 | Replace phantom attestation_type names |
| `docs/specs/decomposers/safetensors.md` | 352, 358 | Replace phantom attestation_type names |

**Verify:** `grep -rn "model_attention_qk_pattern\|model_ffn_full_path\|model_input_embedding\|model_lm_head_projection\|model_moe_router\|model_moe_expert_response\|model_lora_adapter_evidence\|model_position_embedding\|model_layer_norm_evidence" docs/ --include="*.md" | grep -v "phantom\|deprecated\|removed\|stale\|historical"` returns zero hits.

### Step C: Seed expansion (additions only — no deletions yet)

| File | Change |
|---|---|
| `sql/schema/seed/significance_context.sql` | ADD 8 arenas (one INSERT row each): `unicode_version_consensus` (named in CLAUDE.md), `encoding_position_consensus`, `ivd_collection_consensus`, `unihan_reading_consensus`, `consortium_discussion_density`, `script_membership_consensus`, `language_codepoint_coverage_consensus`, `locale_definition_consensus`. Each corresponds to a concrete contested surface a decomposer in this plan emits events on |
| `sql/schema/seed/edge_type.sql` | ADD `cross_lingual` category edges: `has_iso_639_1_code` (language_name→text_composition), `has_iso_639_2b_code` (language_name→text_composition), `has_iso_639_2t_code` (language_name→text_composition), `has_script` (language_name→text_composition where target is 4-letter ISO 15924 script identifier), `has_region` (language_name→text_composition where target is ISO 3166-1 alpha-2 code). ADD `unicode` category edges: `has_encoding_position` (codepoint→text_composition where target is byte-sequence in encoding's space), `has_ideographic_variant_in_collection` (codepoint→text_composition where target carries variant glyph identifier + collection name). ADD `structural` category edges for the AP-8 unified-Glicko-surface migration: `has_pos` (word_form→text_composition where target is POS category name like "NOUN" / "VERB" / "ADJ" — replaces `entity_pos.mu` separate junction Glicko surface), `has_morph_feature` (word_form→text_composition where target is morph feature like "Gender=Masc" / "Number=Sing" — replaces `entity_morph_feature` junction Glicko), `has_deprel_pattern` (word_form→text_composition where target is dependency relation name like "nsubj" / "obj" / "amod" — replaces `pattern_deprel.mu` junction Glicko), `has_lexname` (synset→text_composition where target is WordNet lexname like "noun.animal" / "verb.motion" — replaces `entity_lexname` junction Glicko), `has_language` (entity→language_name where the entity asserts a language tag — replaces `entity_language` junction). Use existing INSERT...SELECT pattern (lines 35-203) joining to `substrate.entity_type` by code |
| `sql/schema/seed/significance_context.sql` (continued) | Reuse existing arenas for AP-8 migration: POS attestations fire on `syntactic_role_fitness`; morph features fire on `morphological_productivity`; deprel patterns fire on `syntactic_role_fitness`; lexname fire on `semantic_relevance`; language tags fire on `corroboration_strength`. No new arenas needed for the unified-surface migration |
| `sql/schema/seed/provenance.sql` | ADD provenance rows: `iso_15924` (mu 95000), `iso_3166` (mu 95000), `ietf_bcp47` (mu 90000), `cldr` (mu 70000 per CLAUDE.md), and one per encoding standard: `ascii`, `iso_8859_1` through `iso_8859_16`, `windows_1250` through `windows_1258`, `ebcdic_037`, `ebcdic_500`, `ebcdic_1047`, `koi8_r`, `koi8_u`, `gb18030`, `jis_x_0201`, `jis_x_0208`, `jis_x_0212`, `shift_jis`, `euc_jp`, `euc_kr`, `big5`, `mac_roman` (mu calibrated per standard's authority — Unicode-Consortium-grade ISO standards at 90000-95000, vendor-specific at 60000-70000) |

**Verify:** `scripts/hart build extension-sql` succeeds; new arena/edge_type/provenance rows visible in generated SQL.

### Step D: Native binding for C# decomposer xml_pull access

Per the parallel pre-gen rewrite (Python), the C# decomposers also need to stream `ucd.all.flat.xml`. Use the existing `ext/libhartonomous/codegen/xml_pull.c` parser (header at `xml_pull.h:1-94`) — verify default-namespace behavior (the parser explicitly returns qualified names as-is per its own scope statement), then either ignore the namespace declaration in C# wrapper code or extend xml_pull with a namespace-strip mode.

**Files created/edited:**
- New: `src/Hartonomous.Core/Native/XmlPullNative.cs` — P/Invoke binding to `xml_pull_init`, `xml_pull_next`, `xml_pull_attr` mirroring `Blake3Native.cs` pattern
- New: `src/Hartonomous.Core/Text/Ucd/UcdFlatXmlReader.cs` — opens `ucd.all.flat.zip` via `System.IO.Compression.ZipArchive`, streams the inner XML through the native parser, yields `CodepointRecord` structs holding per-cp UAX #44 attributes (same shape the rewritten pre-gen produces)
- New: `src/Hartonomous.Core/Text/Ucd/CodepointRecord.cs` — record struct carrying all per-cp UAX #44 fields

**Verify:** xUnit test in `tests/Hartonomous.Core.Tests/Text/UcdFlatXmlReaderTests.cs` parses a small UCD sample and asserts known properties: U+0041 has gc=Lu, sc=Latn, ccc=0, age=1.1, bc=L, ea=A, lb=AL, gcb=Other, wb=ALetter; U+0301 (combining acute) has gc=Mn, ccc=230; U+1F600 (grinning face) has Emoji=Y, EPres=Y, ExtPict=Y.

### Step E: Rewrite 13 UcdUcaDecomposer passes as real producers

Replace each of the 13 thin `populate_*_from_ext`-dispatching passes at `src/Hartonomous.Decomposers/Ucd/*.cs` with producer passes that mirror the Wiktionary/WordNet/UD/Tatoeba pattern.

**Pattern per pass** (verified against `Wiktionary/WiktionaryDecomposer.cs:153-200` + `Tatoeba/TatoebaDecomposer.cs:83-200`):

```csharp
async Task RunAsync(UnicodePassContext ctx, CancellationToken ct) {
    IIngestionBatch batch = ctx.Pipeline.CreateBatch("unicode_consortium");
    await ParallelChunkProcessor.RunAsync(
        source: streamFromSource,
        processChunk: async (chunk, innerCt) => {
            HashSet<HashKey> existing = await ctx.Pipeline.GetExistingEntityHashesAsync(chunk.CandidateHashes, innerCt);
            foreach (var record in chunk) {
                if (existing.Contains(record.Hash)) continue;
                EntityHandle handle = batch.AddEntity(record.Hash, "codepoint",
                    record.CentroidX, record.CentroidY, record.CentroidZ, record.CentroidM, record.HilbertIndex);
                batch.AddPhysicality(handle, "codepoint_atom", record.GeometryPayload);
                batch.AddJunction("codepoint_property", handle, propertyId, mu: null, "positive_evidence");
                // edges as appropriate
                if (batch.EntityCount >= 25_000 || batch.EdgeCount >= 25_000) {
                    await ctx.Pipeline.SubmitBatchAsync(batch, innerCt);
                    batch = ctx.Pipeline.CreateBatch("unicode_consortium");
                }
            }
        },
        degreeOfParallelism: ParallelChunkProcessor.DefaultDegreeOfParallelism(),
        ct: ct);
    await ctx.Pipeline.SubmitBatchAsync(batch, ct);
}
```

**Per-pass new source + emission table:**

| Pass | New source | Emits |
|---|---|---|
| `CodepointAtomPass` | `UcdFlatXmlReader` over `ucd.all.flat.xml` | `substrate.entity(codepoint)` + `entity_classification` + `physicality(POINTZM)` using S³-by-UCA-rank position from `hartonomous_ucd_cp_centroid` (LEGITIMATE perf-cache use at runtime — the blob is queried for the centroid value, the substrate row is emitted via batch) |
| `CodepointPropertyPass` | Same XML stream, single pass over the unified dict | `codepoint_property` junction rows per UAX #44 attribute per cp (covers gc, ccc, lb, gcb, wb, sb, ea, hst, bc, jt, jg, InSC, InPC, age, scx, etc. — every per-cp attribute, including the ~10 the .txt path was missing) |
| `UnicodeCaseEdgePass` | Same XML stream — `lc`, `uc`, `tc`, `scf` attributes | `maps_to_lowercase`, `maps_to_uppercase`, `maps_to_titlecase`, `case_folds_to` edges with `LINESTRINGZM` participant trajectories |
| `UnicodeDecompositionEdgePass` | Same XML stream — `dt`, `dm` attributes | `has_canonical_decomposition`, `has_compatibility_decomposition` (codepoint→text_composition); `canonical_composes_to` for canonical-2-element decompositions with `Comp_Ex=N` |
| `UnicodeFullCaseMappingEdgePass` | `SpecialCasing.txt` (multi-cp + locale-conditional) — NOT in flat XML as a separate surface; flat XML carries `lc`/`uc`/`tc` simple maps via attributes, SpecialCasing.txt has the conditional + multi-cp variants | `has_full_case_mapping` (codepoint→text_composition) with locale condition metadata on edge attribute |
| `UnicodeConfusablePass` | `security/confusables.txt` (UTS #39) | `confusable_with` edges (text_composition pairs) |
| `UnicodeStandardizedVariantPass` | `StandardizedVariants.txt` + `emoji/emoji-variation-sequences.txt` | `has_standardized_variant` edges (codepoint→text_composition) |
| `UnicodeRadicalStrokePass` | `CJKRadicals.txt` + Unihan `kRSUnicode` (from `ucd.unihan.flat.xml`) | `has_radical_stroke` edges (codepoint→text_composition) |
| `UnicodeNamedSequencePass` | `NamedSequences.txt` | `has_named_sequence` edges (text_composition→text_composition) |
| `UnicodeEmojiSequencePass` | `emoji/emoji-sequences.txt` + `emoji-zwj-sequences.txt` | `has_emoji_sequence` + `has_emoji_zwj_sequence` edges + multi-cp content trajectories |
| `UnicodeReferenceVocabularyPass` | Same XML stream | Reference table inserts: `general_category`, `script`, `block`, `break_property` rows. Keep current C# logic; just point it at the unified dict instead of multiple .txt parsers |
| `ExtensionCatalogVerificationPass` | n/a | Keep as-is (verifies extension blob loaded) |
| `UnicodeMaterializationValidationPass` | Post-run count assertions | Update expected counts to match new producer outputs |

**Files created:**
- `src/Hartonomous.Decomposers/Ucd/Sources/SpecialCasingReader.cs`
- `src/Hartonomous.Decomposers/Ucd/Sources/ConfusablesReader.cs`
- `src/Hartonomous.Decomposers/Ucd/Sources/StandardizedVariantsReader.cs`
- `src/Hartonomous.Decomposers/Ucd/Sources/NamedSequencesReader.cs`
- `src/Hartonomous.Decomposers/Ucd/Sources/EmojiSequencesReader.cs`
- `src/Hartonomous.Decomposers/Ucd/Sources/CjkRadicalsReader.cs`

**Files edited:**
- Each of 10 `*Pass.cs` files in `src/Hartonomous.Decomposers/Ucd/` (replace `UnicodeSql.Populate*Async(connection, ct)` with parse-and-emit body)
- `src/Hartonomous.Decomposers/Ucd/UnicodePassContext.cs` — add `IIngestionPipeline Pipeline { get; }` and `string SourceDirectory { get; }`; remove the long-lived `NpgsqlConnection` field (each batch creates its own short-lived connections through the pipeline)
- `src/Hartonomous.Decomposers/Ucd/UcdUcaDecomposer.cs` — `DecomposeCoreAsync` passes `pipeline` to passes via `UnicodePassContext`

### Step E.5: AP-8 unified-Glicko-surface migration (data + decomposers + inference paths)

The substrate's authoritative classification consensus must live on `substrate.edge_significance`, NOT on separate junction Glicko surfaces (`entity_pos.mu`, `pattern_deprel.mu`, `entity_morph_feature` Glicko, `entity_lexname` Glicko, `entity_language` Glicko). Per `[[feedback-unified-glicko-surface]]`: AI model attestations on POS / morph / deprel claims must compete with corpus attestations on the SAME Glicko ladder; separate junction Glicko ladders fragment cross-source consensus.

**Three pieces, all required for end-to-end correctness:**

#### E.5a — Reference category content-hashed substrate entities

POS / morph_feature / deprel / lexname / language category reference rows need substrate.entity rows to serve as edge targets. The reference table stays for app-side fast lookup; each reference row also has a corresponding substrate.entity (text_composition with content_hash = BLAKE3 of the category code/name).

New SQL function `substrate.seed_reference_category_entities()` runs once after Step C seed expansion:
- For each row in `substrate.pos`: emit `substrate.entity(BLAKE3(code), 'text_composition')` + classification under `pos_taxonomy` provenance
- Same for `substrate.morph_feature`, `substrate.deprel`, `substrate.lexname`, `substrate.language` (language already partially seeded by Iso639Decomposer; verify)
- All these reference-category entities live in substrate.entity AND in their reference tables. The reference table id → substrate.entity hash mapping is recorded in a lookup table (`substrate.reference_category_hash_map(reference_table_text, reference_id, entity_hash)`) for fast resolution

New SQL function file: `sql/schema/functions/seed_reference_category_entities.sql`

#### E.5b — One-time data migration from junction Glicko to edge Glicko

After the new edge types land in seed and reference-category entities exist, migrate every existing junction Glicko row to the unified edge surface. Single SQL migration function `substrate.migrate_junction_glicko_to_edge_significance()`:

```sql
-- entity_pos → has_pos edges + edge_significance
INSERT INTO substrate.edge (edge_type_id, hash, geom, provenance_id)
SELECT 
    (SELECT id FROM substrate.edge_type WHERE code = 'has_pos'),
    substrate.compute_edge_hash('has_pos', ARRAY[ep.entity_hash, rcm.entity_hash]),
    NULL,  -- geom filled at drain
    ep.provenance_id
FROM substrate.entity_pos ep
JOIN substrate.reference_category_hash_map rcm 
  ON rcm.reference_table_text = 'pos' AND rcm.reference_id = ep.pos_id
ON CONFLICT DO NOTHING;

-- corresponding edge_member rows
INSERT INTO substrate.edge_member ...;

-- corresponding edge_significance rows carrying the existing mu/sigma/volatility
INSERT INTO substrate.edge_significance 
    (context_type_id, edge_type_id, edge_hash, mu, sigma, volatility, games)
SELECT 
    (SELECT id FROM substrate.significance_context WHERE code = 'syntactic_role_fitness'),
    (SELECT id FROM substrate.edge_type WHERE code = 'has_pos'),
    substrate.compute_edge_hash(...),
    ep.mu, ep.sigma, ep.volatility, ep.games
FROM substrate.entity_pos ep
JOIN substrate.reference_category_hash_map rcm ON ...
ON CONFLICT DO NOTHING;
```

Repeat for `pattern_deprel` → `has_deprel_pattern`, `entity_morph_feature` → `has_morph_feature`, `entity_lexname` → `has_lexname`, `entity_language` → `has_language`.

After migration: the junction tables remain as analytics caches (denormalized for index-locality on frequent classification-lookup queries per spec §X.1) but are no longer authoritative — substrate truth is on `edge_significance`.

New SQL function file: `sql/schema/functions/migrate_junction_glicko_to_edge_significance.sql`

#### E.5c — Decomposer + inference path rewrites

**Decomposers** that currently call `batch.AddJunction(...)` for POS / morph / deprel / lexname / language classifications must switch to `batch.AddEdge(...)` with the new edge types and per-arena `EdgeSignificanceSpec` + sign-aware `EdgeRatingEvent`:

| Decomposer | File | Change |
|---|---|---|
| `WordNetDecomposer` | `src/Hartonomous.Decomposers/WordNet/WordNetDecomposer.cs:94-199` | Replace `batch.AddJunction("entity_lexname", ...)` with `batch.AddEdge("has_lexname", "wordnet", members, [new EdgeSignificanceSpec("semantic_relevance", "positive_evidence", initialMu)], [new EdgeRatingEvent(...)])`. Same shape for POS junction → has_pos edge. Same shape for any sense-related junction calls |
| `WiktionaryDecomposer` | `src/Hartonomous.Decomposers/Wiktionary/WiktionaryDecomposer.cs:153-200` | Replace `batch.AddJunction("entity_pos", ...)` with `batch.AddEdge("has_pos", ...)`; same for `entity_language` → `has_language`, `entity_morph_feature` → `has_morph_feature` |
| `UniversalDependenciesDecomposer` | (wherever it lives — verify path) | Replace per-token POS junction emit with `has_pos` edge emit; per-token morph feature junctions with `has_morph_feature` edges; per-token deprel pattern with `has_deprel_pattern` edge |
| `TatoebaDecomposer` | `src/Hartonomous.Decomposers/Tatoeba/TatoebaDecomposer.cs:83-200` | Replace `entity_language` junction with `has_language` edge per sentence |
| `Iso639Decomposer` | `src/Hartonomous.Decomposers/Iso/Iso639Decomposer.cs` | Reference vocabulary seed stays; the language entities it produces become substrate.entity rows via Step E.5a; verify any `entity_language` junction emits also go to `has_language` edges |

**Inference paths** that currently consult `entity_pos.mu` / `pattern_deprel.mu` / `entity_lexname.mu` / `entity_morph_feature.mu` / `entity_language.mu` must switch to consulting `substrate.edge_significance` filtered by the corresponding edge_type + arena. Grep for `entity_pos\.mu\|pattern_deprel\.mu\|entity_lexname\.mu\|entity_morph_feature\.mu\|entity_language\.mu` across `src/Hartonomous.Engine/`, `src/Hartonomous.Api/`, `sql/schema/functions/` and rewrite each call site to the unified surface. The junction `.mu` columns remain populated (as analytics cache) but new code paths read from `edge_significance`.

**Verification:** post-migration SQL assertion: every `entity_pos` row has a corresponding `has_pos` edge + `edge_significance` row in `syntactic_role_fitness` arena with matching mu/sigma/games; same for every other migrated junction. Inference path tests that previously consulted `entity_pos.mu` return the same answers via the unified surface (within Glicko-update precision).

---

### Step F: New Unicode passes for currently-absent scope

Add producer passes for substrate scope that has NO existing populate function:

| New pass | Source | Emits |
|---|---|---|
| `UcdVersionDifferencingPass` | 18 versions of `ucd.all.flat.xml` (confirm staging at `/vault/Data/Unicode/Public/UCD/{ver}/ucdxml/` per `ls` output) | For each (codepoint, property): one `EdgeRatingEvent` per version into `unicode_version_consensus` arena. ~18 events per cp per property (with sigma tightening for stable properties, widening for version-revised). Note in pass: 30 versions named in spec, 18 staged — flag the gap in `monitor` table for the practitioner to address |
| `IvdPerCollectionPass` | `/vault/Data/Unicode/ivd/{adobe-japan1, hanyo-denshi, krname, moji_joho, msarg}/` per-collection identifier mapping files | `has_ideographic_variant_in_collection` edges (codepoint→text_composition); 5 distinct provenances fire `positive_evidence` events on `ivd_collection_consensus` arena |
| `UnihanReadingPass` | `/vault/Data/Unicode/Public/UCD/17.0.0/ucdxml/ucd.unihan.flat.zip` — extracts `kMandarin`, `kCantonese`, `kJapanese`, `kVietnamese` per CJK cp via xml_pull | `unihan_reading` edges (already in seed as id 110: codepoint→text_composition); per-language provenance fires events on `unihan_reading_consensus` arena |
| `L2WorkingDocsPass` | `/vault/Data/Unicode/L2/` — confirm directory structure (expect per-doc PDF/HTML/text content + topic metadata). SCOPE: initial pass emits doc-topic edges from filename/metadata patterns; full text content extraction is documented-as-followup in the monitor table (not in this plan's scope to fully implement L2 content extraction, but the pass exists and at least topic-metadata edges fire) | `has_topic` edges (text_composition→codepoint, doc-as-content → codepoint-discussed-in-doc); events on `consortium_discussion_density` arena |

**Files created:**
- `src/Hartonomous.Decomposers/Ucd/UcdVersionDifferencingPass.cs`
- `src/Hartonomous.Decomposers/Ucd/IvdPerCollectionPass.cs`
- `src/Hartonomous.Decomposers/Ucd/UnihanReadingPass.cs`
- `src/Hartonomous.Decomposers/Ucd/L2WorkingDocsPass.cs`
- Edit `src/Hartonomous.Decomposers/Ucd/UcdUcaDecomposer.cs::CreatePasses()` to append these 4 passes

### Step G: ISO 15924 + ISO 3166 + BCP 47 + CLDR decomposers

Mirror the existing `Iso639Decomposer` shape exactly (single-file decomposer at `src/Hartonomous.Decomposers/Iso/`):

| New decomposer | Source | Emits |
|---|---|---|
| `Iso15924Decomposer` | `/vault/Data/Unicode/iso15924/iso15924.txt` (4-letter script codes + names + numeric IDs) | Reference table `substrate.script` (extend if needed); `substrate.entity(text_composition)` for each script name; `has_script` edges between codepoint entities (via UCD `sc` attribute already extracted in Step E) and script_name entities. Fire events on `script_membership_consensus` arena |
| `Iso3166Decomposer` | `/vault/Data/ISO639/` (carries ISO 3166 alongside?) else `/vault/Data/Unicode/cldr/common/supplemental/territoryInfo.xml` if CLDR staged | Region entities (text_composition with classification); `has_region` edges between language_name entities and region text_composition entities |
| `Bcp47Decomposer` | `/vault/Data/Unicode/cldr/common/bcp47/` or IANA language subtag registry (confirm staging at runtime) | Per registered tag: `has_iso_639_1_code`, `has_iso_639_2b_code`, `has_iso_639_2t_code`, `has_script`, `has_region` edges between language_name entities and code/script/region entities. Events on `locale_definition_consensus` arena |
| `CldrDecomposer` | `/vault/Data/Unicode/cldr/common/main/{locale}.xml` per-locale | Per-locale: `exemplarCharacters` → events on `language_codepoint_coverage_consensus` per (locale, codepoint); collation/casing rules per-locale → events on `locale_definition_consensus` |

**Files created:**
- `src/Hartonomous.Decomposers/Iso/Iso15924Decomposer.cs`
- `src/Hartonomous.Decomposers/Iso/Iso3166Decomposer.cs`
- `src/Hartonomous.Decomposers/Iso/Bcp47Decomposer.cs`
- `src/Hartonomous.Decomposers/Cldr/CldrDecomposer.cs`
- Edit `src/Hartonomous.Core/Orchestration/Phase.cs` (lines 3-24) — add `Iso15924`, `Iso3166`, `Bcp47`, `Cldr` phase enum values

### Step H: Encoding-standard decomposers

Greenfield work. One small decomposer per encoding standard, each carrying a static mapping table embedded as data in the decomposer (the encoding's spec is fixed):

| Encoding | Mappings | Decomposer |
|---|---|---|
| ASCII | 128 | `AsciiEncodingDecomposer` |
| ISO 8859-1 through 8859-16 (15 active variants) | 256 each | `Iso8859{1..16}EncodingDecomposer` |
| Windows-1250 through 1258 (9 codepages) | 256 each | `Windows125{0..8}EncodingDecomposer` |
| EBCDIC variants (037, 500, 1047) | 256 each | `Ebcdic{037,500,1047}EncodingDecomposer` |
| KOI8-R, KOI8-U | 256 each | `Koi8{R,U}EncodingDecomposer` |
| GB18030 | ~70k | `Gb18030EncodingDecomposer` (data-table loaded from staged GB18030 spec table) |
| JIS X 0201, 0208, 0212 | Variable | `Jis{0201,0208,0212}EncodingDecomposer` |
| Shift_JIS, EUC-JP | Variable | `{ShiftJis,EucJp}EncodingDecomposer` |
| EUC-KR, Big5 | Variable | `{EucKr,Big5}EncodingDecomposer` |
| MacRoman | 256 | `MacRomanEncodingDecomposer` |

Each: provenance code per standard, emits `has_encoding_position` edges (codepoint→text_composition with byte-sequence content), fires events on `encoding_position_consensus` arena.

**Files created:** ~30 small decomposers under `src/Hartonomous.Decomposers/Encoding/`. Each is short (~50-300 lines, table-dominated).

### Step I: Cross-link attestation in existing text decomposers

Edit existing text-bearing decomposers to fire cross-link attestation events on entities they touch:

| Decomposer | New emissions |
|---|---|
| `WordNetDecomposer` (`src/Hartonomous.Decomposers/WordNet/WordNetDecomposer.cs:94-199`) | Per per-language wordnet: `has_language(lemma, language_name)` events; codepoint-usage events on `language_codepoint_coverage_consensus` arena |
| `WiktionaryDecomposer` (`src/Hartonomous.Decomposers/Wiktionary/WiktionaryDecomposer.cs:153-200`) | `has_language` per entry's language tag; codepoint-usage events; cross-lingual translation events (partially already present per audit) |
| `UniversalDependenciesDecomposer` | Per-treebank `has_language`; per-token codepoint-usage events |
| `TatoebaDecomposer` (`src/Hartonomous.Decomposers/Tatoeba/TatoebaDecomposer.cs:83-200`) | Codepoint-usage per sentence; `language_codepoint_coverage_consensus` events |
| `SafetensorsDecomposer` tokenizer pass | Per model's tokenizer vocab: codepoint-usage events for each vocab string's codepoints; `has_language` if model metadata declares target languages |

### Step J: Retire populate_*_from_ext substrate-ingestion SRFs

After Steps A-I land and the new producer passes pass verification, retire the substrate-ingestion SRFs (keep the reference-vocabulary populate functions):

| populate_* function | Disposition |
|---|---|
| `populate_codepoint_atoms_chunk` | RETIRE |
| `populate_codepoint_property_range_from_ext` | RETIRE |
| `populate_unicode_case_edges_from_properties` | RETIRE |
| `populate_unicode_decomposition_edges_from_ext` | RETIRE |
| `populate_unicode_full_case_mapping_edges_from_ext` | RETIRE |
| `populate_unicode_confusables_from_ext` | RETIRE |
| `populate_unicode_standardized_variants_from_ext` | RETIRE |
| `populate_unicode_radical_stroke_from_ext` | RETIRE |
| `populate_unicode_named_sequences_from_ext` | RETIRE |
| `populate_unicode_emoji_sequences_from_ext` | RETIRE |
| `populate_blocks_from_ext` | KEEP (reference vocabulary) |
| `populate_break_properties_from_ext` | KEEP (reference vocabulary) |
| `populate_general_categories_from_ext` | KEEP (reference vocabulary) |
| `populate_scripts_from_ext` | KEEP (reference vocabulary; reconsider once Iso15924Decomposer is in place) |

**Edits:**
- `src/Hartonomous.Core/Data/SubstrateFunctionNames.cs` — remove the 10 retired function constants from the Allowlist (lines 96-99 reference these via `PopulateUnicode*` constants; verify each)
- `sql/schema/bootstrap.sql` — remove the 10 corresponding `@include schema/functions/populate_unicode_*.sql` lines
- Delete the 10 .sql files from `sql/schema/functions/`

### Step K: End-to-end verification

The single gate. All of the above must be done before this passes:

```bash
dotnet build Hartonomous.slnx -c Debug -nologo
scripts/hart build extension-sql
scripts/hart preflight
scripts/hart db reset
scripts/hart seed phases --source /vault/Data
scripts/hart ops status
./RunAll.sh --source /vault/Data --with-synth
```

Must return all-green for `./RunAll.sh`. Synth step recomposes a tiny target (e.g., MiniLM-base via `--synth-template minilm-base --synth-vocab 256`), confirms output safetensors loads in HF transformers and produces sensible logits on a known prompt.

**Content reconstruction property gate** — proves the substrate's tree-walk-recomposition surface works end-to-end via ONE QUERY:

Ingest a known long-form content document (Moby Dick from /vault/Data or a Bible plain-text file) through the standard text-decomposition path. The document becomes one content entity with nested LINESTRINGZM physicality through chapters → paragraphs → sentences → word_forms → codepoints. Then recompose from the document's content hash alone via a single PG round-trip:

```bash
scripts/hart recompose-content --hash <document_hash> --out /tmp/reconstructed.txt
# Total wall time must be < 1 second for a 200K-word document via ONE SQL function call
# Output bytes must match the ingested content at the codepoint sequence level
```

**Holistic stack implementation — C heavy lifting + SQL host + C# one-line orchestration:**

New C extension function at `ext/hartonomous_pg/src/pg_recompose_content.c`:

```c
PG_FUNCTION_INFO_V1(hartonomous_recompose_content);
Datum hartonomous_recompose_content(PG_FUNCTION_ARGS) {
    bytea *doc_hash = PG_GETARG_BYTEA_P(0);
    // 1. SPI bulk-probe physicality for document hash
    // 2. Iterate LINESTRINGZM vertices via zero-copy geometry access
    // 3. Mantissa-unpack each vertex via bb_unpack_hash_lo/bb_unpack_hash_hi
    // 4. Bulk SPI probe substrate.entity_by_hash_prefix composite btree
    //    — ONE query per tier, regardless of tier width
    // 5. Recurse into each child's content_trajectory physicality if present
    // 6. At codepoint leaves: resolve UTF-8 via hartonomous_ucd_cp_* against
    //    the mmap'd pre-gen blob (direct memory access, zero allocation)
    // 7. Append leaf bytes in ordinal_rle order via Y-mantissa unpack
    // 8. Return assembled bytes as bytea
    PG_RETURN_BYTEA_P(result);
}
```

SQL function exposes it: `CREATE FUNCTION substrate.recompose_content(hash substrate.hash_value) RETURNS bytea LANGUAGE c AS 'hartonomous', 'hartonomous_recompose_content';` (registered in `ext/hartonomous_pg/sql/hartonomous--1.0.sql`).

C# orchestration is one line via the existing `NpgsqlSubstrateCommand` allowlist pattern:

```csharp
// In SubstrateFunctionNames: public const string RecomposeContent = "substrate.recompose_content";
await using NpgsqlCommand cmd = NpgsqlSubstrateCommand.CreateFunction(
    conn, SubstrateFunctionNames.RecomposeContent, new object?[] { documentHash });
byte[] reconstructed = (byte[])await cmd.ExecuteScalarAsync(ct);
```

CLI command at `src/Hartonomous.Cli/Commands/RecomposeContentCommand.cs` wraps the C# call with file output.

Total work: O(tier-depth) bulk SPI probes (one per tier, not per child), zero-copy geometry access, zero-allocation byte appending in C, single C# round-trip. C/C++ does the hot recursive walk + memory-direct codepoint resolution; PG SQL hosts the recursive query + provides the SPI bulk btree probe surface; C# orchestrates with one query. This is the load-bearing efficiency property of the invention — and the canonical holistic-stack pattern every substrate operation should follow.

If reconstruction is slower than this OR requires more than one round-trip from C#, the substrate's geometry-as-indexed-manifest contract is broken.

**Files created for this gate:**
- `ext/hartonomous_pg/src/pg_recompose_content.c` (new C extension function)
- `ext/hartonomous_pg/sql/recompose_content.sql` (SQL function registration; included in bootstrap)
- `src/Hartonomous.Cli/Commands/RecomposeContentCommand.cs` (CLI wrapper)
- Add `RecomposeContent` constant to `src/Hartonomous.Core/Data/SubstrateFunctionNames.cs` allowlist
- Add `@include schema/functions/recompose_content.sql` to `sql/schema/bootstrap.sql`

**Substrate-state correctness assertions (SQL queries run post-seed):**

```sql
-- Codepoint count: 1,114,112 entities (= 0x110000)
SELECT count(*) FROM substrate.entity_classification 
WHERE entity_type_id = (SELECT id FROM substrate.entity_type WHERE code = 'codepoint');

-- Per-codepoint UCD-version attestation chain (sample U+0041)
SELECT count(DISTINCT provenance_id) FROM substrate.edge_rating_event ere
JOIN substrate.edge e ON e.hash = ere.edge_hash AND e.edge_type_id = ere.edge_type_id
WHERE [participants include U+0041 codepoint]
  AND ere.context_type_id = (SELECT id FROM substrate.significance_context WHERE code = 'unicode_version_consensus');
-- Expected: ≥ 18 (one per staged UCD version, 5.2.0 through 17.0.0)

-- IVD per-collection attestation
SELECT count(DISTINCT provenance_id) FROM substrate.edge_rating_event 
WHERE context_type_id = (SELECT id FROM substrate.significance_context WHERE code = 'ivd_collection_consensus');
-- Expected: 5 distinct (adobe-japan1, hanyo-denshi, krname, moji_joho, msarg)

-- Encoding-standard attestation
SELECT count(DISTINCT provenance_id) FROM substrate.edge_rating_event 
WHERE context_type_id = (SELECT id FROM substrate.significance_context WHERE code = 'encoding_position_consensus');
-- Expected: ≥ 30 distinct provenances

-- attestation_type still at 3 rows
SELECT count(*) FROM substrate.attestation_type;
-- Expected: 3 (positive_evidence, negative_evidence, neutral_evidence)

-- AP-1 priming coverage: every edge has a significance row per current arena
SELECT et.code, sc.code, count(*) FROM substrate.edge_significance es
JOIN substrate.edge_type et ON et.id = es.edge_type_id
JOIN substrate.significance_context sc ON sc.id = es.context_type_id
GROUP BY et.code, sc.code;
-- Verify every edge_type × arena combination has rows
```

---

## Critical files to read before implementation (verified existence)

| File | Purpose |
|---|---|
| `src/Hartonomous.Core/Ingestion/IIngestionBatch.cs` (lines 14-320) | Real `AddEntity`/`AddEdge`/`AddJunction`/`AddPhysicality`/`AddSignificance` API with all overloads |
| `src/Hartonomous.Core/Ingestion/IIngestionPipeline.cs` | `CreateBatch(provenance)`, `SubmitBatchAsync`, `DrainPendingAsync`, `GetExistingEntity{Hashes,Classifications}Async` |
| `src/Hartonomous.Core/Ingestion/EdgeSignificanceSpec.cs` (lines 18-21) | `(ContextTypeCode, AttestationTypeCode, InitialMu)` arena priming shape |
| `src/Hartonomous.Core/Ingestion/EdgeRatingEvent.cs` (lines 28-45) | Sign-aware event with `(ContextTypeCode, AttestationTypeCode, Score, Weight, ModelSourceId?, TensorHash?, SourceTensorName?, PrimitiveCode?, TupleCode?, SlotCode?)` |
| `src/Hartonomous.Decomposers/Wiktionary/WiktionaryDecomposer.cs` (lines 153-200) | Canonical producer with `ParallelChunkProcessor` |
| `src/Hartonomous.Decomposers/Tatoeba/TatoebaDecomposer.cs` (lines 83-200) | Multi-pass producer with batch flush on size |
| `src/Hartonomous.Decomposers/WordNet/WordNetDecomposer.cs` (lines 94-199) | Reference-map-driven producer |
| `src/Hartonomous.Decomposers/Iso/Iso639Decomposer.cs` | Existing reference-table-seeding pattern to mirror |
| `src/Hartonomous.Core/Text/SubstrateTextDecomposer.cs` (lines 139-223) | `EmitStatic(batch, utf8, options)` — canonical text-emission entry; all text routes through this |
| `src/Hartonomous.Core/Decomposition/ParallelChunkProcessor.cs` (lines 27-96) | Fan-out per AP-24, `DefaultDegreeOfParallelism = cores/2 ∈ [4,16]` |
| `src/Hartonomous.Core/Decomposition/DecomposerConfig.cs` (lines 1-33) | Per-decomposer config: SourceDirectory, BatchSize=25_000, ConnectionString, LanguageFilter?, ModelFilter? |
| `src/Hartonomous.Core/Orchestration/Phase.cs` (lines 3-24) | 12 phases — add Iso15924, Iso3166, Bcp47, Cldr |
| `ext/libhartonomous/codegen/xml_pull.h` (lines 1-94) | C XML pull parser; namespace caveat in header comment |
| `scripts/build/generate_unicode_tables.py` (lines 1-1921) | Pre-gen script to rewrite at Step A |
| `sql/schema/seed/edge_type.sql` (113 rows, 7 categories) | Existing edge type catalog |
| `sql/schema/seed/significance_context.sql` (11 arenas) | Existing arena vocabulary |
| `sql/schema/seed/attestation_type.sql` (3 rows) | Confirmed P1d collapse done |
| `sql/schema/seed/entity_type.sql` (23 rows) | Confirmed phantom-removal done; no new types needed |
| `sql/schema/seed/provenance.sql` | Provenance trust priors; add new sources in Step C |

---

## Out of scope (explicit deferrals)

These are real follow-ups but not part of this delivery — they're separate plans:

- Safetensors decomposer collapse to 4 primitives + 5 tuple passes (`docs/01-tensor-primitive-spec.md` §VI)
- Recomposer Build-a-bear synthesizer library expansion
- L2 / IRG / WG2 working doc ingestion — these are provenance / audit trail, not semantic content. Future content trajectories ingestion if/when we want to query the docs themselves
- Recipes batch under `docs/recipes/` realignment (downstream of Step B)

---

## Execution

One delivery, this session. AI-agent minutes per step, not human weeks. The substrate either has the corrected Unicode/ISO foundation by end-of-session or it doesn't.

Sequence A → K runs in dependency order: A (pre-gen flat-XML) + B (doc cleanup) + C (seed expansion) + D (xml_pull C# binding) ship first; E (13-pass rewrite) is the centerpiece producer work; F (new Unicode passes) + G (ISO 15924/3166/BCP47/CLDR) + H (encoding standards) + I (cross-link emissions in existing decomposers) expand coverage; J retires `populate_*_from_ext`; K is the end-to-end gate (RunAll.sh green + SQL assertions pass).

The plan delivers one substrate correction, not a sequence of independently-shippable patches.
