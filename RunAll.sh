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
with_synth=false
tee_stdout=false
dry_run=false
codegen_force=true
build_clean=true
install_mode="${HARTONOMOUS_RUNALL_INSTALL_MODE:-copy}"
source_root=""
model_source=""
ucd_root=""
synth_template="${HARTONOMOUS_RUNALL_SYNTH_TEMPLATE:-minilm-base}"
synth_vocab_size="${HARTONOMOUS_RUNALL_SYNTH_VOCAB:-256}"
synth_output="${HARTONOMOUS_RUNALL_SYNTH_OUTPUT:-}"  # default set after RUN_DIR resolves
synth_dtype="${HARTONOMOUS_RUNALL_SYNTH_DTYPE:-f32}"
synth_blend="${HARTONOMOUS_RUNALL_SYNTH_BLEND:-}"
synth_recipe=""

usage() {
        sed 's/^|//' <<'USAGE'
|Hartonomous full Linux reset/rebuild/reseed runner. No PowerShell.
|
|Usage:
|  ./RunAll.sh [options]
|
|Default flow (system install):
|  preflight -> clean -> codegen unicode --force -> build native/sql/dotnet/pg-extension
|  -> install pg-extension -> db reset --force -> seed phases --no-build
|  -> seed validate -> ops status [-> synthesize-model when --with-synth]
|
|Permission model:
|  System install writes to /usr/lib/postgresql/18/lib + /usr/share/postgresql/18/extension.
|  Those dirs are mode 775 group postgres-extensions in this layout. If your user
|  is in the postgres-extensions group, NO sudo is needed — install-pg-extension
|  detects parent-dir writability and skips sudo. The only sudo prompts are the
|  delete and the actual install when group membership is absent.
|
|  --user mode writes everything under ~/.local/pg-hartonomous/ and bakes
|  per-database GUCs (extension_control_path + dynamic_library_path) so no
|  system-dir write is ever attempted. NOTE: db reset drops the database,
|  which also drops the per-database GUCs; --user mode in RunAll re-runs
|  install pg-extension AFTER db reset to re-bake them before CREATE EXTENSION.
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
|  --with-synth           After validate, run scripts/hart synthesize-model and verify the export.
|  --install-mode MODE    PG extension install mode: copy | symlink | user. Default: copy.
|  --user                 Shorthand for --install-mode user.
|  --synth-template NAME  Synthesis template: minilm-base | bert-base | llama-small | llama-1b
|                         | llama-3b | qwen-7b | mistral-7b. Default: minilm-base.
|  --synth-vocab-size N   Override synthesis vocab size. Default: 256.
|  --synth-dtype DT       Synthesis output dtype (f32 | f16 | bf16). Default: f32.
|  --synth-blend NAME     Recipe blend (default | encyclopedic | conversational | practitioner
|                         | grammar-tutor).
|  --synth-recipe PATH    Use a custom recipe JSON instead of --synth-template.
|  --synth-output PATH    Synthesis output dir. Default: logs/runall/<run-id>/synth/.
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
|  ./RunAll.sh --user --with-synth                                # user-mode + auto-export
|  ./RunAll.sh --with-synth --synth-template llama-small --synth-vocab-size 512
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
        --with-synth) with_synth=true; shift ;;
        --install-mode) install_mode="${2:?missing install mode}"; shift 2 ;;
        --user) install_mode="user"; shift ;;
        --synth-template) synth_template="${2:?missing template}"; shift 2 ;;
        --synth-vocab-size) synth_vocab_size="${2:?missing vocab size}"; shift 2 ;;
        --synth-dtype) synth_dtype="${2:?missing dtype}"; shift 2 ;;
        --synth-blend) synth_blend="${2:?missing blend}"; shift 2 ;;
        --synth-recipe) synth_recipe="${2:?missing recipe path}"; shift 2 ;;
        --synth-output) synth_output="${2:?missing synth output path}"; shift 2 ;;
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
    copy|symlink|user) ;;
    *) die "invalid --install-mode: $install_mode (expected copy|symlink|user)" ;;
esac

[[ -n "$synth_output" ]] || synth_output="$RUN_DIR/synth"

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
    # build pg-extension internally calls install pg-extension --mode <install_mode>
    # so for system mode this is build + install; for user mode this is build +
    # user-prefix install + per-DB GUC bake.
    run_step build-pg-extension "$HART" "${pg_extension_args[@]}"

    if [[ "$with_tests" == true ]]; then
        run_step test-native "$HART" test native
        run_step test-unit "$HART" test unit --no-build
    fi
fi

if [[ "$skip_db_reset" == false ]]; then
    if [[ "$install_mode" == user ]]; then
        # db reset drops the database, which drops the per-database
        # extension_control_path + dynamic_library_path GUCs that --user
        # mode set. If we let db-reset's default bootstrap run CREATE
        # EXTENSION, it fails because the new DB has no GUCs and the
        # extension isn't in the system search path. Split it:
        #   1. db reset --no-bootstrap   — drop + recreate empty DB
        #   2. install pg-extension --user — re-bake GUCs on the new DB
        #   3. db bootstrap              — CREATE EXTENSION hartonomous
        run_step db-reset "$HART" db reset --force --no-bootstrap
        run_step reinstall-pg-extension-user "$HART" install pg-extension --user
        run_step db-bootstrap "$HART" db bootstrap
    else
        run_step db-reset "$HART" db reset --force
    fi

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

if [[ "$with_synth" == true ]]; then
    mkdir -p "$synth_output"
    synth_args=(synthesize-model --output "$synth_output" --dtype "$synth_dtype")
    if [[ -n "$synth_recipe" ]]; then
        synth_args+=(--recipe "$synth_recipe")
    else
        synth_args+=(--template "$synth_template")
    fi
    [[ -n "$synth_vocab_size" ]] && synth_args+=(--vocab-size "$synth_vocab_size")
    [[ -n "$synth_blend" ]] && synth_args+=(--blend "$synth_blend")

    run_step synthesize-model "$HART" "${synth_args[@]}"

    # Verify the exported package is HF-shape-correct.
    if command -v python3 >/dev/null 2>&1; then
        run_step verify-safetensors python3 -c "
import sys, json, os
from safetensors import safe_open
out = '$synth_output'
need = ['model.safetensors', 'config.json', 'tokenizer.json', 'tokenizer_config.json', 'hartonomous_audit.json']
for f in need:
    p = os.path.join(out, f)
    if not os.path.exists(p):
        print(f'MISSING: {p}', file=sys.stderr); sys.exit(1)
with open(os.path.join(out, 'config.json')) as f:
    cfg = json.load(f)
print(f\"config.architectures: {cfg.get('architectures')}\")
print(f\"config.vocab_size:    {cfg.get('vocab_size')}\")
print(f\"config.hidden_size:   {cfg.get('hidden_size')}\")
with safe_open(os.path.join(out, 'model.safetensors'), framework='numpy') as f:
    keys = list(f.keys())
    print(f'tensor count: {len(keys)}')
    # Inspect the substrate-derived embedding and one attention/FFN slice.
    embed_key = next((k for k in keys if 'embed' in k.lower() and 'weight' in k), None)
    if embed_key:
        t = f.get_tensor(embed_key)
        print(f'embedding {embed_key}: shape={list(t.shape)} mean={float(t.mean()):.4f} std={float(t.std()):.4f}')
    attn_key = next((k for k in keys if 'attention' in k and 'query' in k and 'weight' in k), None) \
            or next((k for k in keys if 'q_proj.weight' in k), None)
    if attn_key:
        t = f.get_tensor(attn_key)
        print(f'attention {attn_key}: shape={list(t.shape)} std={float(t.std()):.4f}')
    ffn_key = next((k for k in keys if ('intermediate' in k or 'up_proj' in k) and 'weight' in k), None)
    if ffn_key:
        t = f.get_tensor(ffn_key)
        print(f'ffn {ffn_key}: shape={list(t.shape)} std={float(t.std()):.4f}')
print('safetensors export verified.')
"
    else
        info "python3 not on PATH; skipping verify-safetensors step"
    fi
fi

info "RunAll completed"
info "Summary: $SUMMARY"
if [[ "$with_synth" == true ]]; then
    info "Synth output: $synth_output"
fi
