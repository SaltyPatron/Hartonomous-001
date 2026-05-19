# Tree-sitter + Kaitai Struct as the universal decomposer infrastructure

Source: `docs/20-technical/16-tree-sitter-grammar-strategy.md`. Topic I completely missed in earlier frame docs.

## The architectural insight

Tree-sitter is NOT "the code parser." It's the **canonical implementation of the substrate's decomposer contract**: produce typed compositions with named/positional ordered children from input bytes. Every input format that's substrate-friendly should be expressed as a tree-sitter grammar wherever feasible.

This collapses what would otherwise be ~60 bespoke per-format parsers into ONE infrastructure (tree-sitter + per-grammar AST→substrate mapping table) with grammars as DECLARATIVE config rather than imperative code.

Kaitai Struct is the binary-format complement — it does for binary what tree-sitter does for text (declarative grammar, generated parsers across multiple languages, typed output). For substrate's binary-format ingestion (safetensors tensor blocks, audio waveforms, images, video, MIDI), Kaitai is the natural pairing.

**Tree-sitter + Kaitai together cover essentially every substrate input format with shared declarative-grammar infrastructure.**

## What tree-sitter does for substrate

For any input format that fits a context-free or LR(k) grammar:

1. Write `grammar.js` declaring production rules
2. `tree-sitter generate` produces a C parser library
3. Substrate's decomposer pipeline parses input bytes through that library
4. Output is typed AST: every node has `node_type` (from grammar), named children (semantic roles), positional children (ordered)
5. **AST nodes map MECHANICALLY to substrate compositions**:
   - `node_type` → `entity_type_id`
   - named children → `edge_member` rows with `role_id`
   - positional children → LINESTRINGZM vertex order
   - leaves → atom or text_composition references
6. Incremental parsing — file changes only re-parse affected subtrees; substrate re-emits only changed entities
7. Error recovery — malformed input produces best-effort AST with explicit error nodes

The mapping from tree-sitter AST to substrate entities is **one piece of code per grammar** (typically a small dispatcher walking AST + emitting substrate records). The grammar itself is reusable across all substrate language bindings.

## Four format categories

### Category 1 — Existing tree-sitter grammar (use as-is, zero authorship)

305+ grammars in `tree-sitter-language-pack` cover most of what substrate cares about.

| Format | Grammar |
|---|---|
| 600+ programming languages | tree-sitter-{python, rust, go, c, cpp, java, javascript, typescript, ...} |
| Markdown / HTML / XML / LaTeX / RST / JSON / YAML / TOML | per-format grammars |
| SQL / GraphQL / Regex | available |
| Dockerfile / Makefile / Nix / Org-mode | available |

**Effort**: zero authorship. ~hours per grammar to author AST→substrate mapping.

### Category 2 — Author small custom grammars (sub-1-week each)

~25 substrate-specific grammars. Each is 50-300 lines of grammar.js.

Datasets they serve:
- **tree-sitter-conllu** — UD treebanks (339 corpora), PROIEL, Vedic Sanskrit Treebank, GUM, PreCo
- **tree-sitter-conllu-cupt** — PARSEME (26 languages)
- **tree-sitter-conll-2003** — CoNLL-2003 NER, WikiNER
- **tree-sitter-wordnet-dict** — Princeton WordNet 3.0 (data.*, index.*, lexnames)
- **tree-sitter-omw-tab** — OMW per-language .tab files
- **tree-sitter-iso639-tab** — ISO 639-3 .tab files
- **tree-sitter-ucd-properties** — UCD's ~50 .txt files (UnicodeData, Blocks, Scripts, etc.)
- **tree-sitter-uca-allkeys** — UCA allkeys.txt (collation weights)
- **tree-sitter-ucd-xml** — UCD's ucd.all.flat.xml etc.
- **tree-sitter-atomic-tsv** — ATOMIC 2020
- **tree-sitter-conceptnet-csv** — ConceptNet 5.7
- **tree-sitter-tatoeba-csv** — Tatoeba sentences.csv, links.csv
- **tree-sitter-goemotions-tsv** — GoEmotions
- **tree-sitter-social-chemistry** — Social Chemistry RoT TSV
- **tree-sitter-hatecheck-csv** / **tree-sitter-hatexplain-json** — moderation corpora
- **tree-sitter-emobank-csv** / **tree-sitter-nrc-lexicon** — emotion ratings
- **tree-sitter-wikipron-tsv** / **tree-sitter-phoible-csv** — pronunciation
- **tree-sitter-wals-csv** / **tree-sitter-glottolog-csv** — language typology
- **tree-sitter-magpie-csv** — MAGPIE idioms
- **tree-sitter-leandojo-json** — theorem proving
- **tree-sitter-kaikki-jsonl** — kaikki.org wiktextract dumps
- **tree-sitter-safetensors-header** — safetensors JSON metadata header

**Effort**: 1-3 days per grammar including tests + AST→substrate mapping. Total ~6-10 weeks of focused grammar authorship.

Subtle point: many LOOK like the same generic format (TSV, CSV, JSON), but giving each a dedicated grammar produces semantically-typed AST nodes (`atomic_tuple` vs `social_chem_rot` vs `nrc_emolex_entry`) that map directly to substrate entity types. **The grammar IS the type contract.**

### Category 3 — Author moderate custom grammars (1-2 weeks each)

~10 format-specific XML schemas with substantial domain semantics. tree-sitter-xml gives lexical layer; build semantic layer on top.

- **tree-sitter-timeml** (ISO-TimeML / ISO 24617-1) — TempEval-3, MEANTIME, Causal-TimeBank
- **tree-sitter-diaml** (ISO 24617-2 dialogue acts) — DialogBank, DiAML annotations
- **tree-sitter-iso-space** (ISO 24617-6) — SpaceEval 2015
- **tree-sitter-tei-perseus** — Perseus Greek + Latin canonical libraries, EpiDoc
- **tree-sitter-verbnet** — VerbNet 3.4
- **tree-sitter-propbank-frame** — PropBank, Universal PropBank
- **tree-sitter-framenet** — FrameNet XML
- **tree-sitter-cldr-xml** — CLDR locale data
- **tree-sitter-vua-metaphor** — VUA Metaphor XML annotations
- **tree-sitter-ami-multixml** — AMI Meeting Corpus

**Effort**: ~10 grammars × ~1.5 weeks = ~15 weeks. Most don't exist today; substrate authoring them is contribution to the linguistic-NLP community beyond own use case.

### Category 4 — Out of scope for tree-sitter (binary / non-grammatical) → Kaitai Struct

| Format | Why not tree-sitter | Alternative |
|---|---|---|
| safetensors **tensor blocks** (after JSON header) | Raw binary tensor data; no grammar | mmap + offset table from JSON header; emit tensor entities directly |
| PyTorch `.pt` / `.pth` (pickle) | Pickle opcode stream; security implications | `torch.load(..., weights_only=True)` |
| GGUF | Custom binary with k-quants | Hand-written reader |
| Audio (WAV, FLAC, MP3, OGG) | Binary; PCM or compressed | libsndfile / ffmpeg / dr_libs → audio_chunk + sample-grid entities |
| MIDI | Binary event stream | python-midi / mido → per-event entities |
| Images (JPEG, PNG, WebP) | Binary with format-specific structure | libjpeg / libpng / OpenCV → pixel-region entities |
| Video (MP4, WebM) | Multi-stream container | ffmpeg → per-frame extraction → image decomposer |
| Parquet | Columnar binary | pyarrow / Apache Arrow → row iteration → text/structured-field decomposers |
| Single-line giant JSONL | Each line IS JSON doc tractable by tree-sitter-json; file as a whole just newline-delimited | Line-by-line + tree-sitter-json |

For Category 4 formats, substrate's decomposer infrastructure has format-specific parsers, but they emit the SAME substrate-shaped output the tree-sitter path produces. **The contract is preserved; only internal parsing differs.**

Kaitai Struct enables declarative binary grammar (substrate's analog of tree-sitter for binary). Generated parsers across multiple languages, typed output.

## AST → substrate mapping pattern

For any tree-sitter grammar G, substrate needs mapping function `M_G : AST → substrate_records`. Small piece of imperative code per grammar.

```python
def map_conllu_ast_to_substrate(ast_root, provenance_id, pipeline):
    """Mapping for tree-sitter-conllu grammar."""
    for sentence_node in ast_root.named_children_of_type('sentence'):
        sentence_text_bytes = extract_raw_text(sentence_node)
        sentence_text_hash = pipeline.decompose_text(sentence_text_bytes, provenance_id)

        for token_node in sentence_node.named_children_of_type('token'):
            form = token_node.named_child('form').text
            upos = token_node.named_child('upos').text
            head = int(token_node.named_child('head').text)
            deprel = token_node.named_child('deprel').text

            form_hash = pipeline.decompose_text(form.encode('utf-8'), provenance_id)

            pipeline.emit_edge(EdgeRecord(
                edge_type_id=lookup_dep_type(deprel),
                participants=[(form_hash, 'dependent'), (head_form_hash, 'head')],
                provenance_id=provenance_id,
            ))

            pipeline.emit_junction(JunctionRecord(
                table='entity_pos',
                entity_hash=form_hash,
                pos_id=lookup_pos_id(upos),
            ))
```

~50 lines for canonical CoNLL-U decomposer. Grammar handles parsing; mapping function handles substrate-specific emission.

Mapping pattern is consistent enough that substrate's decomposer infrastructure can provide base class with helpers (`decompose_text`, `emit_edge`, `emit_junction`, `lookup_pos_id`, etc.). Each grammar's mapping function becomes mostly declarative (visit node X, emit substrate record Y).

## Tree-sitter vs alternatives

| Tool | Pros | Cons | Substrate fit |
|---|---|---|---|
| **tree-sitter** | Incremental parsing, error recovery, 305+ grammars exist, multi-language bindings, declarative DSL | Limited to context-free grammars, text input only | **Best fit** — chosen |
| **ANTLR4** | Mature, expressive (LL(*)), good IDE support | Heavier, generates per-language parsers, no shared compiled artifact, weaker incremental story | Second choice for some moderate-effort grammars |
| **Lark (Python)** | Pythonic, easy authoring | Python-only | Skip (not universal) |
| **PEG generators** | Easy authoring | Per-language; PEG performance pitfalls on left-recursion | Skip |
| **Hand-written recursive-descent** | Maximal control + performance | Per-format imperative code; defeats universal contract | Skip unless format truly demands |
| **Kaitai Struct** | Tree-sitter equivalent for BINARY | Binary-only | **Complement** to tree-sitter |

## Effort breakdown

| Category | Grammars | Effort |
|---|---|---|
| Cat. 1 (existing) | ~20 use as-is | 0 grammar; ~1 week mapping each |
| Cat. 2 (small custom) | ~25 grammars | 1-3 days each; ~6-10 weeks total |
| Cat. 3 (moderate XML) | ~10 grammars | ~1.5 weeks each; ~15 weeks total |
| Cat. 4 (binary, Kaitai) | ~7 unique formats | ~1-2 weeks each; ~10 weeks total |

**Total: ~6-9 months for one senior engineer, parallelizable to ~3 months with two.**

Compare to writing 60 bespoke per-format imperative parsers (~500-2000 lines each): ~12-18 months for zero reusable infrastructure.

## What this means for the substrate

1. **"We ingest anything structured" becomes structurally defensible.** Tree-sitter + Kaitai + clean decomposer contract → ingestion-of-format-X is config exercise, not research project. Substrate's universal claim has actual mechanism.

2. **Authoring substrate grammars is open-source contribution.** tree-sitter-timeml, tree-sitter-diaml, tree-sitter-conllu, tree-sitter-tei-perseus — most don't exist today. Releasing as open source is contribution to linguistic-NLP community.

3. **Per-source custom decomposers become tractable.** "Author tree-sitter grammar for this format" is 1-2 weeks, not multi-month custom dev.

## Honest limits

- **Whitespace/indentation-sensitive formats** (Python, YAML) need external scanners in C
- **Context-sensitive formats** push tree-sitter's GLR limits
- **Streaming over very large files** (22 GB Wiktionary JSONL) — parse line-by-line; decomposer drives streaming
- **Semantic disambiguation requires mapping function, not grammar** — grammars give typed structure; what types MEAN in substrate is mapping function responsibility
- **Performance** — tree-sitter is fast but not fastest possible; substrate's ingestion is not in multi-GB-per-second regime
- **Grammar authorship has learning curve** — GLR parsing, conflict resolution, DSL quirks; real engineering

## Cross-references

- `docs/20-technical/16-tree-sitter-grammar-strategy.md` — canonical source
- `frame/04-DECOMPOSER-ARCHITECTURE.md` — layer-type decomposer library (this complements: tree-sitter handles ingestion FORMAT parsing; layer-type decomposers handle tensor-math decomposition)
- `frame/25-TRINITY-AXIS-EMISSION.md` — per-decomposer contract template (the AST→substrate mapping function instantiates this)
- `frame/01-SUBSTRATE-LAWS.md` — Law 5 (decomposers as pure producers) preserved by tree-sitter approach
