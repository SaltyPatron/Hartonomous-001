# Decomposer Contract

**Status:** Canonical
**Last verified:** 2026-04-29
**Audience:** Engineers writing or maintaining decomposers for any modality, format, or seed source.

---

## What a decomposer is

A decomposer is a pure function from `(input bytes, provenance, format hints)` to a stream of substrate-shaped records. Records are emitted to the central ingestion pipeline; the pipeline owns concurrency, batching, COPY semantics, and significance priming.

Decomposers do not own threads. Decomposers do not open transactions. Decomposers do not call `NpgsqlBinaryImporter` or `COPY ... FROM STDIN` directly. Decomposers do not perform per-row inserts. Decomposers do not run their own thread pools or `Channel.CreateBounded` instances.

Decomposers are pure producers. The pipeline is the orchestrator.

## The contract surface

Every decomposer implements one canonical interface:

```csharp
public interface IDecomposer : IAsyncDisposable {
    string ProvenanceCode { get; }       // matches a row in ref.provenance
    string DisplayName { get; }
    IReadOnlyList<Phase> Phases { get; }   // which ingestion phases this participates in
    Task ValidateSourceAsync(CancellationToken ct);
    Task DecomposeAsync(IIngestionPipeline pipeline, IProgressReporter reporter, CancellationToken ct);
}
```

The decomposer's `DecomposeAsync` walks its input source (a directory, an XML file, a JSONL stream, a safetensors file, a corpus tarball) and emits records via the `IIngestionPipeline` interface. The pipeline accepts records into bounded channels and drains them into staging tables and then substrate tables. The decomposer never touches `Npgsql` or any database primitive directly.

## Record types the pipeline accepts

```
EntityRecord            (entity_type_id, hash, provenance_id)
EdgeRecord              (edge_type_id, hash, geom_or_linestring4d, provenance_id)
EdgeMemberRecord        (edge_type_id, edge_hash, entity_type_id, entity_hash, edge_role_id, position)
PhysicalityRecord       (physicality_type_id, entity_type_id, entity_hash, geom_or_point4d_or_linestring4d)
SignificanceRecord      (context_type_id, target_kind, target_type_id, target_hash, mu, sigma, volatility, games)
JunctionRecord          (junction_table_id, entity_type_id, entity_hash, classification_type_id, classification_value_id, mu_optional, ...)
SequenceRecord          (parent_type_id, parent_hash, child_type_id, child_hash, ordinal_position, count_rle)
                        ← OPTIONAL inverse-lookup denormalization;
                          composition trajectory is the source of truth for ordering
```

The decomposer emits these record types as it processes its input. The pipeline batches them by record type into bounded channels; long-lived COPY workers drain channels into staging tables; staging-flush procedures move staging data into substrate tables with FK ordering and dedup.

## Identity is the decomposer's responsibility

Every record the decomposer emits must carry the correct content-addressed hash. The decomposer computes hashes via the canonical native functions:

```csharp
// in Hartonomous.Core.Native.HCore (P/Invoke surface)
public static byte[] AtomId(uint codepoint);                           // BLAKE3(le32(codepoint))
public static byte[] CompositionId(IReadOnlyList<byte[]> childHashes); // BLAKE3(child hashes concatenated)
public static byte[] EdgeId(int edgeTypeId, IReadOnlyList<byte[]> roleOrderedParticipantHashes); // BLAKE3(le32(type) || hashes)
```

Decomposers MUST NOT roll their own hashing. MUST NOT hash strings via `Encoding.UTF8.GetBytes(s)` and pass to BLAKE3 — that bypasses the decomposer's responsibility to route text through `text_decompose` (see "seed-uses-core" below). MUST NOT include placement metadata in any hash input.

## Seed-uses-core

The single most-violated principle in prior attempts. Stated unambiguously:

**Every text-bearing string from any seed source MUST be routed through the universal text decomposer (`text_decompose`).** WordNet glosses, WordNet examples, UD sentence text, Wiktionary etymologies, Wiktionary definitions, Wiktionary translations, Tatoeba sentence text, safetensors `config.json` JSON values, image captions, audio transcripts, model display names — all text content.

The text decomposer hashes the bytes through codepoint atom → grapheme cluster composition → word form composition → text_composition. The seed decomposer does NOT call `Blake3.Hash(string)` on user-visible text.

When a Tatoeba sentence and a WordNet example contain identical text bytes, both decomposers route the bytes through `text_decompose`, both hit the same `text_composition` hash, both reference the same row. Convergence works. Without seed-uses-core, the same content produces multiple `text_composition` rows fragmented across decomposers — convergence fails — the substrate stops being a learning system.

The pipeline interface exposes this as:

```csharp
public interface IIngestionPipeline {
    Task<byte[]> DecomposeText(byte[] utf8Bytes, int provenanceId, CancellationToken ct);
    Task<byte[]> DecomposeText(string text, int provenanceId, CancellationToken ct);

    Task EmitEntity(EntityRecord record, CancellationToken ct);
    Task EmitEdge(EdgeRecord record, CancellationToken ct);
    Task EmitEdgeMember(EdgeMemberRecord record, CancellationToken ct);
    Task EmitPhysicality(PhysicalityRecord record, CancellationToken ct);
    Task EmitJunction(JunctionRecord record, CancellationToken ct);
    Task EmitSequence(SequenceRecord record, CancellationToken ct);

    // ... etc
}
```

Seed decomposers call `DecomposeText` with the bytes of glosses/examples/etc., receive the root `text_composition` hash, and emit semantic edges (e.g., `has_gloss(synset, text_composition_root)`) on top. They never call `Blake3.Hash(textBytes)` directly.

## NFC normalization at the decomposer entry

The text decomposer applies NFC normalization to its input codepoint sequence before producing grapheme clusters. This ensures `café` (NFC `é` = U+00E9) and `cafe` + `U+0301` (NFD) are treated as canonically distinct — they're not the same text — but their canonical equivalence is recorded by an explicit `canonical_decomposition_of` edge from UCD's decomposition mapping data.

Note: NFC normalization is NOT identity collapse. Different byte sequences produce different hashes. NFC ensures that consistent canonical-ordering rules apply to combining marks; it does not merge precomposed and decomposed forms. Their equivalence is the canonical-decomposition edge, navigable as graph data.

## Fail-loud: decomposers halt on first defect

Per Substrate Law #13, decomposers that encounter malformed input (broken XML, truncated files, invalid UTF-8, schema mismatches, missing required fields) HALT with a diagnostic error pointing at the file, line, byte offset, and entity. They do not skip-and-continue. They do not "best effort." They do not log and proceed.

The pipeline catches the decomposer's halt, rolls back the current batch (which may have been partially staged), and surfaces the error to the operator. The operator fixes the input data or the decomposer; ingestion resumes from the last clean checkpoint.

Patterns to catch in code review:
```csharp
try { ... } catch (Exception ex) { logger.Warn(ex); continue; }   // FORBIDDEN
try { ... } catch { /* skip */ }                                   // FORBIDDEN
if (!validRecord) { stats.Skipped++; continue; }                   // FORBIDDEN
```

The only `catch` block allowed in decomposer code is for transient infrastructure failures (database deadlock, network blip) where the pipeline-level retry policy takes over.

## Decomposer phases

Decomposers declare which phases they participate in. The substrate's phase enumeration:

```
Phase.CoreAlgebra         // schema bootstrap, reference tables, custom types
Phase.UcdUca              // UCD/UCA seeding (creates atoms with S³ positions)
Phase.Iso639              // ISO 639 language reference
Phase.WordNetOmw          // WordNet + OMW alignment
Phase.UniversalDeps       // UD treebanks
Phase.ModelDecomp         // Safetensors model ingestion
Phase.Wiktionary          // Wiktionary
Phase.Tatoeba             // Tatoeba
Phase.TextDecomp          // text corpus ingestion
Phase.SignificanceField   // background priming and arena dynamics
Phase.InferenceEngine     // engine readiness validation
Phase.Validation          // post-ingest validation gates
```

Phases run in declared dependency order. UCD/UCA produces atoms before any composition can be produced (everything bottoms at codepoints). ISO 639 produces language reference rows before multilingual seeders run. WordNet+OMW produces synsets before UD's lemma-to-synset edges. Models can be ingested after the lexical foundation (so model edges have curated edges to compete with).

A decomposer's `Phases` property is a set; some decomposers participate in multiple phases. Most participate in exactly one.

## Concurrency within a decomposer

A decomposer's `DecomposeAsync` runs as one logical workflow but may use parallelism INTERNALLY for its parsing work, as long as it produces records to the pipeline through the pipeline's interface (which is thread-safe).

For example, the Wiktionary decomposer parses the kaikki.org JSONL dump (`raw-wiktextract-data.jsonl` — single file, multi-million records; exact line count varies by dump version and is unverified in this doc). Per-record parsing is independent and CPU-bound (simdjson, regex, validation). The decomposer can use `Parallel.ForEachAsync` over JSONL byte-offset chunks, with each chunk's worker calling `pipeline.EmitEntity` etc. The pipeline accepts emissions from concurrent producers safely.

What's forbidden: the decomposer creating its own bounded channels, its own COPY connections, its own transactions. The pipeline interface is the only allowed substrate access.

## Decomposers per source

| Decomposer | Source | Provenance | Phases | What it produces |
|---|---|---|---|---|
| `UcdUcaDecomposer` | `D:\Models\UCD\Public\UCD\latest\ucdxml\ucd.all.flat.xml` + `allkeys.txt` | `unicode_consortium` | `UcdUca` | codepoint atoms, point4d on S³, codepoint_property junction, canonical_decomposition edges, case_folds_to edges |
| `Iso639Decomposer` | `D:\Models\ISO639\` | `sil_international` | `Iso639` | language reference rows, macrolanguage_includes edges, language_family edges |
| `WordNetDecomposer` | `D:\Models\princeton-wordnet\` dict files | `princeton_wordnet` | `WordNetOmw` | lemma, synset, word_sense entities; has_sense, hypernym, hyponym, meronym, holonym, antonym, has_gloss, has_example edges; entity_pos, entity_sense junctions; sense, lexname, semantic_relation_type reference rows |
| `OmwDecomposer` | `D:\Models\omw\` `.tab` files | `omwn_consortium` | `WordNetOmw` | non-English lemma entities (text_composition + word_form via text_decompose); aligned_to_synset edges; entity_language junctions |
| `UdDecomposer` | `D:\Models\ud-treebanks\` `.conllu` files | `universaldependencies` | `UniversalDeps` | ud_sentence, ud_token entities; lemma, word_form (via text_decompose); dep_* edges (one per UD deprel); entity_pos, entity_morph_feature junctions |
| `WiktionaryDecomposer` | `D:\Models\wiktionary\` (kaikki.org JSONL) | `wiktextract` | `Wiktionary` | wikt_sense, inflected_form, lemma entities; has_etymology, has_pronunciation, has_form, translation_of, inflection_of edges; entity_pos, entity_sense junctions |
| `TatoebaDecomposer` | `D:\Models\tatoeba\` | `tatoeba` | `Tatoeba` | tatoeba_sentence (via text_decompose), audio_recording entities; has_text, translation_link, recording_of, has_contributor edges |
| `SafetensorsDecomposer` | `D:\Models\hub\<model_dir>\` | `huggingface_model:<model_id>` | `ModelDecomp` | tensor, model_architecture, attention_pattern, bpe_token entities; in_model, in_layer, has_dtype, has_shape, beaten_path, transformation, embedding_similarity edges; tensor_role, model_architecture_class junctions; firefly point4d physicality |
| `TextCorpusDecomposer` | arbitrary text directories | `text_corpus:<id>` | `TextDecomp` | text_composition (via text_decompose), paragraph, document entities; document-level metadata edges |
| `TinyCodesDecomposer` | `D:\Models\hub\datasets--nampdn-ai--tiny-codes\` parquet | `tiny_codes` | `TextDecomp` | NL prompt → text_composition; code → tree-sitter AST compositions; implements_description edges between them |

Each decomposer's full specification (input schema, emitted entities, emitted edges, junction populations, edge cases, validation gates) lives in `20-technical/06-seed-decomposers.md` (for seed sources) or `20-technical/0X-*-decomposer.md` (for modality decomposers).

## Tree-sitter as the canonical decomposer infrastructure

Tree-sitter is the substrate's universal "take digital content, produce typed AST" engine. It is NOT just for code. It IS the canonical implementation of the decomposer contract for any text-format input (and Kaitai Struct is the canonical equivalent for binary formats).

The substrate's strategic position: every text-format decomposer is **a tree-sitter grammar plus an AST→substrate mapping function**. This collapses what would otherwise be ~60 bespoke per-format parsers into one infrastructure with declarative grammars. See `20-technical/16-tree-sitter-grammar-strategy.md` for the comprehensive grammar authorship plan and per-dataset grammar assignments.

The decomposition pipeline:

```
tree-sitter parse output       ↔ substrate composition
tree-sitter node_type          ↔ entity_type_id
tree-sitter named children     ↔ edge_member rows with role_id
tree-sitter positional children ↔ position in linestring4d
tree-sitter leaf token         ↔ atom or text_composition reference
```

A tree-sitter Python file's AST decomposes into substrate compositions:
- `module` composition (root)
- `function_definition` composition (each function)
- `parameters`, `body` named children → edges with named roles
- `string_literal`, `identifier` leaves → text_compositions via text_decompose

The tree-sitter language pack provides ~305 grammars covering virtually every programming language and many markup/data formats. For substrate-specific formats (CoNLL-U, TimeML, DiAML XML, WordNet dict, ATOMIC TSV, etc.), the substrate authors custom grammars — typically 50–300 lines of grammar.js per format. See `20-technical/16-tree-sitter-grammar-strategy.md` for the authorship plan, the per-dataset grammar assignments, and the canonical AST→substrate mapping pattern.

For binary formats (safetensors tensor blocks, PyTorch .pt/.pth pickle, audio waveforms, image bitmaps, video containers, MIDI), Kaitai Struct (declarative binary grammar DSL with multi-language code generation) is the analog of tree-sitter. The same decomposer contract applies — produce typed compositions with ordered children — but the parsing infrastructure is binary-aware. Some formats may not have suitable Kaitai grammars and use hand-written readers (e.g., `torch.load(weights_only=True)` for .pt files).

The decomposer pipeline doesn't care which parser is upstream. The contract is: produce typed compositions with ordered children and proper hashing. Tree-sitter is canonical for text; Kaitai Struct is canonical for binary; both produce the same downstream shape.

## Decomposer validation gates

Before a decomposer is considered production-ready:

1. **Determinism gate.** Run the decomposer on the same input twice. Compare emitted record sets via hash-of-hashes. Identical.
2. **Idempotency gate.** Run the decomposer twice into the same substrate. Substrate state after second run is identical to state after first run (no duplicate rows; no significance drift).
3. **Convergence gate.** Run two different decomposers that should produce overlapping content. Verify the overlapping content lands at the same entity hashes.
4. **Seed-uses-core gate.** Grep the decomposer source for `Blake3.Hash(...)` calls on text-bearing strings. Should be zero matches outside the universal text decomposer itself.
5. **Fail-loud gate.** Inject a deliberately broken input (truncated XML, invalid UTF-8, schema violation). Verify decomposer halts with diagnostic error; substrate state unchanged.
6. **Phase gate.** Decomposer's `Phases` property correctly declares dependencies. Pipeline schedules it after dependencies and refuses to schedule it before.
7. **Provenance gate.** All emitted records carry the correct `provenance_id`. Verify via SQL: `SELECT count(*) FROM substrate.entity WHERE provenance_id = X` matches expected.

These are the standard checklist; per-decomposer specs add domain-specific gates.

## Cross-references

- Substrate laws governing decomposers: `10-architecture/01-substrate-laws.md` (Laws 1, 2, 5, 6, 7, 8, 12, 13)
- The universal text decomposer's behavior: `20-technical/02-text-decomposer.md`
- Per-decomposer specs: `20-technical/06-seed-decomposers.md` and modality docs
- Tree-sitter integration: `20-technical/03-code-decomposer.md`
- Decomposer checklist: `40-process/checklists/00-decomposer-checklist.md`
- Recomposer counterpart: `10-architecture/06-recomposer-contract.md`
