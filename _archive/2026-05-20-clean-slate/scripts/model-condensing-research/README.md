# Model condensing research

Implements the **plan** leg: what is measurable on real `.safetensors` files without implementing the distilled exporter.

- **[METRICS.md](METRICS.md)** — `B_raw`, proxy numerators, Track 1/2 interpretation.
- **scan_safetensors.py** — walk a HuggingFace hub root, report `B_raw`, byte share by **heuristic** bucket, optional ε-sparsity, PerRow-style row L2, and small-matrix SVD 90% energy.
- **attribution_by_model_source.sql** — example SQL for per-model substrate attribution (separate from export file size).
- `requirements.txt` — install in a venv: `pip install -r requirements.txt`

## Run

```powershell
cd scripts/model-condensing-research
python -m venv .venv
.\.venv\Scripts\activate
pip install -r requirements.txt
# Fast: exact B_raw + bucket table only
python scan_safetensors.py --root /vault/Data/hub --out condensing_report.md
# Slower: sparsity / SVD (caps large tensors; see --help)
python scan_safetensors.py --root /vault/Data/hub --sparsity --max-models 5 --out condensing_report.md
```

Use `--max-files` or `--max-models` while iterating; full trees can be large.

## Not in scope

- No change to .NET decomposers. This is research tooling.
- `B_distilled` (real export size) is only estimated via **scenarios** in METRICS.md until a packer exists.
