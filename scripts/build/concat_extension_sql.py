#!/usr/bin/env python3
"""
concat_extension_sql.py — assemble the single unified extension install
script the substrate ships under PostGIS-pattern packaging:

    ext/hartonomous_pg/sql/hartonomous--1.0.sql

Layout of the generated script (in apply order):

    (a) CREATE SCHEMA IF NOT EXISTS substrate;
        CREATE SCHEMA IF NOT EXISTS monitor;
        — the substrate / monitor schemas must exist before any
          substrate.* function or type is declared. The bootstrap walk
          also includes the schema/schemas/*.sql files, so this is
          belt-and-suspenders; both forms use IF NOT EXISTS and are
          idempotent.

    (b) The hand-written `hartonomous--1.0.sql.in` C-binding template,
        lightly cleaned (psql meta-commands + raw transaction control
        stripped). This installs:
          - public.point4d, public.box4d (shell types → I/O fns → full
            CREATE TYPE → constructors → operators → opclasses →
            aggregates)
          - BLAKE3 functions
          - A* traversal in pg_traversal.c
          - substrate.text_decompose_summary composite type
          - substrate.text_decompose / text_decompose_batch (SPI C)
          - substrate.cp_* lookup wrappers over the embedded UCD blob
          - substrate.codepoint_atom composite type + atom enumerators
          - Glicko-2 bulk update in pg_glicko_bulk.c

    (c) The bootstrap.sql `@include` walk, expanded recursively:
          - domains (hash_value, significance_mu, significance_sigma,
            significance_volatility, code_value, tier_number,
            modality_code)
          - composite types
          - reference / core / junction / model / monitor tables
          - indexes (one per file)
          - seed inserts
          - substrate.* SQL / plpgsql functions, procedures, views
        Extension prerequisites (postgis, btree_gist, pg_trgm) are
        declared in hartonomous.control's `requires` and CASCADE'd by
        `CREATE EXTENSION hartonomous` — they're skipped here.
        Top-level BEGIN/COMMIT/ROLLBACK is stripped because the extension
        script runs inside CREATE EXTENSION's implicit transaction.

Packaging contract (PostGIS-pattern):
  - `make install` copies hartonomous.control + hartonomous--1.0.sql
    into $(pg_sharedir)/extension/.
  - `CREATE EXTENSION hartonomous CASCADE` installs the full substrate
    in one transaction. DROP EXTENSION hartonomous CASCADE removes
    everything cleanly. No separate `psql -f substrate-schema.sql` step.

Determinism: same source tree → byte-identical output.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path


REPO_ROOT      = Path(__file__).resolve().parent.parent.parent
SQL_ROOT       = REPO_ROOT / "sql"
SCHEMA_ROOT    = SQL_ROOT / "schema"
BOOTSTRAP_FILE = SCHEMA_ROOT / "bootstrap.sql"
EXT_SRC        = REPO_ROOT / "ext" / "hartonomous_pg" / "sql"
EXT_TEMPLATE   = EXT_SRC / "hartonomous--1.0.sql.in"
EXT_OUTPUT     = EXT_SRC / "hartonomous--1.0.sql"
LEGACY_SUBSTRATE_OUTPUT = EXT_SRC / "substrate-schema.sql"


INCLUDE_RE = re.compile(r"^\s*--\s*@include\s+(?P<path>\S+)\s*$", re.MULTILINE)
META_LINE_RE = re.compile(
    r"^\s*\\(?:set|echo|connect|quit|timing|pset|c|cd|conninfo|encoding|password|gset)\b.*$",
    re.MULTILINE | re.IGNORECASE,
)
# Top-level transaction-control statements are forbidden inside an extension
# script (extensions run in an implicit transaction).
TXN_LINE_RE = re.compile(
    r"^\s*(?:BEGIN|COMMIT|ROLLBACK|START\s+TRANSACTION)\s*;\s*$",
    re.MULTILINE | re.IGNORECASE,
)

# Extension prerequisites — become `requires` in the control file
# instead of being CREATE EXTENSION'd inside our script.
PREREQUISITE_EXTENSIONS = {"postgis", "btree_gist", "pg_trgm"}


def strip_psql_meta(content: str) -> str:
    """Remove psql backslash meta-commands and raw transaction control."""
    content = META_LINE_RE.sub("", content)
    content = TXN_LINE_RE.sub("", content)
    return content


def is_extension_creation_file(rel_include_path: str) -> bool:
    """Files under schema/extensions/ wrap CREATE EXTENSION calls — declared
    via control `requires`, skipped here."""
    norm = rel_include_path.replace("\\", "/")
    return norm.startswith("schema/extensions/") and norm.endswith(".sql")


def expand_file(path: Path, depth: int = 0) -> list[tuple[Path, str]]:
    """Recursively expand @include directives, returning a list of
    (source_path, content) pairs in declaration order."""
    if depth > 16:
        raise RuntimeError(f"@include depth limit exceeded: {path}")
    text = path.read_text(encoding="utf-8")

    parts: list[tuple[Path, str]] = []
    cursor = 0
    for m in INCLUDE_RE.finditer(text):
        head = text[cursor : m.start()]
        if head.strip():
            parts.append((path, head))
        rel = m.group("path").replace("\\", "/")
        if is_extension_creation_file(rel):
            parts.append((path, f"-- (skipped @include {rel} — handled via control file `requires`)\n"))
            cursor = m.end()
            continue
        target = (SQL_ROOT / rel).resolve()
        if not target.is_file():
            raise FileNotFoundError(
                f"@include not found: {rel} (resolved {target}) referenced from {path}"
            )
        parts.extend(expand_file(target, depth + 1))
        cursor = m.end()
    tail = text[cursor:]
    if tail.strip():
        parts.append((path, tail))

    return parts


def render_parts(parts: list[tuple[Path, str]]) -> str:
    chunks: list[str] = []
    last_src: Path | None = None
    for src, content in parts:
        cleaned = strip_psql_meta(content)
        if not cleaned.strip():
            continue
        if src != last_src:
            try:
                rel = src.resolve().relative_to(REPO_ROOT)
                marker = f"\n-- ── {rel.as_posix()} ───────────────────────────────────────\n"
            except ValueError:
                marker = f"\n-- ── {src.name} ───────────────────────────────────────\n"
            chunks.append(marker)
            last_src = src
        chunks.append(cleaned)
        if not cleaned.endswith("\n"):
            chunks.append("\n")
    return "".join(chunks)


def assemble_unified_extension_sql() -> str:
    """Produce the single unified hartonomous--1.0.sql script.

    Order: (a) CREATE SCHEMA — (b) C-binding template — (c) bootstrap
    @include walk. See module docstring for rationale.
    """
    if not EXT_TEMPLATE.is_file():
        raise FileNotFoundError(
            f".sql.in template not found at {EXT_TEMPLATE} — should contain "
            "C-binding declarations (point4d, traversal, BLAKE3, etc.)"
        )
    if not BOOTSTRAP_FILE.is_file():
        raise FileNotFoundError(f"bootstrap.sql not found at {BOOTSTRAP_FILE}")

    header = (
        "/* GENERATED — do not edit by hand.\n"
        " *\n"
        " * Source: ext/hartonomous_pg/sql/hartonomous--1.0.sql.in\n"
        " *       + sql/schema/bootstrap.sql + included files.\n"
        " * Concatenated by: scripts/build/concat_extension_sql.py\n"
        " *\n"
        " * Single unified extension install script — PostGIS-pattern\n"
        " * packaging. CREATE EXTENSION hartonomous CASCADE installs the\n"
        " * complete substrate (C-binding types/operators/functions +\n"
        " * substrate / monitor schemas + domains + tables + indexes +\n"
        " * seeds + substrate.* SQL/plpgsql functions) in one transaction.\n"
        " * DROP EXTENSION hartonomous CASCADE removes everything cleanly.\n"
        " *\n"
        " * Prerequisite extensions (postgis, btree_gist, pg_trgm) are\n"
        " * declared in hartonomous.control's `requires` and installed\n"
        " * automatically by CREATE EXTENSION ... CASCADE.\n"
        " */\n"
        "\n"
        "\\echo Use \"CREATE EXTENSION hartonomous CASCADE\" to load this extension. \\quit\n"
        "\n"
        "-- ════════════════════════════════════════════════════════════════════\n"
        "-- (a) Schemas — created up front so subsequent substrate.* declarations\n"
        "-- have a home. The bootstrap walk re-includes schema/schemas/*.sql\n"
        "-- below; both forms use IF NOT EXISTS and are idempotent.\n"
        "-- ════════════════════════════════════════════════════════════════════\n"
        "CREATE SCHEMA IF NOT EXISTS substrate;\n"
        "CREATE SCHEMA IF NOT EXISTS monitor;\n"
        "COMMENT ON SCHEMA substrate IS\n"
        "    'Content-addressed substrate. Every table here is keyed on BLAKE3 hashes; no surrogate IDs.';\n"
        "COMMENT ON SCHEMA monitor IS\n"
        "    'Substrate health, ingestion progress, phase status, inference metrics.';\n"
    )

    # (b) C-binding template — strip the .sql.in's own `\echo … \quit`
    # preamble; we already emitted one above.
    template_text = EXT_TEMPLATE.read_text(encoding="utf-8")
    template_text = strip_psql_meta(template_text)
    # Trim any leading whitespace produced by the meta-strip.
    template_text = template_text.lstrip("\n")

    template_section = (
        "\n-- ════════════════════════════════════════════════════════════════════\n"
        "-- (b) C-binding declarations — public.point4d / box4d types and ops,\n"
        "-- BLAKE3, traversal, substrate.text_decompose_summary, substrate.cp_*,\n"
        "-- substrate.text_decompose / text_decompose_batch, substrate.codepoint_atom,\n"
        "-- substrate.glicko2_bulk_update. From hartonomous--1.0.sql.in.\n"
        "-- ════════════════════════════════════════════════════════════════════\n"
        f"\n-- ── ext/hartonomous_pg/sql/{EXT_TEMPLATE.name} ───────────────────────────────────────\n"
        f"{template_text}"
    )
    if not template_section.endswith("\n"):
        template_section += "\n"

    # (c) Bootstrap walk — substrate schema content (domains, types, tables,
    # indexes, seeds, substrate.* SQL/plpgsql functions, procedures, views).
    parts = expand_file(BOOTSTRAP_FILE)
    bootstrap_text = render_parts(parts)

    bootstrap_section = (
        "\n-- ════════════════════════════════════════════════════════════════════\n"
        "-- (c) Substrate schema content — domains, composite types, reference\n"
        "-- + core + junction + model + monitor + meta tables, indexes, seed\n"
        "-- inserts, substrate.* SQL/plpgsql functions, procedures, views.\n"
        "-- Bootstrap.sql @include walk expanded recursively.\n"
        "-- ════════════════════════════════════════════════════════════════════\n"
        f"{bootstrap_text}"
    )

    return header + template_section + bootstrap_section


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument(
        "--ext-output",
        default=str(EXT_OUTPUT),
        help="Output path for the unified extension install script.",
    )
    ap.add_argument(
        "--check",
        action="store_true",
        help="Verify the output exists and is non-empty without rewriting",
    )
    args = ap.parse_args()

    ext_path = Path(args.ext_output)

    if args.check:
        ok = True
        if not ext_path.is_file() or ext_path.stat().st_size < 1024:
            print(
                f"[concat_extension_sql] FAIL: missing or too small: {ext_path}",
                file=sys.stderr,
            )
            ok = False
        else:
            print(
                f"[concat_extension_sql] OK: {ext_path} ({ext_path.stat().st_size:,} bytes)"
            )
        return 0 if ok else 1

    ext_text = assemble_unified_extension_sql()

    ext_path.parent.mkdir(parents=True, exist_ok=True)
    ext_path.write_text(ext_text, encoding="utf-8")
    print(
        f"[concat_extension_sql] wrote {ext_path} ({len(ext_text):,} chars)"
    )

    # Retire the legacy split output if it's still on disk — it's no longer
    # part of the install path; leaving it around invites confusion.
    if LEGACY_SUBSTRATE_OUTPUT.is_file():
        LEGACY_SUBSTRATE_OUTPUT.unlink()
        print(
            f"[concat_extension_sql] removed legacy split output {LEGACY_SUBSTRATE_OUTPUT}"
        )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
