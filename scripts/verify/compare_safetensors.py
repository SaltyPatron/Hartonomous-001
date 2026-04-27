"""Compare two safetensors files tensor-by-tensor.

Out-of-tree verifier for the Hartonomous round-trip / distillation export
flow. Loads two safetensors files (typically the original ingested model
and the substrate-recomposed export), aligns tensors by name, and reports:

  - tensor coverage (names present in both / only-in-original / only-in-export)
  - per-tensor relative Frobenius error: ||orig - exp||_F / ||orig||_F
  - aggregate metrics: mean rel_err, share of all-zero export tensors,
    share of byte-identical tensors

The reported errors interpret the substrate's distillation contract:

  - The export should be DENSER than the source — gradient noise filtered
    out by the substrate's sparsity threshold becomes zero in the export.
    A high share of all-zero export tensors is expected today; it shrinks
    as the per-role unit substrate fills in.
  - Tensors that ARE recomposed should land within a small relative
    Frobenius error on the rows the substrate has unit content for.
  - 100% identity is NOT the goal — the substrate is a NEW student model,
    not a byte replay of the source.

Usage:
    pip install safetensors numpy
    python scripts/verify/compare_safetensors.py --original orig.safetensors --exported exp.safetensors
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

try:
    import numpy as np
    from safetensors import safe_open
except ImportError as exc:
    sys.stderr.write(
        f"[compare_safetensors] missing dependency: {exc.name}. "
        "Install with: pip install safetensors numpy\n")
    sys.exit(1)


def load_tensor_set(path: Path) -> dict[str, np.ndarray]:
    out: dict[str, np.ndarray] = {}
    with safe_open(str(path), framework="numpy") as f:
        for key in f.keys():
            out[key] = f.get_tensor(key)
    return out


def relative_frobenius_error(a: np.ndarray, b: np.ndarray) -> float:
    a64 = a.astype(np.float64, copy=False)
    b64 = b.astype(np.float64, copy=False)
    diff = a64 - b64
    num = float(np.linalg.norm(diff))
    den = float(np.linalg.norm(a64))
    if den == 0.0:
        return 0.0 if num == 0.0 else float("inf")
    return num / den


def main() -> int:
    parser = argparse.ArgumentParser(description="Compare two safetensors files.")
    parser.add_argument("--original", required=True, type=Path, help="Original safetensors file")
    parser.add_argument("--exported", required=True, type=Path, help="Substrate-exported safetensors file")
    parser.add_argument("--limit", type=int, default=None,
                        help="Optional cap on per-tensor results printed (full aggregate still computed).")
    args = parser.parse_args()

    if not args.original.exists():
        print(f"original not found: {args.original}", file=sys.stderr)
        return 1
    if not args.exported.exists():
        print(f"exported not found: {args.exported}", file=sys.stderr)
        return 1

    orig = load_tensor_set(args.original)
    exp = load_tensor_set(args.exported)

    only_orig = sorted(set(orig) - set(exp))
    only_exp = sorted(set(exp) - set(orig))
    common = sorted(set(orig) & set(exp))

    print(f"Original: {len(orig)} tensors  |  Exported: {len(exp)} tensors")
    print(f"Names common: {len(common)} | only-original: {len(only_orig)} | only-export: {len(only_exp)}")
    if only_orig:
        print(f"  only in original (first 5): {only_orig[:5]}")
    if only_exp:
        print(f"  only in exported (first 5): {only_exp[:5]}")
    print()

    rel_errs: list[tuple[str, float, bool, bool]] = []
    identical = 0
    all_zero = 0
    for name in common:
        a = orig[name]
        b = exp[name]
        if a.shape != b.shape:
            rel_errs.append((name, float("nan"), False, False))
            continue
        is_identical = bool(np.array_equal(a, b))
        is_all_zero = bool(np.all(b == 0))
        if is_identical:
            identical += 1
        if is_all_zero:
            all_zero += 1
        rel = relative_frobenius_error(a, b)
        rel_errs.append((name, rel, is_identical, is_all_zero))

    finite_errs = [r for _, r, _, _ in rel_errs if not (r != r) and r != float("inf")]
    mean_rel = float(np.mean(finite_errs)) if finite_errs else 0.0

    by_rank: dict[int, list[float]] = {}
    for name, rel, _, _ in rel_errs:
        if rel != rel or rel == float("inf"):
            continue
        rank = orig[name].ndim
        by_rank.setdefault(rank, []).append(rel)

    print("=== Aggregate ===")
    print(f"  matched tensors: {len(common)}")
    print(f"  byte-identical:  {identical} ({(100.0 * identical / max(1, len(common))):.1f}%)")
    print(f"  all-zero export: {all_zero} ({(100.0 * all_zero / max(1, len(common))):.1f}%)")
    print(f"  mean rel_err:    {mean_rel:.4f}")
    print()
    print("=== By rank ===")
    for rank in sorted(by_rank):
        errs = by_rank[rank]
        print(f"  {rank}D: {len(errs)} tensors, mean rel_err = {float(np.mean(errs)):.4f}")
    print()

    if args.limit:
        print(f"=== Per-tensor (first {args.limit}) ===")
        for name, rel, ident, zero in rel_errs[: args.limit]:
            tag = "IDENT" if ident else ("ZERO " if zero else "DIFF ")
            print(f"  [{tag}] {rel:8.4f}  {name}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
