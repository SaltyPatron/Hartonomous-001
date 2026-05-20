# Code Decomposer — Tree-sitter for Programming Languages and Structured Formats

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Engineers ingesting code corpora (tiny-codes, The Stack, customer code repositories), authors of new tree-sitter grammar bindings, anyone debugging code-decomposer output.

---

## What the code decomposer is

The code decomposer is the substrate's path for ingesting structured-text input (programming languages, markup, data formats) via tree-sitter grammars. It produces typed AST compositions that map directly onto substrate entities.

The code decomposer is NOT separate from the text decomposer in any architectural sense. It's the same substrate-shaped output (compositions with linestring4d trajectories), reached through a different parsing front-end:
- **Text decomposer**: bytes → codepoints → UAX #29 segmentation → composition tiers
- **Code decomposer**: bytes → tree-sitter grammar → typed AST → composition tiers

Both produce substrate state with the same entity-type / edge-type / physicality contract. Both invoke each other when needed: code decomposer calls text decomposer for string literals and identifiers; text decomposer doesn't call code decomposer (no recursion in that direction).

## Two operating modes

Mode 1 — **Pure code ingestion**: input is a single file in a known programming language; output is a typed AST.

Mode 2 — **Embedded code in text**: input is a text document with code blocks (Markdown with fenced code, RST with `..code-block::`, etc.); the text decomposer handles the prose; code blocks are recursively decomposed via the code decomposer. Output is a hierarchical composition where the document tree contains code AST subtrees.

## The pipeline, in order

```
input bytes (source file or code block)
        │
        ▼
[1] Language detection (file extension, content heuristics, caller hint)
        │
        ▼
[2] Tree-sitter parser selection (load the appropriate grammar)
        │
        ▼
[3] Tree-sitter parse → AST root node
        │
        ▼
[4] AST walk with substrate emission visitor
        │
        ├─ For each AST node:
        │     compute composition_id from children's hashes
        │     emit substrate.entity row (entity_type_id = ts_<grammar>_<node_type>)
        │     emit substrate.physicality (linestring4d through child centroids)
        │     emit substrate.edge_member rows (named children → role-tagged; positional children → ordinal)
        │     for leaf token nodes containing text content:
        │          recursively call text_decompose for the token's bytes
        │          treat returned text_composition hash as the leaf's representation
        │
        ▼
[5] File-level wrapper composition (typed code_file or per-language file root)
        │
        ▼
returns: BLAKE3 hash of the file-level composition
```

## Step-by-step specification

### Step 1 — Language detection

**Input:** Source bytes + optional caller hint (file extension, declared language).

**Output:** Selected tree-sitter grammar identifier (e.g., `tree-sitter-python`, `tree-sitter-rust`, `tree-sitter-typescript`).

**Behavior:**
- Caller-provided hint takes precedence.
- File extension lookup: `.py` → tree-sitter-python; `.rs` → tree-sitter-rust; `.ts` → tree-sitter-typescript; etc.
- For ambiguous extensions (`.h` could be C or C++), use content heuristics (presence of `class`, `template`, etc.).
- For shebang-line detection: `#!/usr/bin/env python3` → python.
- For files with no extension and no shebang: optional content-classifier fallback or raise `language_undetected`.

**Determinism:** Heuristic detection is deterministic given identical input; caller-hint paths bypass detection entirely.

### Step 2 — Tree-sitter parser selection

**Input:** Grammar identifier.

**Output:** Loaded tree-sitter parser instance.

**Behavior:** The substrate maintains a registry of compiled tree-sitter grammars. Each is a shared library exposing `tree_sitter_<lang>()` returning a TSLanguage. Parser selection loads from registry; cached after first use.

The grammar registry is configured at substrate-extension-build time and at decomposer-startup time. Adding a new language means:
1. Compile the tree-sitter grammar into a shared library (or include in the substrate's bundled grammars).
2. Add an entry to the grammar registry.
3. Author the AST→substrate mapping function for that grammar.
4. Register entity types `ts_<lang>_<node_type>` in `ref.entity_type` for each AST node type the grammar produces.

### Step 3 — Tree-sitter parse

**Input:** Source bytes + parser.

**Output:** AST root node (TSNode in tree-sitter's API).

**Behavior:** Standard tree-sitter parse. Returns AST where every node has:
- `type`: string identifier matching the grammar's production rule (e.g., `function_definition`, `class_declaration`, `binary_expression`, `string_literal`, `identifier`).
- `start_byte`, `end_byte`: byte range in source.
- `start_point`, `end_point`: line/column.
- `is_named`: whether this node is named in the grammar (filters anonymous syntactic noise like punctuation tokens).
- `children`: ordered list of child nodes (named and anonymous).
- `field_name(i)`: for named children, the grammar's field name (e.g., `name`, `body`, `parameters`, `condition`).

**Tree-sitter's error recovery:** for malformed input, tree-sitter produces best-effort AST with `ERROR` nodes marking unparseable regions. The substrate ingests recoverable parts; ERROR nodes become `tree_sitter_error` entities for audit but don't block ingestion.

### Step 4 — AST walk with substrate emission visitor

**Input:** AST root node.

**Output:** Substrate entity/edge/physicality rows for the AST.

**Behavior:** Depth-first post-order traversal. Children are visited before parents; each parent's hash depends on its children's hashes (Merkle).

Per-node mapping pseudocode:

```
function visit(node):
    if node.type is a leaf-token type with text content (identifier, string_literal, number, comment):
        token_text = source_bytes[node.start_byte : node.end_byte]
        text_hash = pipeline.decompose_text(token_text, provenance_id)
        return text_hash, type_id_for("ts_<lang>_<node.type>")

    if node.type is anonymous syntactic punctuation (e.g., open-brace, semicolon):
        # Skip; not its own substrate entity, but its position is recorded in parent's edge_members
        return None, None

    # Named non-leaf node:
    children_results = [visit(child) for child in node.children if child.is_named]

    child_hashes_in_order = [r.hash for r in children_results]
    composition_hash = composition_id(child_hashes_in_order)
    entity_type = type_id_for("ts_<lang>_<node.type>")

    upsert substrate.entity(entity_type, composition_hash)

    # Build linestring4d through child centroids
    child_centroids = [physicality_lookup(child.entity_type, child.hash).centroid for child in children_results]
    upsert substrate.physicality(composition_trajectory, entity_type, composition_hash, linestring4d(child_centroids))

    # Emit edge_member rows
    for i, child_result in enumerate(children_results):
        if node.field_name(i):
            # Named child: use grammar's field name as role
            role_id = lookup_role_id(node.field_name(i))
        else:
            # Positional child: use generic 'positional' role
            role_id = lookup_role_id('positional')

        upsert substrate.edge_member(
            edge_type_id = entity_type,    # The composition acts as its own edge in this representation
            edge_hash = composition_hash,
            entity_type_id = child_result.entity_type,
            entity_hash = child_result.hash,
            edge_role_id = role_id,
            position = i
        )

    return composition_hash, entity_type
```

**Determinism:** Tree-sitter parsing is deterministic. The visitor is deterministic. Hash computation is deterministic. Entity type IDs are stable (registered at grammar-binding time).

**Critical note on children-with-fields:** tree-sitter grammars distinguish two kinds of children:
- **Named fields** (e.g., a `function_definition` has a `name` field, a `body` field, a `parameters` field): these become role-tagged edge_member rows. Role names match the grammar's field names.
- **Positional / anonymous children**: become positional edge_member rows.

This means the substrate preserves the grammar's named structure: `function_definition.name` is a named-role edge, not a positional one. Querying "the name child of this function" becomes an edge_member lookup by role, not by ordinal.

### Step 5 — File-level wrapper composition

**Input:** AST root entity hash + file metadata.

**Output:** A `code_file` (or per-language equivalent) composition wrapping the AST root with file-level metadata edges.

**Behavior:**
- Compose `code_file` entity from AST root hash.
- Attach file-level edges: `has_filename`, `has_language`, `has_origin_repo` (if known), `has_origin_path`, `has_license_per_file` (per-file license preserved from sources like The Stack v2).
- Return `code_file` hash as the call's root.

## Tree-sitter grammar registry

The substrate's tree-sitter grammar registry is documented in `20-technical/16-tree-sitter-grammar-strategy.md`. A representative subset for code decomposition:

| Grammar | Languages | Maintained where | Status |
|---|---|---|---|
| tree-sitter-python | Python 3.x | tree-sitter org | Mature |
| tree-sitter-rust | Rust | tree-sitter org | Mature |
| tree-sitter-typescript | TypeScript + TSX | tree-sitter org | Mature |
| tree-sitter-javascript | JavaScript + JSX | tree-sitter org | Mature |
| tree-sitter-go | Go | tree-sitter org | Mature |
| tree-sitter-c | C | tree-sitter org | Mature |
| tree-sitter-cpp | C++ | tree-sitter org | Mature |
| tree-sitter-java | Java | tree-sitter org | Mature |
| tree-sitter-c-sharp | C# | tree-sitter org | Mature |
| tree-sitter-sql | SQL (multiple dialects) | community | Mature |
| tree-sitter-markdown | Markdown CommonMark | tree-sitter org | Has CommonMark edge cases |
| tree-sitter-html | HTML | tree-sitter org | Mature |
| tree-sitter-css | CSS | tree-sitter org | Mature |
| tree-sitter-json | JSON | tree-sitter org | Mature |
| tree-sitter-yaml | YAML | community | Mature |
| tree-sitter-toml | TOML | community | Mature |
| tree-sitter-bash | Bash | tree-sitter org | Mature |
| tree-sitter-dockerfile | Dockerfile | community | Mature |
| tree-sitter-nix | Nix | community | Mature |
| tree-sitter-haskell | Haskell | community | Mature |
| tree-sitter-ocaml | OCaml | community | Mature |
| tree-sitter-elixir | Elixir | community | Mature |
| tree-sitter-erlang | Erlang | community | Mature |
| tree-sitter-lean4 | Lean 4 | TO AUTHOR or fork community WIP | For Mathlib ingestion |
| tree-sitter-coq | Coq | community | Mature |

305+ grammars total via tree-sitter-language-pack. Substrate's bundled set is configurable per deployment.

## Entity type registration per grammar

When a grammar is registered with the substrate, every named AST node type produces a corresponding `ref.entity_type` row:

```sql
-- After registering tree-sitter-python:
INSERT INTO ref.entity_type (code, modality, description) VALUES
  ('ts_python_module', 'code', 'Python module (file root)'),
  ('ts_python_function_definition', 'code', 'Python function definition'),
  ('ts_python_class_definition', 'code', 'Python class definition'),
  ('ts_python_if_statement', 'code', 'Python if statement'),
  ('ts_python_for_statement', 'code', 'Python for loop'),
  ('ts_python_call', 'code', 'Python function call'),
  ('ts_python_binary_expression', 'code', 'Python binary expression'),
  ('ts_python_identifier', 'code', 'Python identifier'),
  -- ... ~80 entity types total for Python grammar
ON CONFLICT (code) DO NOTHING;
```

Substrate's grammar-registration tooling produces these INSERTs from the grammar's `node-types.json` automatically. Manual registration is not required for already-supported grammars.

## Edge role registration per grammar

Tree-sitter field names become `ref.edge_role` entries:

```sql
INSERT INTO ref.edge_role (code) VALUES
  ('name'),
  ('body'),
  ('parameters'),
  ('condition'),
  ('consequence'),
  ('alternative'),
  ('arguments'),
  ('left'),
  ('right'),
  ('operator'),
  -- ... etc.
ON CONFLICT (code) DO NOTHING;
```

These are universal across grammars; many languages share role names (`name`, `body`, `parameters`). Where a grammar uses unique field names, those are registered too.

## Cross-language convergence

Because content-addressed identity is determined by the AST's structure plus the leaf token text, identical code across languages does NOT produce identical substrate hashes (even if the bytes are the same), because the entity types differ (`ts_python_function_definition` vs `ts_javascript_function_declaration`). This is correct — the same source bytes parsed under two different grammars are two genuinely different ASTs.

What DOES converge:
- The string "hello" appearing as a `string_literal` in Python and as a `string_literal` in JavaScript both decompose to the SAME `text_composition` hash via the text decomposer (their bytes are identical). The leaf-token text content converges; the structural AST does not.
- A function named `foo` in Python and a function named `foo` in JavaScript share the SAME `text_composition` for the identifier `foo` but DIFFERENT compositions for the function definition (because `ts_python_function_definition` ≠ `ts_javascript_function_declaration`).
- Cross-language semantic equivalence (the Python `def foo(): pass` and JavaScript `function foo() {}` doing essentially the same thing) is captured at a HIGHER tier via `equivalent_to` edges that cross-corroborate per-language ASTs through the substrate's accumulated knowledge. This is not a decomposer-time computation; it emerges from arena dynamics.

## Embedded code in text (Mode 2)

When the text decomposer encounters a fenced code block (Markdown ```python ... ```, RST `..code-block::`, etc.):

1. Text decomposer parses the document via tree-sitter-markdown (or appropriate document grammar).
2. The grammar's AST contains a `fenced_code_block` node with an `info_string` field (`python`) and a `code_fence_content` field.
3. Text decomposer's mapping function recognizes the `fenced_code_block`: extract `info_string` to identify language, extract `code_fence_content` bytes.
4. Recursively call code decomposer on the code bytes with declared language = info_string.
5. Code decomposer returns its file-level hash.
6. Text decomposer attaches an `embeds_code` edge from the `fenced_code_block` text composition to the code's `code_file` composition.

The result is a hierarchical structure: document text_composition → paragraphs → some paragraphs contain code_block text_compositions → those embed code_file compositions whose internal structure is the code's typed AST.

Customers querying "find functions named X" can traverse from a document's text composition through `embeds_code` to the file's AST, and find function_definition nodes whose `name` field text matches X. All via SQL traversal.

## Performance characteristics

| Input | Latency target |
|---|---|
| Small Python file (100 lines, ~3KB) | <10 ms |
| Medium Python file (1000 lines, ~30KB) | ~50–100 ms |
| Large Python file (10000 lines, ~300KB) | ~500 ms – 1s |
| Repository with 1000 files (~5MB total) | ~30–60 s (parallelized via pipeline) |

Latency components:
- Tree-sitter parse: linear in file size; very fast (~MB/s)
- AST walk: linear in node count; fast
- Substrate INSERT: bulk-batched via pipeline; negligible per-call
- Recursive text decompose for tokens: dominates total cost (each identifier/literal goes through text decomposer)

For bulk repository ingestion, the pipeline parallelizes file-level work; throughput typically >100 files/sec/core.

## Validation gates

- **D-tree-sitter-grammar-roundtrip**: parse a representative sample file, walk the AST, reconstruct source bytes from the AST (printing the substrate's stored leaf tokens in order). Result must equal input bytes.
- **D-cross-language-tokens**: a string literal `"hello"` in Python and JavaScript produces the SAME `text_composition` for `hello`, even though the surrounding `string_literal` entities are language-typed.
- **D-error-recovery**: parse syntactically-broken Python; verify ERROR nodes are emitted; verify substrate state still contains the recoverable AST portions; total entities emitted should be > 0.
- **D-file-roundtrip**: ingest a file; query the substrate for that file's AST; reconstruct source from substrate state; compare to original.
- **D-determinism**: same file + same grammar version → byte-identical entity row set.

## Failure modes

- **`language_undetected`**: no caller hint, no extension, no shebang. Caller can pass `assume_language => 'plaintext'` to fall back to text decomposer.
- **`grammar_not_loaded`**: declared language has no registered tree-sitter grammar. Caller-resolvable by registering grammar; otherwise raises with grammar registration instructions.
- **`tree_sitter_parse_error`**: tree-sitter's internal parser failed catastrophically (rare; usually tree-sitter recovers via ERROR nodes). Logged with parse position; partial AST emitted.
- **`exceeded_recursion_depth`**: AST depth exceeds substrate's safety limit (default 200). Logged; deeply-nested AST partially emitted with truncation note.

## Extending: adding a new language

1. Build/install the tree-sitter grammar (compiled .so/.dll/.dylib from `grammar.js`).
2. Register entity types from the grammar's `node-types.json` via the substrate's grammar-registration tool.
3. (Optional) Author per-grammar mapping overrides if some node types should map to specialized substrate entity types beyond the auto-generated `ts_<lang>_<node_type>` pattern (e.g., language-specific semantic groupings).
4. Add validation gates: representative sample file roundtrip, cross-language token convergence test.
5. Document the addition in `20-technical/16-tree-sitter-grammar-strategy.md`.

## Cross-references

- Tree-sitter strategy (broader context): `20-technical/16-tree-sitter-grammar-strategy.md`
- Text decomposer (called recursively by code decomposer for token text): `20-technical/02-text-decomposer.md`
- Decomposer contract: `10-architecture/05-decomposer-contract.md`
- Substrate Law 5 (decomposers as pure producers): `10-architecture/01-substrate-laws.md`
- Identity / Merkle hashing: `10-architecture/02-identity-and-convergence.md`

## External references

- Tree-sitter docs: <https://tree-sitter.github.io/tree-sitter/>
- Tree-sitter language pack (305+ grammars): <https://github.com/kreuzberg-dev/tree-sitter-language-pack>
- Tree-sitter grammar DSL: <https://tree-sitter.github.io/tree-sitter/creating-parsers/>
