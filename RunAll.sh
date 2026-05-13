#!/usr/bin/env bash
set -Eeuo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

source "$ROOT/scripts/linux/common.sh"

HART="$ROOT/scripts/hart"
RUN_ID="${HARTONOMOUS_RUNALL_ID:-$(date -u +%Y%m%dT%H%M%SZ)}"
LOG_ROOT="${HARTONOMOUS_RUNALL_LOG_ROOT:-logs/runall}"
RUN_DIR="$LOG_ROOT/$RUN_ID"
SUMMARY="$RUN_DIR/summary.tsv"
FAIL_TAIL_LINES="${HARTONOMOUS_RUNALL_FAIL_TAIL_LINES:-120}"

skip_preflight=false
skip_clean=false
skip_codegen=false
skip_build=false
skip_db_reset=false
skip_seed=false
skip_status=false
with_model=false
with_tests=false
tee_stdout=false
dry_run=false
codegen_force=true
build_clean=true
install_mode="${HARTONOMOUS_RUNALL_INSTALL_MODE:-copy}"
source_root=""
model_source=""
ucd_root=""

usage() {
        sed 's/^|//' <<'USAGE'
|Hartonomous full Linux reset/rebuild/reseed runner. No PowerShell.
|
|Usage:
|  ./RunAll.sh [options]
|
|Default flow:
|  preflight -> clean -> codegen unicode --force -> build native/sql/dotnet/pg-extension
|  -> db reset --force -> seed phases --no-build -> seed validate -> ops status
|
|Each top-level build/reset/seed phase writes its own log under logs/runall/<run-id>/.
|
|Options:
|  --source PATH          Seed source root passed to each scripts/hart seed phase.
|  --model-source PATH    Override the model hub root for ModelDecomp (independent of --source).
|                         Defaults to $HARTONOMOUS_PATHS__MODELSOURCE / $HARTONOMOUS_MODEL_SOURCE,
|                         else falls back to Decomposers.Safetensors.HubPath in appsettings.
|  --ucd-root PATH        UCD root passed to scripts/hart codegen unicode.
|  --with-model           Include the ModelDecomp seed phase.
|  --with-tests           Also run native/unit tests after build and smoke/integration around DB work.
|  --install-mode MODE    PG extension install mode: copy or symlink. Default: copy.
|  --log-root PATH        Parent log directory. Default: logs/runall.
|  --run-id ID            Stable run id. Default: UTC timestamp.
|  --tee                  Stream stdout as well as stderr while logging.
|  --dry-run              Print and log the step plan without executing commands.
|
|Skip flags:
|  --skip-preflight
|  --skip-clean
|  --skip-codegen
|  --skip-build
|  --skip-db-reset
|  --skip-seed
|  --skip-status
|
|Tuning:
|  --no-codegen-force     Do not pass --force to Unicode codegen.
|  --no-build-clean       Do not pass --clean to native / PG extension builds.
|
|Examples:
|  ./RunAll.sh
|  ./RunAll.sh --source /vault/Data --with-model
|  ./RunAll.sh --skip-codegen --skip-clean --source /vault/Data
|  ./RunAll.sh --install-mode symlink --with-tests
USAGE
}

quote_command() {
    printf '%q ' "$@"
}

write_run_metadata() {
    mkdir -p "$RUN_DIR"
    ln -sfn "$RUN_ID" "$LOG_ROOT/latest"

    {
        printf 'run_id=%s\n' "$RUN_ID"
        printf 'started_at=%s\n' "$(date -Iseconds)"
        printf 'root=%s\n' "$ROOT"
        printf 'db_runtime=%s\n' "$DB_RUNTIME"
        printf 'postgres_host=%s\n' "$POSTGRES_HOST"
        printf 'postgres_port=%s\n' "$POSTGRES_PORT"
        printf 'postgres_user=%s\n' "$POSTGRES_USER"
        printf 'postgres_db=%s\n' "$POSTGRES_DB"
        printf 'source_root=%s\n' "${source_root:-$SOURCE_ROOT}"
        printf 'model_source=%s\n' "${model_source:-$MODEL_SOURCE}"
        printf 'ucd_root=%s\n' "${ucd_root:-$UCD_ROOT}"
        printf 'dotnet_configuration=%s\n' "$DOTNET_CONFIGURATION"
        printf 'native_configuration=%s\n' "$NATIVE_CONFIGURATION"
        printf 'install_mode=%s\n' "$install_mode"
        printf 'with_model=%s\n' "$with_model"
        printf 'with_tests=%s\n' "$with_tests"
        printf 'dry_run=%s\n' "$dry_run"
    } > "$RUN_DIR/run.env"

    printf 'step\tname\tstatus\telapsed_seconds\tlog\n' > "$SUMMARY"
}

step_index=0
run_step() {
    local name="$1"
    shift

    local ordinal log start elapsed status cmd_display
    ordinal="$(printf '%02d' "$step_index")"
    step_index=$((step_index + 1))
    log="$RUN_DIR/${ordinal}-${name}.log"
    cmd_display="$(quote_command "$@")"

    printf '\n==> [%s] %s\n' "$ordinal" "$name"
    printf '    command: %s\n' "$cmd_display"
    printf '    log:     %s\n' "$log"

    {
        printf 'step=%s\n' "$name"
        printf 'started_at=%s\n' "$(date -Iseconds)"
        printf 'command=%s\n\n' "$cmd_display"
    } > "$log"

    start="$SECONDS"
    if [[ "$dry_run" == true ]]; then
        printf 'dry_run=true\n' >> "$log"
        printf '%s\t%s\tdry-run\t0\t%s\n' "$ordinal" "$name" "$log" >> "$SUMMARY"
        printf '==> [%s] %s dry-run\n' "$ordinal" "$name"
        return 0
    fi

    set +e
    if [[ "$tee_stdout" == true ]]; then
        "$@" > >(tee -a "$log") 2> >(tee -a "$log" >&2)
        status=$?
    else
        "$@" >> "$log" 2> >(tee -a "$log" >&2)
        status=$?
    fi
    set -e
    elapsed=$((SECONDS - start))

    {
        printf '\nfinished_at=%s\n' "$(date -Iseconds)"
        printf 'status=%s\n' "$status"
        printf 'elapsed_seconds=%s\n' "$elapsed"
    } >> "$log"

    if ((status != 0)); then
        printf '%s\t%s\tfailed:%s\t%s\t%s\n' "$ordinal" "$name" "$status" "$elapsed" "$log" >> "$SUMMARY"
        printf '\nERROR: step failed: %s (exit %s)\n' "$name" "$status" >&2
        printf 'Log: %s\n' "$log" >&2
        printf '\nLast %s log lines:\n' "$FAIL_TAIL_LINES" >&2
        tail -n "$FAIL_TAIL_LINES" "$log" >&2 || true
        exit "$status"
    fi

    printf '%s\t%s\tok\t%s\t%s\n' "$ordinal" "$name" "$elapsed" "$log" >> "$SUMMARY"
    printf '==> [%s] %s completed in %ss\n' "$ordinal" "$name" "$elapsed"
}

while (($#)); do
    case "$1" in
        --source) source_root="${2:?missing source path}"; shift 2 ;;
        --model-source) model_source="${2:?missing model source}"; shift 2 ;;
        --ucd-root) ucd_root="${2:?missing UCD root}"; shift 2 ;;
        --with-model) with_model=true; shift ;;
        --with-tests) with_tests=true; shift ;;
        --install-mode) install_mode="${2:?missing install mode}"; shift 2 ;;
        --log-root) LOG_ROOT="${2:?missing log root}"; RUN_DIR="$LOG_ROOT/$RUN_ID"; SUMMARY="$RUN_DIR/summary.tsv"; shift 2 ;;
        --run-id) RUN_ID="${2:?missing run id}"; RUN_DIR="$LOG_ROOT/$RUN_ID"; SUMMARY="$RUN_DIR/summary.tsv"; shift 2 ;;
        --tee) tee_stdout=true; shift ;;
        --dry-run) dry_run=true; shift ;;
        --skip-preflight) skip_preflight=true; shift ;;
        --skip-clean) skip_clean=true; shift ;;
        --skip-codegen) skip_codegen=true; shift ;;
        --skip-build) skip_build=true; shift ;;
        --skip-db-reset) skip_db_reset=true; shift ;;
        --skip-seed) skip_seed=true; shift ;;
        --skip-status) skip_status=true; shift ;;
        --no-codegen-force) codegen_force=false; shift ;;
        --no-build-clean) build_clean=false; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown argument: $1" ;;
    esac
done

case "$install_mode" in
    copy|symlink) ;;
    *) die "invalid --install-mode: $install_mode" ;;
esac

write_run_metadata

info "RunAll logs: $RUN_DIR"
info "Latest run symlink: $LOG_ROOT/latest"
info "DB target: $DB_RUNTIME $POSTGRES_HOST:$POSTGRES_PORT/$POSTGRES_DB as $POSTGRES_USER"

if [[ "$skip_preflight" == false ]]; then
    run_step preflight "$HART" preflight
fi

if [[ "$skip_clean" == false ]]; then
    run_step clean "$HART" clean --all
fi

if [[ "$skip_codegen" == false ]]; then
    codegen_args=(codegen unicode)
    [[ "$codegen_force" == true ]] && codegen_args+=(--force)
    [[ -n "$ucd_root" ]] && codegen_args+=(--ucd-root "$ucd_root")
    run_step codegen-unicode "$HART" "${codegen_args[@]}"
fi

if [[ "$skip_build" == false ]]; then
    native_args=(build native)
    pg_extension_args=(build pg-extension --install-mode "$install_mode")
    if [[ "$build_clean" == true ]]; then
        native_args+=(--clean)
        pg_extension_args+=(--clean)
    fi

    run_step build-native "$HART" "${native_args[@]}"
    run_step build-extension-sql "$HART" build extension-sql
    run_step build-dotnet "$HART" build dotnet
    run_step build-pg-extension "$HART" "${pg_extension_args[@]}"

    if [[ "$with_tests" == true ]]; then
        run_step test-native "$HART" test native
        run_step test-unit "$HART" test unit --no-build
    fi
fi

if [[ "$skip_db_reset" == false ]]; then
    run_step db-reset "$HART" db reset --force

    if [[ "$with_tests" == true ]]; then
        run_step test-smoke "$HART" test smoke --no-build
    fi
fi

if [[ "$skip_seed" == false ]]; then
    seed_common=()
    [[ -n "$source_root" ]] && seed_common+=(--source "$source_root")
    effective_model_source="${model_source:-$MODEL_SOURCE}"
    [[ -n "$effective_model_source" ]] && seed_common+=(--model-source "$effective_model_source")
    [[ "$skip_build" == false ]] && seed_common+=(--no-build)

    for phase_spec in \
        UcdUca:seed-ucd-uca \
        Iso639:seed-iso-639 \
        WordNetOmw:seed-wordnet-omw \
        UniversalDeps:seed-universal-deps \
        Wiktionary:seed-wiktionary \
        Tatoeba:seed-tatoeba
    do
        phase="${phase_spec%%:*}"
        step="${phase_spec#*:}"
        run_step "$step" "$HART" seed "$phase" "${seed_common[@]}"
    done

    if [[ "$with_model" == true ]]; then
        run_step seed-model-decomp "$HART" seed ModelDecomp "${seed_common[@]}"
    fi

    run_step seed-validate "$HART" seed validate

    if [[ "$with_tests" == true ]]; then
        run_step test-integration "$HART" test integration --no-build
    fi
fi

if [[ "$skip_status" == false ]]; then
    run_step ops-status "$HART" ops status
fi

info "RunAll completed"
info "Summary: $SUMMARY"
