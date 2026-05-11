#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

mode=copy
native_only=false

while (($#)); do
    case "$1" in
        --mode)
            mode="${2:?missing install mode}"
            shift 2
            ;;
        --copy)
            mode=copy
            shift
            ;;
        --symlink)
            mode=symlink
            shift
            ;;
        --native-only)
            native_only=true
            shift
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/install-pg-extension.sh [--copy|--symlink|--mode copy|symlink] [--native-only]

Install Hartonomous PostgreSQL runtime artifacts into the local PostgreSQL
returned by pg_config.

Modes:
  copy     Secure/default. Copies root-owned artifacts into PostgreSQL's
           pkglibdir and sharedir/extension. Uses sudo only when needed.
  symlink  Dev loop. Creates system-directory symlinks back to this checkout.
           Faster, but the extension then follows mutable files in the repo.

Environment:
  HARTONOMOUS_PG_CONFIG   Override pg_config path.
  HARTONOMOUS_SUDO        Override sudo command, e.g. "doas" or "sudo -n".
USAGE
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

case "$mode" in
    copy|symlink) ;;
    *) die "invalid install mode: $mode" ;;
esac

pg_config="$(pg_config_bin)"
[[ -n "$pg_config" ]] || die "required command not found: pg_config"

pkglibdir="$("$pg_config" --pkglibdir)"
sharedir="$("$pg_config" --sharedir)"
extensiondir="$sharedir/extension"
[[ -d "$pkglibdir" ]] || die "pg_config --pkglibdir does not exist: $pkglibdir"
[[ -d "$extensiondir" ]] || die "PostgreSQL extension directory does not exist: $extensiondir"

native_lib="$(realpath "$NATIVE_BUILD_DIR/bin/libhartonomous.so")"
[[ -s "$native_lib" ]] || die "native library missing: $native_lib"

extension_so="$(realpath ext/hartonomous_pg/hartonomous.so 2>/dev/null || true)"
control_file="$(realpath ext/hartonomous_pg/hartonomous.control)"
sql_file="$(realpath ext/hartonomous_pg/sql/hartonomous--1.0.sql)"

needs_privilege() {
    local path="$1"
    [[ -e "$path" && -w "$path" ]] || [[ ! -e "$path" && -w "$(dirname "$path")" ]]
}

sudo_cmd=()
read -r -a sudo_cmd <<< "${HARTONOMOUS_SUDO:-sudo}"

run_privileged() {
    local target="$1"
    shift
    if needs_privilege "$target"; then
        "$@"
    else
        "${sudo_cmd[@]}" "$@"
    fi
}

install_copy() {
    local src="$1"
    local dst="$2"
    local mode_bits="$3"
    run_privileged "$dst" install -m "$mode_bits" "$src" "$dst"
}

install_symlink() {
    local src="$1"
    local dst="$2"
    run_privileged "$dst" ln -sfn "$src" "$dst"
}

install_artifact() {
    local src="$1"
    local dst="$2"
    local mode_bits="$3"
    case "$mode" in
        copy) install_copy "$src" "$dst" "$mode_bits" ;;
        symlink) install_symlink "$src" "$dst" ;;
    esac
}

info "Installing libhartonomous.so into $pkglibdir ($mode)"
install_artifact "$native_lib" "$pkglibdir/libhartonomous.so" 755

if [[ "$native_only" == true ]]; then
    exit 0
fi

[[ -s "$extension_so" ]] || die "PostgreSQL extension binary missing; run scripts/hart build pg-extension first: ext/hartonomous_pg/hartonomous.so"
[[ -s "$control_file" ]] || die "control file missing: ext/hartonomous_pg/hartonomous.control"
[[ -s "$sql_file" ]] || die "extension SQL missing: ext/hartonomous_pg/sql/hartonomous--1.0.sql"

info "Installing hartonomous.so into $pkglibdir ($mode)"
install_artifact "$extension_so" "$pkglibdir/hartonomous.so" 755

info "Installing hartonomous control/sql into $extensiondir ($mode)"
install_artifact "$control_file" "$extensiondir/hartonomous.control" 644
install_artifact "$sql_file" "$extensiondir/hartonomous--1.0.sql" 644

blob_dir="ext/hartonomous_pg/src/generated"
if [[ -f "$blob_dir/hartonomous-ucd-17.0.0.idx" && -f "$blob_dir/hartonomous-ucd-17.0.0.reverse.bin" && -d "$blob_dir/blocks" ]]; then
    target_blob_dir="$extensiondir/hartonomous-ucd"
    info "Installing optional UCD atom blob into $target_blob_dir ($mode)"
    if [[ "$mode" == copy ]]; then
        run_privileged "$target_blob_dir" install -d -m 755 "$target_blob_dir/blocks"
        run_privileged "$target_blob_dir/hartonomous-ucd-17.0.0.idx" install -m 644 "$blob_dir/hartonomous-ucd-17.0.0.idx" "$target_blob_dir/"
        run_privileged "$target_blob_dir/hartonomous-ucd-17.0.0.reverse.bin" install -m 644 "$blob_dir/hartonomous-ucd-17.0.0.reverse.bin" "$target_blob_dir/"
        for block in "$blob_dir"/blocks/*.bin; do
            run_privileged "$target_blob_dir/blocks/$(basename "$block")" install -m 644 "$block" "$target_blob_dir/blocks/"
        done
    else
        run_privileged "$target_blob_dir" ln -sfn "$(realpath "$blob_dir")" "$target_blob_dir"
    fi
else
    info "Optional UCD atom blob artifacts absent; extension will use embedded catalog fallback"
fi
