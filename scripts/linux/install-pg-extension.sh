#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

mode=copy
native_only=false
user_prefix=""

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
        --user)
            mode=user
            user_prefix="${HARTONOMOUS_USER_PREFIX:-$HOME/.local/pg-hartonomous}"
            shift
            ;;
        --user-prefix)
            mode=user
            user_prefix="${2:?missing prefix}"
            shift 2
            ;;
        --native-only)
            native_only=true
            shift
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/install-pg-extension.sh [--copy|--symlink|--user[-prefix DIR]|--mode copy|symlink] [--native-only]

Install Hartonomous PostgreSQL runtime artifacts into PostgreSQL's system
directories OR into a user-local prefix that requires no sudo.

Modes:
  copy     Secure/default. Copies root-owned artifacts into PostgreSQL's
           pkglibdir and sharedir/extension. Uses sudo only when needed.
  symlink  Dev loop. Creates system-directory symlinks back to this checkout.
           Faster, but the extension then follows mutable files in the repo.
  user     Per-user install into $HOME/.local/pg-hartonomous/ (or
           $HARTONOMOUS_USER_PREFIX). No sudo required. After install,
           connect with:
             PGOPTIONS="-c extension_control_path=<prefix>/share:\$system
                        -c dynamic_library_path=<prefix>/lib:\$libdir"
           or set those GUCs in the connection string options= parameter.
           Also rewrites the extension .so's runpath via chrpath / patchelf
           when present, so libhartonomous.so resolves from <prefix>/lib.

Environment:
  HARTONOMOUS_PG_CONFIG     Override pg_config path.
  HARTONOMOUS_SUDO          Override sudo command, e.g. "doas" or "sudo -n".
  HARTONOMOUS_USER_PREFIX   Override default --user prefix.
USAGE
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

case "$mode" in
    copy|symlink|user) ;;
    *) die "invalid install mode: $mode" ;;
esac

pg_config="$(pg_config_bin)"
[[ -n "$pg_config" ]] || die "required command not found: pg_config"

pkglibdir="$("$pg_config" --pkglibdir)"
sharedir="$("$pg_config" --sharedir)"
extensiondir="$sharedir/extension"
[[ -d "$pkglibdir" ]] || die "pg_config --pkglibdir does not exist: $pkglibdir"
[[ -d "$extensiondir" ]] || die "PostgreSQL extension directory does not exist: $extensiondir"

native_lib_dir="$NATIVE_BUILD_DIR/bin"
native_lib="$native_lib_dir/libhartonomous.so"
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
        user)
            install -m "$mode_bits" "$src" "$dst"
            ;;
    esac
}

if [[ "$mode" == user ]]; then
    [[ -n "$user_prefix" ]] || die "user mode without prefix"
    info "User-local install prefix: $user_prefix"
    mkdir -p "$user_prefix/lib" "$user_prefix/share/extension"
    chmod o+x "$HOME" 2>/dev/null || true
    chmod -R o+rX "$user_prefix" 2>/dev/null || true
    pkglibdir="$user_prefix/lib"
    extensiondir="$user_prefix/share/extension"
fi

info "Installing libhartonomous.so into $pkglibdir ($mode)"
for artifact in "$native_lib_dir"/libhartonomous.so*; do
    [[ -e "$artifact" ]] || continue
    install_artifact "$(realpath "$artifact")" "$pkglibdir/$(basename "$artifact")" 755
done

if [[ "$native_only" == true ]]; then
    exit 0
fi

[[ -s "$extension_so" ]] || die "PostgreSQL extension binary missing; run scripts/hart build pg-extension first: ext/hartonomous_pg/hartonomous.so"
[[ -s "$control_file" ]] || die "control file missing: ext/hartonomous_pg/hartonomous.control"
[[ -s "$sql_file" ]] || die "extension SQL missing: ext/hartonomous_pg/sql/hartonomous--1.0.sql"

info "Installing hartonomous.so into $pkglibdir ($mode)"
install_artifact "$extension_so" "$pkglibdir/hartonomous.so" 755

# For --user installs, also rewrite the runpath of hartonomous.so so that
# libhartonomous.so resolves from the user-local lib dir first. chrpath
# can only shrink/replace within the existing space; patchelf can extend.
if [[ "$mode" == user ]]; then
    new_runpath="$user_prefix/lib:$($pg_config --pkglibdir)"
    if command -v patchelf >/dev/null 2>&1; then
        info "Patching $pkglibdir/hartonomous.so runpath via patchelf"
        patchelf --set-rpath "$new_runpath" "$pkglibdir/hartonomous.so"
    elif command -v chrpath >/dev/null 2>&1; then
        info "Patching $pkglibdir/hartonomous.so runpath via chrpath (may fail if new path is longer than original)"
        chrpath -r "$new_runpath" "$pkglibdir/hartonomous.so" 2>/dev/null || \
            info "chrpath could not extend runpath. Either install patchelf, or rebuild ext/hartonomous_pg with: make USE_PGXS=1 PG_CONFIG=$pg_config SHLIB_LINK=\"-L$pkglibdir -L$($pg_config --pkglibdir) -lhartonomous -Wl,-rpath,$new_runpath\""
    fi
fi

info "Installing hartonomous control/sql into $extensiondir ($mode)"
install_artifact "$control_file" "$extensiondir/hartonomous.control" 644
install_artifact "$sql_file" "$extensiondir/hartonomous--1.0.sql" 644

blob_dir="ext/hartonomous_pg/src/generated"
if [[ -f "$blob_dir/hartonomous-ucd-17.0.0.idx" && -f "$blob_dir/hartonomous-ucd-17.0.0.reverse.bin" && -d "$blob_dir/blocks" ]]; then
    target_blob_dir="$extensiondir/hartonomous-ucd"
    info "Installing optional UCD atom blob into $target_blob_dir ($mode)"
    case "$mode" in
      copy)
        run_privileged "$target_blob_dir" install -d -m 755 "$target_blob_dir/blocks"
        run_privileged "$target_blob_dir/hartonomous-ucd-17.0.0.idx" install -m 644 "$blob_dir/hartonomous-ucd-17.0.0.idx" "$target_blob_dir/"
        run_privileged "$target_blob_dir/hartonomous-ucd-17.0.0.reverse.bin" install -m 644 "$blob_dir/hartonomous-ucd-17.0.0.reverse.bin" "$target_blob_dir/"
        for block in "$blob_dir"/blocks/*.bin; do
            run_privileged "$target_blob_dir/blocks/$(basename "$block")" install -m 644 "$block" "$target_blob_dir/blocks/"
        done
        ;;
      symlink)
        run_privileged "$target_blob_dir" ln -sfn "$(realpath "$blob_dir")" "$target_blob_dir"
        ;;
      user)
        mkdir -p "$target_blob_dir/blocks"
        install -m 644 "$blob_dir/hartonomous-ucd-17.0.0.idx" "$target_blob_dir/"
        install -m 644 "$blob_dir/hartonomous-ucd-17.0.0.reverse.bin" "$target_blob_dir/"
        for block in "$blob_dir"/blocks/*.bin; do
            install -m 644 "$block" "$target_blob_dir/blocks/"
        done
        ;;
    esac
else
    info "Optional UCD atom blob artifacts absent; extension will use embedded catalog fallback"
fi

if [[ "$mode" == user ]]; then
    echo
    info "User-local install complete. Connect via:"
    cat <<EOF

    PGOPTIONS="-c extension_control_path=$user_prefix/share:\\\$system -c dynamic_library_path=$user_prefix/lib:\\\$libdir" \\
        psql -h /var/run/postgresql -d hartonomous

  Or set the connection string options for Hartonomous.Cli:

    Host=/var/run/postgresql;Database=hartonomous;options=-c%20extension_control_path=$user_prefix/share:\\\$system%20-c%20dynamic_library_path=$user_prefix/lib:\\\$libdir

EOF
fi
