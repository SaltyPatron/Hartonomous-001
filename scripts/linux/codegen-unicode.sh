#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

ucd_root="$UCD_ROOT"
force=false

while (($#)); do
    case "$1" in
        --ucd-root)
            ucd_root="${2:?missing UCD root}"
            shift 2
            ;;
        --force)
            force=true
            shift
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/codegen-unicode.sh [--ucd-root PATH] [--force]
Regenerate embedded UCD/UCA extension tables from authoritative Unicode source files. This is offline codegen, not build or seed.

Canonical generator: ext/libhartonomous/codegen/gen_ucd_flat.c walks
ucd.all.flat.xml (UAX #42) and emits ext/hartonomous_pg/src/generated/
pg_ucd_segmentation.{c,h}. Property-value short aliases (GCB/WB/SB) are
resolved against PropertyValueAliases.txt internally.

Pre-gen ≠ substrate ingestion. This produces the build-time client-side
perf cache; substrate-content ingestion is a separate runtime path
(decomposers reading source files directly).
USAGE
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

require_cmd cmake
require_cmd ninja
require_cmd unzip

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
ucd_xml_zip="$ucd_root/ucdxml/ucd.all.flat.zip"

if [[ ! -f "$ucd_xml_zip" ]]; then
    die "Missing UCD source: $ucd_xml_zip — set HARTONOMOUS_UCD_ROOT or pass --ucd-root to a complete UCD tree"
fi

build_dir="$repo_root/ext/libhartonomous/build"
if [[ ! -d "$build_dir" ]]; then
    info "Configuring libhartonomous build dir (first run)"
    mkdir -p "$build_dir"
    (cd "$build_dir" && cmake -G Ninja -DHARTONOMOUS_BUILD_CODEGEN=ON "$repo_root/ext/libhartonomous")
fi

# Ensure codegen target is enabled (idempotent reconfigure if user disabled it).
(cd "$build_dir" && cmake -DHARTONOMOUS_BUILD_CODEGEN=ON . >/dev/null)

info "Building gen_ucd_flat"
(cd "$build_dir" && ninja gen_ucd_flat)

generator="$build_dir/bin/gen_ucd_flat"
[[ -x "$generator" ]] || die "gen_ucd_flat build did not produce $generator"

# Extract flat XML into a scratch dir.
extract_dir="$build_dir/_unicode_extracted"
flat_xml="$extract_dir/ucd.all.flat.xml"
if [[ "$force" == true ]] || [[ ! -f "$flat_xml" ]] || [[ "$ucd_xml_zip" -nt "$flat_xml" ]]; then
    info "Extracting $ucd_xml_zip → $extract_dir"
    mkdir -p "$extract_dir"
    (cd "$extract_dir" && unzip -o "$ucd_xml_zip" >/dev/null)
fi

out_dir="$repo_root/ext/hartonomous_pg/src/generated"
mkdir -p "$out_dir"

newest_source="$(stat -c '%Y' "$flat_xml")"
oldest_generated=""
if [[ -f "$out_dir/pg_ucd_segmentation.c" ]] && [[ -f "$out_dir/pg_ucd_segmentation.h" ]]; then
    oldest_generated="$(find "$out_dir/pg_ucd_segmentation.c" "$out_dir/pg_ucd_segmentation.h" -printf '%T@\n' | sort -n | head -n 1)"
fi

if [[ "$force" == false ]] && [[ -n "$oldest_generated" ]]; then
    if awk "BEGIN { exit !($oldest_generated >= $newest_source) }"; then
        info "Generated Unicode segmentation tables are up to date"
        exit 0
    fi
fi

info "Regenerating Unicode segmentation tables from $flat_xml"
"$generator" "$flat_xml" "$out_dir"
[[ -f "$out_dir/pg_ucd_segmentation.c" ]] || die "Generator completed but pg_ucd_segmentation.c is missing"
[[ -f "$out_dir/pg_ucd_segmentation.h" ]] || die "Generator completed but pg_ucd_segmentation.h is missing"
info "Wrote $out_dir/pg_ucd_segmentation.{c,h}"
