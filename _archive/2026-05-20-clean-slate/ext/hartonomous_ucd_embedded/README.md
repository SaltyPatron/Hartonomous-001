# hartonomous_ucd_embedded

Substrate-compatible UCD/UCA atom lookup, no PostgreSQL deps. For embedded
targets, CLI tools, or any caller that needs the same per-codepoint
BLAKE3 hash / 4D Super-Fibonacci centroid / Hilbert code / reverse hash
lookup that the Hartonomous PG extension provides, but cannot link against
PostgreSQL.

## Why this exists

The PG extension and the embedded library both consume the same on-disk
binary blob (per-block files + global reverse table + index) produced by
the codegen tools under `ext/libhartonomous/codegen/` (canonical generator:
`gen_ucd_flat.c`, walks `ucd.all.flat.xml`). Same bytes, same answers,
deterministic across platforms.

## Build

```bash
cmake -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build
```

Outputs:
- `build/libhartonomous_ucd_embedded.a` — static library
- `build/huc` — reference CLI
- `build/huc_parity` — parity test (when `-DHUCD_BUILD_PARITY_TEST=ON`)

## Footprint

| Profile | Lib size | Blob size (full) | Blob size (subset) |
|---|---|---|---|
| Full (server / desktop) | ~80 KB | ~91 MB | n/a |
| Tier-1 only | ~80 KB | ~10 MB | n/a |
| Embedded (only ASCII block) | ~80 KB | n/a | ~9 KB blob + ~26 KB index |
| Embedded (only ASCII + Latin-1 + Latin Extended A/B) | ~80 KB | n/a | ~50 KB blob + index |

## API

See `include/hartonomous_ucd.h`. Microsecond-or-better steady-state per-cp
lookup once a block file is mmap'd; ~50 µs first-touch per block.

## Quickstart

```bash
# After running the generator in the main repo:
huc /path/to/ext/hartonomous_pg/src/generated info
huc /path/to/ext/hartonomous_pg/src/generated hash 0x4E2D    # 中
huc /path/to/ext/hartonomous_pg/src/generated tier 0x1F600   # 😀
huc /path/to/ext/hartonomous_pg/src/generated centroid 0x0041 # A
huc /path/to/ext/hartonomous_pg/src/generated from-hash <64-hex-chars>
```
