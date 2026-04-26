#!/usr/bin/env python3
"""
Scan HuggingFace hub cache (or any tree) of .safetensors for condensing research.

See METRICS.md for definitions of B_raw, buckets, and proxy scenarios.
"""
from __future__ import annotations

import argparse
import json
import struct
import sys
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import numpy as np


def _align8(n: int) -> int:
    return (n + 7) // 8 * 8


def read_safetensors_header(path: Path) -> tuple[dict[str, Any], int, bytes]:
    """Return (header_dict, data_offset_from_file_start, raw_header_bytes)."""
    with open(path, "rb") as f:
        hlen = struct.unpack("<Q", f.read(8))[0]
        if hlen < 2 or hlen > 100_000_000:
            raise ValueError(f"implausible header length {hlen} in {path}")
        raw = f.read(hlen)
    meta = json.loads(raw.decode("utf-8"))
    data_start = _align8(8 + hlen)
    return meta, int(data_start), raw


def dtype_nbytes(dtype: str) -> int:
    d = dtype.upper()
    if d in ("F64", "I64", "U64"):
        return 8
    if d in ("F32", "I32", "U32"):
        return 4
    if d in ("F16", "BF16", "I16", "U16"):
        return 2
    if d in ("I8", "U8", "BOOL", "F8_E4M3", "F8_E5M2", "F8E4M3", "F8E5M2"):
        return 1
    return 0


def classify_tensor(name: str, shape: list[int]) -> str:
    n = name.lower()
    r = 1
    for x in shape:
        r *= int(x)
    is_2d = len(shape) == 2
    is_big = r >= 256 * 256

    if "position" in n or "wpe" in n or "pos_embed" in n:
        if is_2d or len(shape) == 1:
            return "track1_pos_emb"
    if any(
        k in n
        for k in (
            "embed",
            "wte",
            "wpe",
            "token_embd",
            "tok_embeddings",
            "embed_tokens",
        )
    ):
        if "pos" in n and "token" not in n:
            return "track1_pos_emb"
        return "track1_tok_emb"
    if is_2d and is_big and any(k in n for k in ("mlp", "gate_proj", "up_proj", "down_proj", "w1", "w2", "w3")):
        return "track2_ffn"
    if is_2d and is_big and any(
        k in n for k in ("q_proj", "k_proj", "v_proj", "o_proj", "wq", "wk", "wv", "wo", "query", "key", "value", "out_proj")
    ):
        return "track2_attn"
    if is_2d and is_big and "lora" in n:
        return "track2_lora"
    if is_2d and is_big:
        return "track2_other2d"
    if len(shape) <= 1 and r < 16384:
        return "other_small"
    if not is_2d:
        return "other"
    return "track2_other2d" if is_big else "other"


@dataclass
class TensorInfo:
    name: str
    dtype: str
    shape: list[int]
    data_begin: int
    data_end: int
    bucket: str = ""
    n_elements: int = 0
    nbytes_file: int = 0

    def __post_init__(self) -> None:
        r = 1
        for x in self.shape:
            r *= int(x)
        self.n_elements = int(r)
        self.nbytes_file = int(self.data_end - self.data_begin)
        self.bucket = classify_tensor(
            self.name, [int(s) for s in self.shape]
        )


@dataclass
class SnapshotSummary:
    root: Path
    files: list[Path] = field(default_factory=list)
    b_raw: int = 0
    buckets: dict[str, int] = field(default_factory=dict)
    n_tensors: int = 0
    n_params: int = 0


def parse_tensors_in_file(
    path: Path, meta: dict[str, Any], data_start: int
) -> list[TensorInfo]:
    out: list[TensorInfo] = []
    for name, entry in meta.items():
        if name == "__metadata__" or not isinstance(entry, dict):
            continue
        dtype = str(entry.get("dtype", "F32"))
        shape = [int(x) for x in entry.get("shape", [])]
        off = entry.get("data_offsets")
        if not off or len(off) < 2:
            continue
        b0, b1 = int(off[0]), int(off[1])
        out.append(
            TensorInfo(
                name=name,
                dtype=dtype,
                shape=shape,
                data_begin=data_start + b0,
                data_end=data_start + b1,
            )
        )
    return out


def bf16_to_f32_block(raw: memoryview) -> np.ndarray:
    u = np.frombuffer(raw, dtype=np.uint16)
    u32 = u.astype(np.uint32) << 16
    return u32.view(np.float32).astype(np.float64)


def read_tensor_float64_sample(
    path: Path, t: TensorInfo, cap_elements: int
) -> np.ndarray | None:
    """Read at most `cap_elements` values as float64 (strided if huge)."""
    dt = t.dtype.upper()
    nb = dtype_nbytes(dt)
    if nb == 0 or t.nbytes_file != t.n_elements * nb:
        return None

    with open(path, "rb") as f:
        f.seek(t.data_begin)
        raw = f.read(t.nbytes_file)
    n = t.n_elements
    if n == 0:
        return None
    mv = memoryview(raw)
    if dt == "F32":
        a = np.frombuffer(mv, dtype=np.float32, count=n).astype(np.float64)
    elif dt == "F16":
        a = np.frombuffer(mv, dtype=np.float16, count=n).astype(np.float64)
    elif dt == "F64":
        a = np.frombuffer(mv, dtype=np.float64, count=n)
    elif dt == "BF16":
        a = bf16_to_f32_block(mv)
    else:
        return None

    if a.size > cap_elements:
        step = max(1, a.size // cap_elements)
        subs = a[::step]
        return subs[: min(cap_elements, subs.size)]
    return a


def read_tensor_float64_full(path: Path, t: TensorInfo) -> np.ndarray | None:
    return read_tensor_float64_sample(path, t, t.n_elements * 2)


def sparsity_fraction(a: np.ndarray, eps: float) -> float:
    return float(np.mean(np.abs(a) < eps))


def row_l2_dropped_fraction(a2d: np.ndarray, row_thr: float) -> float:
    if a2d.ndim != 2:
        return 0.0
    norms = np.sqrt(np.sum(a2d * a2d, axis=1))
    return float(np.mean(norms < row_thr))


def svd_rank_for_energy(w2d: np.ndarray, energy: float) -> int:
    w = np.ascontiguousarray(w2d, dtype=np.float64)
    s = np.linalg.svd(w, compute_uv=False, full_matrices=False)
    s2 = s * s
    total = float(np.sum(s2)) + 1e-30
    c = np.cumsum(s2) / total
    if len(c) == 0:
        return 0
    k = int(np.searchsorted(c, energy) + 1)
    return int(min(k, len(s)))


def process_file(
    fp: Path,
    meta: dict[str, Any],
    data_start: int,
    s: SnapshotSummary,
    args: argparse.Namespace,
    details: list[dict[str, Any]],
) -> None:
    tensors = parse_tensors_in_file(fp, meta, data_start)
    s.n_tensors += len(tensors)
    for t in tensors:
        s.n_params += t.n_elements
        s.buckets[t.bucket] = s.buckets.get(t.bucket, 0) + t.nbytes_file

    if not args.sparsity:
        return
    for t in tensors:
        if t.nbytes_file > args.max_tensor_mem_mb * 1024 * 1024:
            details.append(
                {
                    "file": str(fp),
                    "tensor": t.name,
                    "bucket": t.bucket,
                    "note": f"skip_sparsity_too_big_bytes={t.nbytes_file}",
                }
            )
            continue
        a = read_tensor_float64_sample(fp, t, int(args.max_elements_per_tensor))
        if a is None or a.size < 2:
            continue
        for eps in args.epsilons:
            details.append(
                {
                    "file": str(fp).replace("\\", "/")[-100:],
                    "tensor": t.name[:64],
                    "bucket": t.bucket,
                    "eps": eps,
                    "frac_lt_eps": sparsity_fraction(a, float(eps)),
                    "n_sample": a.size,
                }
            )
        if (
            len(t.shape) == 2
            and t.bucket.startswith("track2")
        ):
            a_full = read_tensor_float64_full(fp, t)
            if a_full is None or a_full.size != t.n_elements:
                continue
            m, n2 = t.shape[0], t.shape[1]
            a2 = a_full.reshape(m, n2)
            details.append(
                {
                    "file": str(fp).replace("\\", "/")[-100:],
                    "tensor": t.name[:64],
                    "bucket": t.bucket,
                    "per_row_l2_thr": args.per_row_l2,
                    "row_frac_l2_below": row_l2_dropped_fraction(
                        a2, args.per_row_l2
                    ),
                }
            )
            if (
                min(m, n2) <= args.max_svd_side
                and m * n2 <= args.max_svd_elements
            ):
                k90 = svd_rank_for_energy(a2, 0.90)
                k99 = svd_rank_for_energy(a2, 0.99)
                dense = m * n2 * 4
                lr90 = (m + n2) * k90 * 4
                details.append(
                    {
                        "file": str(fp).replace("\\", "/")[-100:],
                        "tensor": t.name[:64],
                        "svd_k90_energy_0.9": k90,
                        "svd_k99_energy_0.99": k99,
                        "dense_f32_B": dense,
                        "lowrank_90_f32_B": lr90,
                        "lowrank_90_over_dense": round(
                            lr90 / dense, 4
                        ) if dense else 0,
                    }
                )


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument(
        "--root", type=Path, default=Path(r"D:\Models\hub")
    )
    p.add_argument("--out", type=Path, default=None)
    p.add_argument("--max-models", type=int, default=None)
    p.add_argument("--max-files", type=int, default=None)
    p.add_argument(
        "--sparsity",
        action="store_true",
        help="Slower: epsilon sparsity, row L2, SVD where small enough",
    )
    p.add_argument(
        "--epsilons", type=str, default="1e-9,1e-5,1e-3"
    )
    p.add_argument("--max-tensor-mem-mb", type=int, default=512)
    p.add_argument(
        "--max-elements-per-tensor", type=int, default=2_000_000
    )
    p.add_argument("--per-row-l2", type=float, default=1e-9)
    p.add_argument("--max-svd-side", type=int, default=4096)
    p.add_argument(
        "--max-svd-elements", type=int, default=16_000_000
    )
    args = p.parse_args()
    args.epsilons = [float(x) for x in args.epsilons.split(",")]

    if not args.root.is_dir():
        print(f"ERROR: root not found: {args.root}", file=sys.stderr)
        return 1

    all_files: list[Path] = []
    for f in args.root.rglob("*.safetensors"):
        if f.is_file():
            all_files.append(f)
    all_files.sort()
    if args.max_files is not None:
        all_files = all_files[: args.max_files]

    by_snap: dict[Path, list[Path]] = {}
    for f in all_files:
        d = f.parent
        for _ in range(16):
            if (d / "config.json").is_file():
                by_snap.setdefault(d, []).append(f)
                break
            if d == d.parent:
                by_snap.setdefault(f.parent, []).append(f)
                break
            d = d.parent
        else:
            by_snap.setdefault(f.parent, []).append(f)

    snaps = sorted(by_snap.keys())
    if args.max_models is not None:
        snaps = snaps[: args.max_models]

    summaries: list[SnapshotSummary] = []
    all_details: list[dict[str, Any]] = []
    b_total = 0

    for sn in snaps:
        summ = SnapshotSummary(
            root=sn, files=sorted(set(by_snap[sn]))
        )
        for fp in summ.files:
            summ.b_raw += fp.stat().st_size
        for fp in summ.files:
            try:
                meta, data_start, _ = read_safetensors_header(fp)
            except Exception as e:
                print(f"WARN: skip {fp}: {e}", file=sys.stderr)
                continue
            process_file(
                fp, meta, data_start, summ, args, all_details
            )
        b_total += summ.b_raw
        summaries.append(summ)

    lines: list[str] = []
    lines.append("# Model condensing research report")
    lines.append(
        f"Generated: {datetime.now(timezone.utc).isoformat()}Z"
    )
    lines.append("")
    lines.append("## Denominator: B_raw (exact)")
    lines.append(f"- **Root:** `{args.root}`")
    lines.append(
        f"- **Total** `B_raw` over processed snapshots: **{b_total:,}** bytes "
        f"({b_total/1e9:.3f} GB)"
    )
    lines.append("")
    lines.append("## Numerator (see METRICS.md)")
    lines.append(
        "- `B_distilled` (true export bytes): **not computable** here; requires your packer + thresholds."
    )
    lines.append(
        "- **Proxy:** magnitude-epsilon sparsity, row L2, SVD 90% energy; NOT equivalent to spec functional sparsity."
    )
    lines.append("")
    lines.append("## Per snapshot")
    lines.append("")
    lines.append(
        "| Snapshot | B_raw (bytes) | tensors | #params (elements) |"
    )
    lines.append("|---|---:|---:|---:|")
    for s in summaries:
        lines.append(
            f"| `{s.root}` | {s.b_raw:,} | {s.n_tensors} | {s.n_params:,} |"
        )
    lines.append("")

    for s in summaries:
        if not s.buckets:
            continue
        lines.append(f"### Byte share by bucket - `{s.root}`")
        lines.append("")
        lines.append("| bucket | bytes | % of B_raw in snapshot |")
        lines.append("|---|---:|---:|")
        tot = s.b_raw or 1
        for b, nb in sorted(s.buckets.items(), key=lambda x: -x[1]):
            lines.append(
                f"| {b} | {nb:,} | {100.0*nb/tot:.2f} |"
            )
        lines.append("")

    if all_details and args.sparsity:
        lines.append("## Proxy details (first 80 rows, full set in out file JSON if needed)")
        lines.append("```")
        for row in all_details[:80]:
            lines.append(str(row))
        if len(all_details) > 80:
            lines.append(
                f"... {len(all_details) - 80} more (run with --out to keep full text)"
            )
        lines.append("```")

    text = "\n".join(lines) + "\n"
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(text, encoding="utf-8")
    if sys.platform == "win32":
        try:
            sys.stdout.reconfigure(encoding="utf-8")
        except (AttributeError, OSError):
            pass
    print(text, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
