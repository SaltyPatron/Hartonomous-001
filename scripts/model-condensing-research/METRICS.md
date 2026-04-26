# Condensing-size research — metric definitions

This folder implements the **research-only** path from the model condensing plan: measure what is observable on **existing** `.safetensors` files without changing Hartonomous product code.

## Denominator (exact)

- **`B_raw`**: Sum of file sizes (bytes) of all `*.safetensors` weight files discovered under the scan root (HF cache layout: `models--*/*/snapshots/*/*.safetensors`, or any nested `*.safetensors`).
- Optional **per-snapshot** grouping: a *snapshot* is a directory containing `config.json` plus at least one `.safetensors` file; `B_raw` is also reported per snapshot.

**Interpretation:** This is the uncompressed on-disk size of published checkpoints (excluding optional `tokenizer.json`, etc., unless you add those to the scan).

## Numerator (not unique without a ship)

The plan distinguishes:

1. **`B_distilled` (true, future)** — Byte size of a **distilled** safetensors package produced by your substrate query + synthesis + packer (not implemented in this repo as a single binary). Depends on Law #11 thresholds, query scope, target architecture, and whether the packer stores **dense** tensors, **sparse** blocks, or **low-rank** factors.

2. **`B_est` (proxy scenarios)** — What this script estimates from **raw** weights only:
   - **Scenario A — dense zeroing (straw-man):** Assume every element with `|w| < ε` becomes an exact zero **and** the export format still stores a **dense** tensor of the same shape/dtype. Then **file size does not shrink** unless you also change storage (quantization, CSR, low-rank factors). The script still reports the **parameter fraction** below ε as a **signal** proxy, not a byte ratio.
   - **Scenario B — same-shape dense “mass” after ε:** The fraction of **non-ε elements** (or 1 − ε-sparsity) is reported as a **weight-mass** ratio for Track 2–like tensors; use it only with an explicit **format** assumption.
   - **Scenario C — rank-_k_ approximation (classical bound):** For 2D matrices, compare storage `m×n×bytes_per_element` to `(m+n)×k×bytes` for a rank-_k_ factorization that retains e.g. 90% Frobenius energy. This is a **theoretical** lower bound for *matrix* storage, not the substrate’s edge encoding size.

3. **Track split (per spec, heuristic in code):**
   - **Track 1–like (embeddings):** Name/shape heuristics. Per `safetensors.md`, embeddings are not row-sparsity-pruned; **do not** expect a large ε-sparsity “win” on that mass without changing vocab/hidden in distillation.
   - **Track 2–like (2D transformation blocks):** Where ε-sparsity, row norms, and SVD energy curves are most relevant as **bounds**; the spec’s true filter is **functional**, not |w|&lt;ε.

## PerRow-style implementation mirror (optional)

`--per-row-l2-threshold` uses the same order of idea as [PerRowEmitter.SparsityThreshold](src/Hartonomous.Decomposers/Safetensors/Passes/PerRowEmitter.cs) (`1e-9`) on 2D tensors: **fraction of rows** with L2 norm below threshold (full row scan for tensors under `--max-tensor-mem-mb`).

**Interpretation:** This is a **code-mirror** bound, not the full “functional sparsity” spec.

## What this script does *not* claim

- A single universal “**N% smaller file**” for your final product.
- Deduplication across models in a shared substrate (needs multi-model + DB context).
- Functional / activation sparsity (requires probes not present here).

## Outputs

- Console + optional `--out report.md` with tables: `B_raw`, byte shares by bucket, ε-sparsity samples, rank at 90% energy (where SVD run), and labeled scenarios.
