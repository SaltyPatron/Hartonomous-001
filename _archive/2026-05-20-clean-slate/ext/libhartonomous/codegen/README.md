# libhartonomous codegen

Self-contained C tools that read canonical Unicode / ISO / CLDR data sources
and emit static `lh_*.{h,c}` table files into `../src/generated/`. The library
links those files. No Python. No external runtime libraries beyond the C
standard library and libblake3 (statically linked).

## Layout

| File | Role |
|------|------|
| `xml_pull.c` / `xml_pull.h` | Hand-written XML 1.0 pull parser. Single token at a time, no DOM, no allocations beyond the user-provided buffer. Sufficient for the UCD/Unihan grouped XML dialect. |
| `lh_emit.c` / `lh_emit.h` | Helpers to write generated `lh_*.h` and `lh_*.c` files with deterministic byte order, ≤25 MB per file (rolls over with `_part2.c`, `_part3.c`, …). |
| `lh_input.c` / `lh_input.h` | Mmap helpers for reading source files. |
| `gen_codepoint_hashes.c` | Iterates `U+0000..U+10FFFF`, emits BLAKE3 of UTF-8 bytes per assigned codepoint. No XML input — purely synthetic. |
| `gen_ucd_flat.c` | Reads `ucd.all.flat.xml` (UAX #42 flat form) → `../../hartonomous_pg/src/generated/pg_ucd_segmentation.{c,h}`. Resolves GCB / WB / SB short property-value aliases against PropertyValueAliases.txt internally. Canonical per project rule `.claude/rules/00-hartonomous-core.md` ("XML-flat for per-codepoint UCD pre-gen"). |
| `gen_unihan_grouped.c` | Reads `ucd.unihan.grouped.xml` → `lh_unihan.{h,c}`. |
| `gen_uca.c` | Reads `allkeys.txt` and CLDR tailorings → `lh_uca_ducet.{h,c}` + `lh_uca_tailorings.{h,c}`. |
| `gen_uax14_pairtable.c` | Reads `LineBreak.txt` → `lh_uax14.{h,c}`. |
| `gen_uax9_brackets.c` | Reads `BidiBrackets.txt` → `lh_uax9_brackets.{h,c}`. |
| `gen_idna_map.c` | Reads `IdnaMappingTable.txt` → `lh_idna.{h,c}`. |
| `gen_confusables.c` | Reads `confusables.txt` → `lh_confusables.{h,c}`. |
| `gen_iso_codes.c` | Curated ISO 639/15924/3166 lists → `lh_iso_codes.{h,c}`. |
| `gen_cldr_static.c` | Curated CLDR likelySubtags + locale meta → `lh_cldr_*.{h,c}`. |
| `gen_full_case.c` | Reads `SpecialCasing.txt` + `CaseFolding.txt` → `lh_full_case.{h,c}`. |
| `lh_inputs.h` / `gen_inputs.c` | Per-source SHA-256 + version manifest baked into the library. |

## Targets

CMake exposes three umbrella targets:

* `codegen` — builds every generator executable.
* `generate` — runs every generator, writing into `../src/generated/`.
* `gen_check` — runs every generator into a scratch dir and `cmp`-diffs against
  the committed files. Used in CI to fail on unregenerated tables.

Hand-edits to `src/generated/` are forbidden. The generators are the spec; if a
table is wrong, fix the generator.

## Determinism

* All output is ASCII, LF-terminated, UTF-8 byte-order-mark-free.
* Numeric literals use lower-case hex with `0x` prefix and zero-padded width.
* Arrays are sorted by key (codepoint, range start, etc.), never by hash bucket.
* No per-invocation timestamps, no PRNG state, no environment-dependent paths.
* `lh_inputs` records the SHA-256 of each input file so a regenerated table is
  bit-identical iff its input is bit-identical.
