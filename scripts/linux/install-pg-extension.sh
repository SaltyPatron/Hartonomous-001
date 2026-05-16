#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

mode=copy
native_only=false
user_prefix=""
target_db="${HARTONOMOUS_DB_NAME:-hartonomous}"
db_host="${HARTONOMOUS_DB_HOST:-/var/run/postgresql}"

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
        --target-db)
            target_db="${2:?missing target database name}"
            shift 2
            ;;
        --native-only)
            native_only=true
            shift
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/install-pg-extension.sh [--copy|--symlink|--user[-prefix DIR]|--mode copy|symlink] [--target-db NAME] [--native-only]

Install Hartonomous PostgreSQL runtime artifacts into PostgreSQL's system
directories OR into a user-local prefix that requires no sudo.

Modes:
  copy     Secure/default. Copies root-owned artifacts into PostgreSQL's
           pkglibdir and sharedir/extension. Uses sudo only when needed.
  symlink  Dev loop. Creates system-directory symlinks back to this checkout.
           Faster, but the extension then follows mutable files in the repo.
  user     Per-user install into $HOME/.local/pg-hartonomous/ (or
           $HARTONOMOUS_USER_PREFIX). No sudo required. After --user install:
             1. The .so / .control / .sql files land in <prefix>/lib and
                <prefix>/share/extension.
             2. The extension .so runpath is rewritten via chrpath/patchelf
                so libhartonomous.so resolves from <prefix>/lib.
             3. PostgreSQL's per-database GUCs extension_control_path +
                dynamic_library_path are set on the target database (default
                hartonomous; override with --target-db NAME or
                HARTONOMOUS_DB_NAME env). This is what removes the need to
                pass PGOPTIONS at every connect — any client connecting to
                the target database picks up the user-prefix paths
                automatically.

Environment:
  HARTONOMOUS_PG_CONFIG     Override pg_config path.
  HARTONOMOUS_SUDO          Override sudo command, e.g. "doas" or "sudo -n".
  HARTONOMOUS_USER_PREFIX   Override default --user prefix.
  HARTONOMOUS_DB_NAME       Override default target database (hartonomous).
  HARTONOMOUS_DB_HOST       Override psql host (default /var/run/postgresql).
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

# Returns 0 (success) when the install can write to $path WITHOUT sudo —
# i.e. no privilege escalation needed. Returns 1 when we must escalate.
#
# Three cases qualify as "no sudo":
#   1. Path exists and is directly writable (we own it, or our group has w).
#   2. Path doesn't exist and parent dir is writable.
#   3. Path exists, NOT directly writable, BUT parent dir is writable —
#      `install` replaces via unlink + create, which only needs dir-write.
#      This case covers the common "file is root:root 755 in a group-775
#      dir we belong to" layout: ahart can replace the file via the dir
#      without owning it.
#
# Name predates the rewrite; semantics are "can write unprivileged".
needs_privilege() {
    local path="$1"
    if [[ -e "$path" && -w "$path" ]]; then
        return 0
    fi
    if [[ -w "$(dirname "$path")" ]]; then
        return 0
    fi
    return 1
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
    # Configure the target database's per-database GUCs so any connection
    # automatically picks up the user-prefix paths. This removes the need
    # for PGOPTIONS / connection-string options= at every connect.
    info "Configuring per-database extension_control_path + dynamic_library_path on database \"$target_db\" (host=$db_host)"
    if ! psql -h "$db_host" -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$target_db'" 2>/dev/null | grep -q '^1$'; then
        info "Database \"$target_db\" does not exist yet; creating it"
        psql -h "$db_host" -d postgres -c "CREATE DATABASE $target_db" >/dev/null
    fi
    psql -h "$db_host" -d postgres -v ON_ERROR_STOP=1 <<SQL
ALTER DATABASE $target_db SET extension_control_path = '$user_prefix/share:\$system';
ALTER DATABASE $target_db SET dynamic_library_path = '$user_prefix/lib:\$libdir';
SQL
    info "Verifying GUCs on database \"$target_db\""
    psql -h "$db_host" -d "$target_db" -c "SHOW extension_control_path; SHOW dynamic_library_path;" 2>&1 | sed 's/^/    /'

    echo
    info "User-local install complete."
    info "Database \"$target_db\" is configured — no PGOPTIONS needed at connect time."
    cat <<EOF

  Connect directly:

    psql -h $db_host -d $target_db
    scripts/hart phase run --phase UcdUca

  (If you need to connect from a database OTHER than \"$target_db\", or to
  override at one connect, you can still pass:
     PGOPTIONS="-c extension_control_path=$user_prefix/share:\\\$system -c dynamic_library_path=$user_prefix/lib:\\\$libdir"
   but the default-database path no longer requires it.)

EOF
fi
