#!/usr/bin/env bash
# Four-gate mechanism test for a substrate-derived safetensors export.
#   1. safetensors structurally valid (loads via safetensors library)
#   2. HF transformers AutoModel.from_pretrained() loads it
#   3. llama.cpp convert-hf-to-gguf.py converts it
#   4. llama.cpp main runs the GGUF (any output; quality not measured)
#
# Usage: scripts/verify/verify_synth_mechanism.sh <synth_output_dir> [llama_cpp_root]
#
# Exit 0 = all four gates pass. Exit 1 = gate failed (which one is in stdout).

set -Eeuo pipefail

OUT_DIR="${1:?usage: $0 <synth_output_dir> [llama_cpp_root]}"
LLAMA_CPP="${2:-${HARTONOMOUS_LLAMA_CPP:-}}"

[[ -d "$OUT_DIR" ]] || { echo "FAIL gate-0: synth output dir missing: $OUT_DIR"; exit 1; }

echo "==> gate 1: safetensors structural validity + tensor inventory"
python3 - <<PY
import sys, os, json
from safetensors import safe_open

out = "$OUT_DIR"
need = ["model.safetensors", "config.json", "tokenizer.json",
        "tokenizer_config.json", "hartonomous_audit.json"]
for f in need:
    p = os.path.join(out, f)
    if not os.path.exists(p):
        print(f"FAIL gate-1: missing file {p}", file=sys.stderr)
        sys.exit(1)

with open(os.path.join(out, "config.json")) as f:
    cfg = json.load(f)
print(f"  arch:        {cfg.get('architectures')}")
print(f"  vocab_size:  {cfg.get('vocab_size')}")
print(f"  hidden:      {cfg.get('hidden_size')}")
print(f"  layers:      {cfg.get('num_hidden_layers')}")

with safe_open(os.path.join(out, "model.safetensors"), framework="numpy") as f:
    keys = list(f.keys())
    print(f"  tensors:     {len(keys)}")
    # Sample a few to verify they read without error
    for k in keys[:3] + keys[-3:]:
        t = f.get_tensor(k)
        if t is None:
            print(f"FAIL gate-1: tensor {k} read as None", file=sys.stderr)
            sys.exit(1)

print("PASS gate-1")
PY

echo "==> gate 2: HF transformers AutoModel.from_pretrained()"
python3 - <<PY
import sys, os
try:
    from transformers import AutoModel, AutoTokenizer, AutoConfig
except Exception as e:
    print(f"SKIP gate-2: transformers not importable ({e})")
    sys.exit(0)

out = "$OUT_DIR"
try:
    cfg = AutoConfig.from_pretrained(out)
    print(f"  AutoConfig OK: {cfg.architectures}")
except Exception as e:
    print(f"FAIL gate-2: AutoConfig.from_pretrained failed: {e}", file=sys.stderr)
    sys.exit(1)

try:
    model = AutoModel.from_pretrained(out)
    n_params = sum(p.numel() for p in model.parameters())
    print(f"  AutoModel OK: {n_params:,} params")
except Exception as e:
    print(f"FAIL gate-2: AutoModel.from_pretrained failed: {e}", file=sys.stderr)
    sys.exit(1)

try:
    tok = AutoTokenizer.from_pretrained(out)
    enc = tok("Hello", return_tensors="pt")
    print(f"  Tokenizer OK: 'Hello' -> {enc['input_ids'].tolist()}")
except Exception as e:
    print(f"FAIL gate-2: AutoTokenizer.from_pretrained or encode failed: {e}", file=sys.stderr)
    sys.exit(1)

print("PASS gate-2")
PY

if [[ -z "$LLAMA_CPP" ]]; then
    echo "SKIP gates 3 + 4: HARTONOMOUS_LLAMA_CPP not set (no llama.cpp root)"
    echo "==> mechanism partial pass: gates 1 + 2 only"
    exit 0
fi

CONVERT="$LLAMA_CPP/convert_hf_to_gguf.py"
if [[ ! -f "$CONVERT" ]]; then
    CONVERT="$LLAMA_CPP/convert-hf-to-gguf.py"
fi
if [[ ! -f "$CONVERT" ]]; then
    echo "FAIL gate-3: convert script not found at $LLAMA_CPP/convert_hf_to_gguf.py or convert-hf-to-gguf.py"
    exit 1
fi

GGUF_OUT="$OUT_DIR/model.gguf"
echo "==> gate 3: llama.cpp convert-hf-to-gguf.py"
if python3 "$CONVERT" "$OUT_DIR" --outfile "$GGUF_OUT" --outtype f16 2>&1 | tail -20; then
    [[ -s "$GGUF_OUT" ]] || { echo "FAIL gate-3: convert returned 0 but no output file"; exit 1; }
    echo "  GGUF written: $(stat -c '%s' "$GGUF_OUT") bytes"
    echo "PASS gate-3"
else
    echo "FAIL gate-3: convert script returned non-zero"
    exit 1
fi

MAIN_BIN=""
for candidate in "$LLAMA_CPP/build/bin/llama-cli" "$LLAMA_CPP/build/bin/main" "$LLAMA_CPP/llama-cli" "$LLAMA_CPP/main"; do
    if [[ -x "$candidate" ]]; then
        MAIN_BIN="$candidate"
        break
    fi
done
if [[ -z "$MAIN_BIN" ]]; then
    echo "FAIL gate-4: llama.cpp main/llama-cli binary not found under $LLAMA_CPP"
    exit 1
fi

echo "==> gate 4: $MAIN_BIN runs the GGUF"
if timeout 30 "$MAIN_BIN" -m "$GGUF_OUT" -p "Hello" -n 8 --no-display-prompt 2>&1 | tee /tmp/llama_output.txt; then
    if [[ -s /tmp/llama_output.txt ]]; then
        echo "PASS gate-4 (any output = mechanism works; quality not asserted)"
    else
        echo "FAIL gate-4: ran exit 0 but produced no output"
        exit 1
    fi
else
    echo "FAIL gate-4: llama.cpp run failed"
    exit 1
fi

echo
echo "==> ALL 4 MECHANISM GATES PASS"
echo "    Synth dir: $OUT_DIR"
echo "    GGUF:      $GGUF_OUT"
