#!/usr/bin/env python3
"""
download_seeds.py — Substrate seed dataset download orchestrator.

Reads scripts/seeds.yaml, dispatches per-dataset downloads to the appropriate
handler (http / git / huggingface / manual), tracks state in a manifest JSON,
and is idempotent — re-runs skip already-downloaded datasets unless --force.

Usage:
    python download_seeds.py [--data-root D:/Models]
                             [--tier {0,1,2,3,all}]
                             [--dataset NAME]
                             [--dry-run]
                             [--force]
                             [--list]
                             [--manifest-only]

Examples:
    python download_seeds.py --tier 1 --dry-run
    python download_seeds.py --dataset conceptnet-5.7
    python download_seeds.py --tier all --force
    python download_seeds.py --list

Required Python packages (substrate runs on stdlib + requests + pyyaml + tqdm):
    pip install requests pyyaml tqdm

Optional (only needed for hf_dataset entries):
    pip install huggingface_hub
"""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import os
import platform
import shutil
import subprocess
import sys
import tarfile
import time
import urllib.parse
import zipfile
from dataclasses import dataclass, field, asdict
from pathlib import Path
from typing import Any, Optional

try:
    import yaml
except ImportError:
    sys.exit("Missing dependency: pip install pyyaml")

try:
    import requests
except ImportError:
    sys.exit("Missing dependency: pip install requests")

try:
    from tqdm import tqdm
except ImportError:
    sys.exit("Missing dependency: pip install tqdm")

DEFAULT_DATA_ROOT = Path(r"D:\Models") if platform.system() == "Windows" else Path.home() / "models"
SEEDS_YAML = Path(__file__).parent / "seeds.yaml"
MANIFEST_FILENAME = "seed_manifest.json"
USER_AGENT = "Hartonomous-substrate-seed-downloader/1.0"

# ─────────────────────────────────────────────────────────────────────────
# Logging helpers
# ─────────────────────────────────────────────────────────────────────────

def log_info(msg: str) -> None:
    print(f"[INFO] {msg}", flush=True)

def log_warn(msg: str) -> None:
    print(f"[WARN] {msg}", flush=True)

def log_err(msg: str) -> None:
    print(f"[ERR ] {msg}", flush=True)

def log_skip(msg: str) -> None:
    print(f"[SKIP] {msg}", flush=True)

def log_ok(msg: str) -> None:
    print(f"[ OK ] {msg}", flush=True)


# ─────────────────────────────────────────────────────────────────────────
# Manifest state
# ─────────────────────────────────────────────────────────────────────────

@dataclass
class ManifestEntry:
    name: str
    source: str
    target: str
    license: str
    license_flags: list[str]
    size_mb_estimated: int
    grammar: str
    downloaded_at: str
    downloaded_by_version: str = "1.0"
    files: list[dict] = field(default_factory=list)  # [{path, size, sha256}]
    error: Optional[str] = None
    notes: str = ""

class Manifest:
    def __init__(self, path: Path):
        self.path = path
        self.entries: dict[str, dict] = {}
        if path.exists():
            try:
                self.entries = json.loads(path.read_text(encoding="utf-8"))
            except Exception as e:
                log_warn(f"Manifest at {path} unreadable ({e}); starting fresh.")
                self.entries = {}

    def has(self, name: str) -> bool:
        return name in self.entries and self.entries[name].get("error") is None

    def record(self, entry: ManifestEntry) -> None:
        self.entries[entry.name] = asdict(entry)
        self.flush()

    def remove(self, name: str) -> None:
        self.entries.pop(name, None)
        self.flush()

    def flush(self) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(
            json.dumps(self.entries, indent=2, sort_keys=True),
            encoding="utf-8",
        )


# ─────────────────────────────────────────────────────────────────────────
# Download primitives
# ─────────────────────────────────────────────────────────────────────────

def http_download_with_progress(url: str, target_path: Path, session: requests.Session) -> None:
    """Stream-download a single URL to target_path with a progress bar."""
    target_path.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = target_path.with_suffix(target_path.suffix + ".part")

    headers = {"User-Agent": USER_AGENT}
    with session.get(url, stream=True, headers=headers, allow_redirects=True, timeout=60) as r:
        r.raise_for_status()
        total = int(r.headers.get("content-length", 0))
        with open(tmp_path, "wb") as f, tqdm(
            total=total or None,
            unit="B",
            unit_scale=True,
            unit_divisor=1024,
            desc=target_path.name,
            leave=False,
        ) as bar:
            for chunk in r.iter_content(chunk_size=1024 * 1024):
                if chunk:
                    f.write(chunk)
                    bar.update(len(chunk))
    tmp_path.replace(target_path)


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def filesize_recursive(path: Path) -> int:
    if path.is_file():
        return path.stat().st_size
    total = 0
    for p in path.rglob("*"):
        if p.is_file():
            try:
                total += p.stat().st_size
            except OSError:
                pass
    return total


def file_inventory(target_dir: Path, sha256_files: bool = False) -> list[dict]:
    """List files under target_dir with size and (optionally) sha256."""
    out = []
    if target_dir.is_file():
        d = {"path": target_dir.name, "size": target_dir.stat().st_size}
        if sha256_files:
            d["sha256"] = sha256_of(target_dir)
        return [d]
    if not target_dir.exists():
        return []
    for p in sorted(target_dir.rglob("*")):
        if p.is_file():
            try:
                d = {"path": str(p.relative_to(target_dir)).replace("\\", "/"), "size": p.stat().st_size}
                if sha256_files and d["size"] < 100 * 1024 * 1024:
                    # Only sha256 files <100MB to keep manifest small
                    d["sha256"] = sha256_of(p)
                out.append(d)
            except OSError:
                pass
    return out


# ─────────────────────────────────────────────────────────────────────────
# Decompression
# ─────────────────────────────────────────────────────────────────────────

def extract_archive(archive_path: Path, target_dir: Path, hint: Optional[str] = None) -> None:
    """Extract archive_path into target_dir. Auto-detect format from suffix or hint."""
    suffix = (hint or "").lower() if hint else "".join(archive_path.suffixes).lower()
    name = archive_path.name.lower()

    target_dir.mkdir(parents=True, exist_ok=True)

    if name.endswith(".zip") or suffix == "zip":
        log_info(f"Extracting zip {archive_path} → {target_dir}")
        with zipfile.ZipFile(archive_path) as zf:
            zf.extractall(target_dir)
    elif name.endswith(".tar.gz") or name.endswith(".tgz") or suffix == "tar.gz":
        log_info(f"Extracting tar.gz {archive_path} → {target_dir}")
        with tarfile.open(archive_path, "r:gz") as tf:
            tf.extractall(target_dir)
    elif name.endswith(".tar.bz2") or name.endswith(".tbz2") or suffix == "tar.bz2":
        log_info(f"Extracting tar.bz2 {archive_path} → {target_dir}")
        with tarfile.open(archive_path, "r:bz2") as tf:
            tf.extractall(target_dir)
    elif name.endswith(".tar"):
        log_info(f"Extracting tar {archive_path} → {target_dir}")
        with tarfile.open(archive_path, "r:") as tf:
            tf.extractall(target_dir)
    elif name.endswith(".gz") or suffix == "gz":
        # Gzipped single file (e.g., .csv.gz) — decompress to target_dir/<basename without .gz>
        out_name = archive_path.name[:-3] if archive_path.name.endswith(".gz") else archive_path.stem
        out_path = target_dir / out_name
        log_info(f"Decompressing gz {archive_path} → {out_path}")
        with gzip.open(archive_path, "rb") as fin, open(out_path, "wb") as fout:
            shutil.copyfileobj(fin, fout, length=4 * 1024 * 1024)
    elif name.endswith(".bz2"):
        import bz2
        out_name = archive_path.name[:-4]
        out_path = target_dir / out_name
        log_info(f"Decompressing bz2 {archive_path} → {out_path}")
        with bz2.open(archive_path, "rb") as fin, open(out_path, "wb") as fout:
            shutil.copyfileobj(fin, fout, length=4 * 1024 * 1024)
    else:
        raise RuntimeError(f"Cannot determine archive format for {archive_path} (suffix={suffix})")


# ─────────────────────────────────────────────────────────────────────────
# Per-source handlers
# ─────────────────────────────────────────────────────────────────────────

def handle_manual(ds: dict, target: Path, session: requests.Session, dry_run: bool) -> tuple[bool, str]:
    """Manual datasets: print instructions, return False (not auto-downloaded)."""
    notes = ds.get("notes", "")
    log_warn(f"{ds['name']} requires manual download. Target: {target}")
    if notes:
        log_warn(f"  Instructions: {notes}")
    return False, "manual_download_required"


def handle_http_file(ds: dict, target: Path, session: requests.Session, dry_run: bool) -> tuple[bool, str]:
    """Single non-archive file → drop into target/."""
    url = ds["url"]
    name = url.rsplit("/", 1)[-1].split("?")[0] or "downloaded.file"
    if dry_run:
        log_info(f"DRY-RUN would GET {url} → {target / name}")
        return True, "dry_run"
    target.mkdir(parents=True, exist_ok=True)
    out = target / name
    log_info(f"GET {url}")
    http_download_with_progress(url, out, session)

    # Auto-decompress single .gz files inline (e.g., conceptnet)
    if ds.get("decompress") == "gz" and out.suffix == ".gz":
        try:
            extract_archive(out, target, hint="gz")
            # Keep the .gz too (cheaper than re-downloading) unless huge
            if out.stat().st_size > 1024 * 1024 * 1024:
                out.unlink()
        except Exception as e:
            log_warn(f"  Auto-decompress failed for {out}: {e}")

    return True, ""


def handle_http_files(ds: dict, target: Path, session: requests.Session, dry_run: bool) -> tuple[bool, str]:
    """Multiple files → all into target/."""
    urls = ds["urls"]
    if dry_run:
        for u in urls:
            log_info(f"DRY-RUN would GET {u}")
        return True, "dry_run"
    target.mkdir(parents=True, exist_ok=True)
    for u in urls:
        name = u.rsplit("/", 1)[-1].split("?")[0]
        out = target / name
        log_info(f"GET {u}")
        try:
            http_download_with_progress(u, out, session)
        except Exception as e:
            log_err(f"  Failed: {e}")
            return False, str(e)
        # Auto-extract zips dropped via http_files
        if name.endswith(".zip"):
            try:
                extract_archive(out, target)
            except Exception as e:
                log_warn(f"  Could not extract {out}: {e}")
    return True, ""


def handle_http_archive(ds: dict, target: Path, session: requests.Session, dry_run: bool) -> tuple[bool, str]:
    """Archive URL → download, extract, optionally remove archive."""
    url = ds["url"]
    if dry_run:
        log_info(f"DRY-RUN would GET archive {url} → extract to {target}")
        return True, "dry_run"
    target.mkdir(parents=True, exist_ok=True)
    name = url.rsplit("/", 1)[-1].split("?")[0]
    archive = target / name
    log_info(f"GET archive {url}")
    http_download_with_progress(url, archive, session)
    try:
        extract_archive(archive, target, hint=ds.get("decompress"))
    except Exception as e:
        log_err(f"  Extract failed: {e}")
        return False, f"extract_failed: {e}"
    # Keep archive only if small; remove if >500MB to save disk
    if archive.exists() and archive.stat().st_size > 500 * 1024 * 1024:
        try:
            archive.unlink()
        except Exception:
            pass
    return True, ""


def handle_git(ds: dict, target: Path, session: requests.Session, dry_run: bool) -> tuple[bool, str]:
    """git clone --depth 1."""
    url = ds["url"]
    if dry_run:
        log_info(f"DRY-RUN would git clone --depth 1 {url} → {target}")
        return True, "dry_run"
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists():
        # Already cloned; could pull, but for idempotency we skip
        log_skip(f"  Target exists: {target}")
        return True, "exists"
    cmd = ["git", "clone", "--depth", "1", "--no-tags", url, str(target)]
    log_info(f"  RUN: {' '.join(cmd)}")
    try:
        proc = subprocess.run(cmd, check=False, capture_output=True, text=True)
        if proc.returncode != 0:
            log_err(f"  git clone failed: {proc.stderr.strip()}")
            return False, f"git_clone_failed: {proc.stderr.strip()[:200]}"
    except FileNotFoundError:
        return False, "git_not_installed"
    return True, ""


def handle_hf_dataset(ds: dict, target: Path, session: requests.Session, dry_run: bool) -> tuple[bool, str]:
    """HuggingFace dataset snapshot_download."""
    repo = ds["repo"]
    if dry_run:
        log_info(f"DRY-RUN would snapshot_download repo={repo} → {target}")
        return True, "dry_run"
    try:
        from huggingface_hub import snapshot_download
    except ImportError:
        return False, "missing_huggingface_hub (pip install huggingface_hub)"
    target.mkdir(parents=True, exist_ok=True)
    log_info(f"  HF snapshot_download repo={repo}")
    try:
        snapshot_download(
            repo_id=repo,
            repo_type="dataset",
            local_dir=str(target),
            local_dir_use_symlinks=False,
        )
    except Exception as e:
        return False, f"hf_download_failed: {e}"
    return True, ""


HANDLERS = {
    "manual": handle_manual,
    "http_file": handle_http_file,
    "http_files": handle_http_files,
    "http_archive": handle_http_archive,
    "git": handle_git,
    "hf_dataset": handle_hf_dataset,
}


# ─────────────────────────────────────────────────────────────────────────
# Driver
# ─────────────────────────────────────────────────────────────────────────

def load_seeds(path: Path) -> list[dict]:
    raw = yaml.safe_load(path.read_text(encoding="utf-8"))
    return raw.get("datasets", [])


def filter_seeds(
    seeds: list[dict],
    tier: Optional[int],
    dataset_name: Optional[str],
) -> list[dict]:
    out = seeds
    if tier is not None:
        out = [d for d in out if d.get("tier") == tier]
    if dataset_name is not None:
        out = [d for d in out if d.get("name") == dataset_name]
    return out


def process_one(
    ds: dict,
    data_root: Path,
    manifest: Manifest,
    session: requests.Session,
    dry_run: bool,
    force: bool,
) -> bool:
    name = ds["name"]
    target = data_root / ds["target"]

    # Idempotency check
    if not force and manifest.has(name):
        log_skip(f"{name} (manifest hit; use --force to redownload)")
        return True

    if not force and target.exists() and any(target.iterdir() if target.is_dir() else [target]):
        log_skip(f"{name} (target {target} non-empty; not in manifest, recording without re-download; use --force to redownload)")
        # Backfill manifest entry without re-downloading
        if not dry_run:
            manifest.record(ManifestEntry(
                name=name,
                source=ds["source"],
                target=str(target),
                license=ds.get("license", ""),
                license_flags=ds.get("license_flags", []),
                size_mb_estimated=ds.get("size_mb", 0),
                grammar=ds.get("grammar", ""),
                downloaded_at=time.strftime("%Y-%m-%dT%H:%M:%S"),
                files=file_inventory(target),
                notes="backfilled from existing files",
            ))
        return True

    log_info(f"=== {name} (tier={ds.get('tier')}, source={ds.get('source')}, license={ds.get('license')}) ===")

    handler = HANDLERS.get(ds.get("source", "manual"))
    if handler is None:
        log_err(f"  Unknown source: {ds.get('source')}")
        return False

    try:
        ok, status = handler(ds, target, session, dry_run)
    except Exception as e:
        log_err(f"  Handler raised: {e}")
        ok, status = False, str(e)

    if dry_run:
        return ok

    entry = ManifestEntry(
        name=name,
        source=ds["source"],
        target=str(target),
        license=ds.get("license", ""),
        license_flags=ds.get("license_flags", []),
        size_mb_estimated=ds.get("size_mb", 0),
        grammar=ds.get("grammar", ""),
        downloaded_at=time.strftime("%Y-%m-%dT%H:%M:%S"),
        files=file_inventory(target) if ok else [],
        error=None if ok else status,
        notes=ds.get("notes", ""),
    )
    manifest.record(entry)

    if ok:
        actual_size_mb = filesize_recursive(target) // (1024 * 1024)
        log_ok(f"{name}: {actual_size_mb} MB on disk at {target}")
    else:
        log_err(f"{name}: FAILED — {status}")

    return ok


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Substrate seed dataset downloader")
    parser.add_argument("--data-root", type=Path, default=DEFAULT_DATA_ROOT,
                        help=f"Root for downloaded data (default: {DEFAULT_DATA_ROOT})")
    parser.add_argument("--seeds", type=Path, default=SEEDS_YAML,
                        help=f"Path to seeds.yaml (default: {SEEDS_YAML})")
    parser.add_argument("--tier", type=str, default=None,
                        choices=["0", "1", "2", "3", "all"],
                        help="Only process datasets in this tier (or 'all')")
    parser.add_argument("--dataset", type=str, default=None,
                        help="Only process this specific dataset by name")
    parser.add_argument("--dry-run", action="store_true",
                        help="Print actions without downloading")
    parser.add_argument("--force", action="store_true",
                        help="Re-download even if manifest already has entry")
    parser.add_argument("--list", action="store_true",
                        help="List datasets in scope and exit")
    parser.add_argument("--manifest-only", action="store_true",
                        help="Walk targets and rebuild manifest without downloading anything")
    args = parser.parse_args(argv)

    if not args.seeds.exists():
        log_err(f"Cannot find seeds file: {args.seeds}")
        return 2

    seeds = load_seeds(args.seeds)
    log_info(f"Loaded {len(seeds)} dataset definitions from {args.seeds}")

    tier = None if args.tier in (None, "all") else int(args.tier)
    selected = filter_seeds(seeds, tier, args.dataset)
    if not selected:
        log_warn("No datasets matched the filter.")
        return 1

    if args.list:
        for d in selected:
            tags = []
            if "non_commercial" in d.get("license_flags", []):
                tags.append("NON-COMMERCIAL")
            if "research_only" in d.get("license_flags", []):
                tags.append("RESEARCH-ONLY")
            tag_str = " [" + ", ".join(tags) + "]" if tags else ""
            print(f"  tier={d.get('tier')} {d['name']:<35} ({d.get('source')}, ~{d.get('size_mb', '?')} MB, {d.get('license')}){tag_str}")
            print(f"      grammar: {d.get('grammar', '?')}")
        return 0

    args.data_root.mkdir(parents=True, exist_ok=True)
    manifest_path = args.data_root / MANIFEST_FILENAME
    manifest = Manifest(manifest_path)

    if args.manifest_only:
        # Walk the data_root, backfill manifest for any seed whose target exists
        for ds in selected:
            target = args.data_root / ds["target"]
            if target.exists() and (target.is_file() or any(target.iterdir())):
                if not manifest.has(ds["name"]):
                    log_info(f"Backfilling manifest for {ds['name']}")
                    manifest.record(ManifestEntry(
                        name=ds["name"],
                        source=ds["source"],
                        target=str(target),
                        license=ds.get("license", ""),
                        license_flags=ds.get("license_flags", []),
                        size_mb_estimated=ds.get("size_mb", 0),
                        grammar=ds.get("grammar", ""),
                        downloaded_at=time.strftime("%Y-%m-%dT%H:%M:%S"),
                        files=file_inventory(target),
                        notes="backfilled by --manifest-only",
                    ))
        log_ok(f"Manifest written to {manifest_path}")
        return 0

    session = requests.Session()
    session.headers.update({"User-Agent": USER_AGENT})

    succeeded = 0
    failed: list[str] = []
    skipped_manual: list[str] = []

    for ds in selected:
        ok = process_one(ds, args.data_root, manifest, session, args.dry_run, args.force)
        if ok:
            succeeded += 1
        else:
            if ds.get("source") == "manual":
                skipped_manual.append(ds["name"])
            else:
                failed.append(ds["name"])

    print()
    log_info(f"Summary: {succeeded}/{len(selected)} processed successfully")
    if skipped_manual:
        log_warn(f"  {len(skipped_manual)} require manual download:")
        for n in skipped_manual:
            log_warn(f"    - {n}")
    if failed:
        log_err(f"  {len(failed)} failed:")
        for n in failed:
            log_err(f"    - {n}")

    log_info(f"Manifest at {manifest_path}")
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
