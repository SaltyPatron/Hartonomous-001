# Tree-sitter as the Universal Decomposer Infrastructure

**Status:** Canonical
**Last verified:** 2026-04-29
**Audience:** Decomposer authors, anyone evaluating "how do we ingest format X." This document sets the substrate's grammar-authorship strategy and is the authoritative answer to "should we use tree-sitter for this?"

---

## The architectural insight

Tree-sitter is not "the code parser." It's the canonical implementation of the substrate's decomposer contract: produce typed compositions with named/positional ordered children from input bytes. Every input format that's substrate-friendly should be expressed as a tree-sitter grammar wherever feasible.

This collapses what would otherwise be ~60 bespoke per-format parsers into one infrastructure (tree-sitter + per-grammar AST→substrate mapping table) with grammars as DECLARATIVE config rather than imperative code.

The substrate's prior planning treated each new dataset's format as needing a custom parser. That's the trap. Each custom parser is hundreds to thousands of lines of imperative parsing code that has to be maintained, tested, and ported across language bindings. A tree-sitter grammar for the same format is typically 50-300 lines of grammar.js (declarative production rules), gets a GLR parser generated, gets cross-language bindings for free, gets incremental re-parsing for free, gets error recovery for free, and gets compiled to fast C.

## What tree-sitter actually does for substrate

For any input format that fits a context-free or LR(k) grammar:

1. You write `grammar.js` declaring production rules
2. `tree-sitter generate` produces a C parser library
3. The substrate's decomposer pipeline parses input bytes through that library
4. Output is a typed AST: every node has a `node_type` (from your grammar), named children (semantic roles), positional children (ordered)
5. AST nodes map MECHANICALLY to substrate compositions: `node_type` → `entity_type_id`, named children → `edge_member` rows with `role_id`, positional children → linestring4d vertex order, leaves → atom or text_composition references
6. Incremental parsing: file changes only re-parse affected subtrees; substrate re-emits only changed entities
7. Error recovery: malformed input produces best-effort AST with explicit error nodes; substrate ingests the recoverable parts and flags the rest

The mapping from tree-sitter AST to substrate entities is one piece of code per grammar (typically a small Python/Rust dispatcher that walks the AST and emits substrate records). The grammar itself is reusable across all substrate language bindings.

## Format categorization for substrate's input ecosystem

Every format the substrate ingests falls into one of four categories.

### Category 1 — Existing tree-sitter grammar (use as-is)

Already in the tree-sitter ecosystem. Just use them. The 305+ grammars in `tree-sitter-language-pack` cover an enormous fraction of what substrate cares about.

| Format | Grammar | Notes |
|---|---|---|
| 600+ programming languages | tree-sitter-{python, rust, go, c, cpp, java, javascript, typescript, ...} | Per-language; well-maintained |
| Markdown | tree-sitter-markdown | Has known edge cases (CommonMark complexity) |
| HTML | tree-sitter-html | Plus tree-sitter-xml for stricter XML |
| LaTeX | tree-sitter-latex | Reasonable for substrate text-with-math |
| RST | tree-sitter-rst | Documentation source |
| JSON | tree-sitter-json | Foundation for many semantic-data grammars |
| YAML | tree-sitter-yaml | Config and structured data |
| TOML | tree-sitter-toml | Config |
| XML (generic) | tree-sitter-xml | Foundation; substrate authors specific schemas on top |
| SQL | tree-sitter-sql | Multiple dialects available |
| GraphQL | tree-sitter-graphql | Schema and query |
| Regex | tree-sitter-regex | Cross-language regex patterns |
| Dockerfile | tree-sitter-dockerfile | Infrastructure-as-code |
| Makefile | tree-sitter-make | Build patterns |
| Nix | tree-sitter-nix | Functional config |
| Org-mode | tree-sitter-org | Document markup |
| Tree-sitter grammars themselves | tree-sitter-grammar | Meta — useful for auto-grammar work |

**Effort:** zero authorship. Map AST → substrate per grammar (~hours per grammar).

### Category 2 — Author small custom grammars (sub-1-week each)

Substrate-specific formats with regular structure. Each grammar is 50-300 lines of grammar.js. Most are over a generic base (CSV/TSV/JSON/XML/CoNLL) with semantic-specific node names.

These are the ones substrate should author and contribute back to the tree-sitter ecosystem.

| Format | Approximate grammar size | Datasets it serves |
|---|---|---|
| **tree-sitter-conllu** | ~200 lines | UD treebanks (339 corpora), PROIEL, Vedic Sanskrit Treebank, GUM (CoNLL columns), PreCo (CoNLL with coref) |
| **tree-sitter-conllu-cupt** (CUPT extension for MWE) | ~100 lines on top | PARSEME (26 languages) |
| **tree-sitter-conll-2003** | ~50 lines | CoNLL-2003 NER, WikiNER |
| **tree-sitter-wordnet-dict** | ~150 lines | Princeton WordNet 3.0 (data.{noun,verb,adj,adv}, index.*, lexnames) |
| **tree-sitter-omw-tab** | ~50 lines | OMW per-language .tab files |
| **tree-sitter-iso639-tab** | ~30 lines | ISO 639-3 .tab files (4 files, same shape) |
| **tree-sitter-ucd-properties** | ~100 lines | UCD's ~50 .txt files (UnicodeData, Blocks, Scripts, etc. — most share a common semicolon-delimited shape with code-or-range; property; value) |
| **tree-sitter-uca-allkeys** | ~80 lines | UCA allkeys.txt (collation weights) |
| **tree-sitter-ucd-xml** | ~150 lines on top of tree-sitter-xml | UCD's ucd.all.flat.xml etc. |
| **tree-sitter-atomic-tsv** | ~30 lines | ATOMIC 2020 (head\trel\ttail) |
| **tree-sitter-conceptnet-csv** | ~60 lines | ConceptNet 5.7 CSV with assertion+context+source |
| **tree-sitter-tatoeba-csv** | ~40 lines | Tatoeba sentences.csv, links.csv |
| **tree-sitter-goemotions-tsv** | ~30 lines | GoEmotions multi-label TSV |
| **tree-sitter-social-chemistry** | ~80 lines | Social Chemistry RoT TSV (12 annotation dimensions) |
| **tree-sitter-hatecheck-csv** | ~40 lines | HateCheck functional tests |
| **tree-sitter-hatexplain-json** | ~40 lines on top of tree-sitter-json | HateXplain JSON entries |
| **tree-sitter-emobank-csv** | ~30 lines | EmoBank VAD ratings |
| **tree-sitter-nrc-lexicon** | ~40 lines | NRC EmoLex, NRC VAD (similar shape) |
| **tree-sitter-wikipron-tsv** | ~30 lines | WikiPron pronunciation TSV |
| **tree-sitter-phoible-csv** | ~80 lines | PHOIBLE phoneme inventory CSV |
| **tree-sitter-wals-csv** | ~80 lines | WALS feature-value CSV |
| **tree-sitter-glottolog-csv** | ~80 lines | Glottolog CLDF CSV |
| **tree-sitter-magpie-csv** | ~40 lines | MAGPIE idioms CSV |
| **tree-sitter-leandojo-json** | ~100 lines on top of tree-sitter-json | LeanDojo theorem/tactic/premise JSON |
| **tree-sitter-kaikki-jsonl** | ~150 lines on top of tree-sitter-json | kaikki.org wiktextract dumps (per-line JSON) |
| **tree-sitter-safetensors-header** | ~80 lines on top of tree-sitter-json | safetensors JSON metadata header |

**Effort:** 1-3 days per grammar including tests and AST→substrate mapping. Total for the listed set: ~6-10 weeks of focused grammar authorship.

A subtle point: many of these LOOK like the same generic format (TSV, CSV, JSON), but giving each a dedicated grammar produces semantically-typed AST nodes (`atomic_tuple` vs `social_chem_rot` vs `nrc_emolex_entry`) that map directly to substrate entity types. That's better than parsing them all as anonymous CSV rows and disambiguating downstream — the grammar is the type contract.

Alternative: a generic CSV/TSV grammar with a per-dataset schema config that names the columns. Possible, but loses some of tree-sitter's typed-AST advantage. Worth doing for one-off lexicons; per-grammar wins for anything substrate ingests at scale.

### Category 3 — Author moderate custom grammars (1-2 weeks each)

Format-specific XML schemas with substantial domain semantics. tree-sitter-xml gives you the lexical layer; you build the semantic layer on top.

| Format | Datasets it serves | Effort |
|---|---|---|
| **tree-sitter-timeml** (ISO-TimeML / ISO 24617-1) | TempEval-3, MEANTIME, Causal-TimeBank | ~2 weeks |
| **tree-sitter-diaml** (ISO 24617-2 dialogue acts) | DialogBank, DiAML annotations | ~1.5 weeks |
| **tree-sitter-iso-space** (ISO 24617-6) | SpaceEval 2015 | ~1.5 weeks |
| **tree-sitter-tei-perseus** (TEI subset for classical texts) | Perseus Greek + Latin canonical libraries, EpiDoc | ~2 weeks |
| **tree-sitter-verbnet** (VerbNet XML schema) | VerbNet 3.4 | ~1 week |
| **tree-sitter-propbank-frame** | PropBank frame files, Universal PropBank | ~1 week |
| **tree-sitter-framenet** | FrameNet XML (frame definitions, frame elements, lexical units) | ~2 weeks |
| **tree-sitter-cldr-xml** | CLDR locale data (multiple sub-schemas) | ~2 weeks |
| **tree-sitter-vua-metaphor** | VUA Metaphor XML annotations | ~1 week |
| **tree-sitter-ami-multixml** | AMI Meeting Corpus multi-stream XML (transcripts + dialog acts + named entities) | ~2 weeks |

**Effort:** ~10 grammars × ~1.5 weeks each = ~15 weeks of grammar authorship. Each can be parallelized; this is the largest single time investment in substrate decomposition.

These are also the most VALUABLE grammars to author and open-source. Most don't exist today; substrate authoring tree-sitter-timeml or tree-sitter-diaml is a contribution to the linguistic-NLP community well beyond the substrate's own use case.

### Category 4 — Out of scope for tree-sitter (binary or non-grammatical)

Some formats are not text — they're binary, or they have semantics that aren't context-free, or they're so simple that grammar overhead exceeds value.

| Format | Why not tree-sitter | Alternative |
|---|---|---|
| safetensors **tensor blocks** (after the JSON header) | Raw binary tensor data; no grammar | mmap + offset table from JSON header; emit tensor entities directly |
| PyTorch `.pt` / `.pth` (pickle format) | Pickle opcode stream; technically grammatical but Python's pickle protocol has security implications and existing libraries are battle-tested | `torch.load(..., weights_only=True)`; emit substrate compositions from the resulting Python dict |
| GGUF | Custom binary format with k-quants etc. | Hand-written reader (or skip per ADR-002) |
| Audio waveforms (WAV, FLAC, MP3, OGG) | Binary; PCM samples or compressed audio | libsndfile / ffmpeg / dr_libs; emit audio_chunk + sample-grid entities |
| MIDI | Binary event stream | python-midi / mido; emit per-event entities |
| Image formats (JPEG, PNG, WebP) | Binary with format-specific structure | libjpeg / libpng / OpenCV; emit pixel-region entities |
| Video formats (MP4, WebM) | Multi-stream container with codec-specific frames | ffmpeg; per-frame extraction → image decomposer |
| Parquet (tiny-codes, etc.) | Columnar binary | pyarrow / Apache Arrow; iterate rows, then text-content goes through text decomposer (and any nested structured fields go through their grammars) |
| Single-line giant JSONL files | Each line IS a JSON document tractable by tree-sitter-json, but the file as a whole is just newline-delimited — no enveloping grammar needed | Read line-by-line, parse each line via tree-sitter-json (or a custom JSONL grammar that's just `(json_document NEWLINE)*`) |

For Category 4 formats, the substrate's decomposer infrastructure has format-specific parsers, but they emit the SAME substrate-shaped output the tree-sitter path produces. The contract is preserved; only the internal parsing differs.

There IS a tool similar to tree-sitter for binary formats — **Kaitai Struct** — which does for binary what tree-sitter does for text: declarative grammar, generated parsers across multiple languages, typed output. For substrate's binary-format ingestion (safetensors, audio, images, video), Kaitai is the natural complement.

## The AST → substrate mapping pattern

For any tree-sitter grammar `G`, the substrate needs a mapping function `M_G : AST → substrate_records`. This is the small piece of imperative code per grammar.

Pattern:

```python
# pseudo-code; actual implementation depends on language bindings
def map_conllu_ast_to_substrate(ast_root, provenance_id, pipeline):
    """Mapping for tree-sitter-conllu grammar."""
    for sentence_node in ast_root.named_children_of_type('sentence'):
        # Each sentence is a substrate composition
        sentence_text_bytes = extract_raw_text(sentence_node)
        sentence_text_hash = pipeline.decompose_text(sentence_text_bytes, provenance_id)

        # Each sentence has comment lines (metadata) and token lines
        for token_node in sentence_node.named_children_of_type('token'):
            form = token_node.named_child('form').text
            lemma = token_node.named_child('lemma').text
            upos = token_node.named_child('upos').text
            head = int(token_node.named_child('head').text)
            deprel = token_node.named_child('deprel').text

            # Form goes through text decomposer
            form_hash = pipeline.decompose_text(form.encode('utf-8'), provenance_id)

            # Emit dependency edge from form to head
            pipeline.emit_edge(EdgeRecord(
                edge_type_id=lookup_dep_type(deprel),
                participants=[(form_hash, 'dependent'), (head_form_hash, 'head')],
                provenance_id=provenance_id,
            ))

            # Emit junction rows
            pipeline.emit_junction(JunctionRecord(
                table='entity_pos',
                entity_hash=form_hash,
                pos_id=lookup_pos_id(upos),
            ))
```

That's ~50 lines for the canonical CoNLL-U decomposer. The grammar handles the parsing; the mapping function handles the substrate-specific entity/edge emission. This is dramatically less code than a custom CoNLL-U parser would be.

The mapping pattern is consistent enough across grammars that substrate's decomposer infrastructure can provide a base class with helpers like `decompose_text`, `emit_edge`, `emit_junction`, `lookup_pos_id`, etc. Each grammar's mapping function then becomes mostly declarative (visit node X, emit substrate record Y).

## Why tree-sitter wins over alternatives for substrate

I evaluated tree-sitter against alternatives. Here's the substrate-specific scorecard:

| Tool | Pros | Cons | Substrate fit |
|---|---|---|---|
| **tree-sitter** | Incremental parsing, error recovery, 305+ grammars exist, multi-language bindings, active community, declarative DSL | Limited to context-free grammars (some formats need external scanners in C), text input only | **Best fit** — chosen |
| **ANTLR4** | Mature, very expressive (LL(*)), good IDE support | Heavier, generates Java/C#/Python parsers individually, no shared compiled artifact, weaker incremental story | Second choice for some moderate-effort grammars |
| **Lark (Python)** | Pythonic, easy to author | Python-only; not suitable as universal infrastructure across substrate language bindings | Skip |
| **PEG generators (pest, peg-rs, pyparsing, etc.)** | Easy to author; PEG is intuitive | Per-language; PEG has performance pitfalls on left-recursion | Skip |
| **Hand-written recursive-descent parsers** | Maximal control and performance | Per-format imperative code; defeats the universal-decomposer-contract argument | Skip unless format truly demands it |
| **Kaitai Struct** | Tree-sitter equivalent for BINARY | Binary-only | **Complement to tree-sitter** for binary formats |

Tree-sitter + Kaitai together cover essentially every substrate input format with shared declarative-grammar infrastructure. That's the recommended pairing.

## Effort breakdown for the substrate's full grammar set

Realistic timeline assuming one senior grammar engineer:

| Category | Grammars to author | Effort |
|---|---|---|
| Cat. 1 (existing) | ~20 we use as-is | 0 grammar authoring; ~1 week per grammar to author AST→substrate mapping |
| Cat. 2 (small custom) | ~25 grammars | 1-3 days each; ~6-10 weeks total |
| Cat. 3 (moderate XML schemas) | ~10 grammars | ~1.5 weeks each; ~15 weeks total |
| Cat. 4 (binary, non-tree-sitter) | ~7 unique formats (safetensors, .pt/.pth, audio, image, video, MIDI, parquet) | ~1-2 weeks each via Kaitai or libraries; ~10 weeks total |

**Total grammar + mapping work for the curated seed set: ~6-9 months for one senior engineer**, parallelizable down to ~3 months with two engineers.

That's a non-trivial commitment. But it's also the substrate's permanent decomposition layer; once authored, those grammars serve every future ingestion of those formats forever, and many can be open-sourced as contributions to the tree-sitter ecosystem.

For comparison: writing 60 bespoke per-format imperative parsers, each typically 500-2000 lines of custom code, would be ~12-18 months and produce zero reusable infrastructure for new formats.

## What this means for the substrate's product positioning

Three implications worth surfacing:

1. **The substrate's "we ingest anything structured" claim becomes structurally defensible.** Tree-sitter + Kaitai + a clean decomposer contract turn ingestion-of-format-X from a research project into a config exercise. Customers can tell you "we have proprietary corpus in format Y" and you can author its grammar in days, not months.

2. **Authoring substrate grammars is open-source contribution.** tree-sitter-timeml, tree-sitter-diaml, tree-sitter-conllu (if no good one exists yet), tree-sitter-tei-perseus — these are ALL valuable to the linguistic NLP community independent of substrate. Releasing them as open source is a marketing and technical-credibility win that costs nothing extra.

3. **The substrate's per-customer custom decomposers become tractable as a service tier.** "We'll ingest your custom XML format" becomes "author a tree-sitter grammar for it" — that's a 1-2 week professional services engagement, not a multi-month custom development project.

## Honest limits

A few things tree-sitter does NOT solve:

1. **Whitespace/indentation-sensitive formats** (Python, YAML) need external scanners in C. Existing grammars handle this; new grammars for indent-sensitive formats need extra care.

2. **Context-sensitive formats** (CSS with complex selectors, some markdown extensions) push tree-sitter's GLR limits. Workable but requires careful grammar design.

3. **Streaming over very large files**: tree-sitter parses in-memory. A 22 GB Wiktionary JSONL must be parsed line-by-line (each line is independently a JSON document — the JSONL "grammar" is just `(json_doc NEWLINE)*`). This is fine but it's the decomposer's job to drive the streaming.

4. **Semantic disambiguation requires the mapping function, not the grammar.** Tree-sitter gives you typed structure; what those types MEAN in substrate (which entity_type_id, which edge_type_id) is the mapping function's responsibility. Grammars don't replace semantic mapping work.

5. **Performance: tree-sitter is fast but not fastest possible.** For very-high-throughput parsing (multi-GB-per-second), a hand-tuned imperative parser might win. Substrate's ingestion is not in that regime; tree-sitter performance is more than adequate.

6. **Grammar authorship has a learning curve.** Writing tree-sitter grammars well requires understanding GLR parsing, conflict resolution, and the grammar DSL's quirks. This is real engineering, not a config file.

## Recommendation

The substrate's decomposer-contract document (`10-architecture/05-decomposer-contract.md`) should be updated to make tree-sitter the explicit canonical implementation, with Kaitai Struct as the binary-format complement.

The implementation roadmap (`40-process/04-implementation-roadmap.md`) should add a new milestone — let's call it **M3.5** — for "Tree-sitter + Kaitai decomposer infrastructure" — that delivers:

1. Tree-sitter library bound into the native extension or orchestrator
2. Kaitai Struct support for binary
3. The base mapping-function class with substrate emission helpers
4. The first three custom grammars: tree-sitter-conllu (foundation for UD ingestion), tree-sitter-ucd-properties (foundation for UCD seed), tree-sitter-conceptnet-csv (foundation for first Tier-1 expansion)

After M3.5, every subsequent decomposer milestone (M4 WordNet, M5 UD, M10 Tier-1 expansion, etc.) becomes "author the grammar + mapping function," not "write a custom parser."

This is the architectural move that turns substrate's seed-expansion roadmap from "a multi-year custom-parser-per-dataset effort" into "a months-of-grammar-authorship effort." Direct cost reduction; faster customer-facing format support; ecosystem contribution as a side benefit.

## Cross-references

- Decomposer contract: `10-architecture/05-decomposer-contract.md`
- Seed expansion roadmap (which datasets need which grammars): `20-technical/15-seed-expansion-roadmap.md`
- Implementation roadmap (where M3.5 fits): `40-process/04-implementation-roadmap.md`
- Substrate Law 5 (decomposers as pure producers): `10-architecture/01-substrate-laws.md`

## External references

- Tree-sitter project: <https://tree-sitter.github.io/tree-sitter/>
- Tree-sitter language pack (305+ grammars): <https://github.com/kreuzberg-dev/tree-sitter-language-pack>
- Tree-sitter grammar DSL guide: <https://tree-sitter.github.io/tree-sitter/creating-parsers/>
- Tree-sitter parsers wiki: <https://github.com/tree-sitter/tree-sitter/wiki/List-of-parsers>
- Kaitai Struct (binary equivalent): <https://kaitai.io/>
