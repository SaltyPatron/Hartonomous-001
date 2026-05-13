#!/usr/bin/env python3
"""
concat_extension_sql.py — assemble the canonical PostgreSQL extension
script from the multi-file substrate source tree.

Pattern matches PostGIS / pgvector: maintain hand-written `.sql.in` +
many small per-object source files; concatenate them in dependency
order at build time into a single `<extension>--<version>.sql`.

Inputs:
  sql/schema/bootstrap.sql                — declares @include order
  sql/schema/**/*.sql                     — per-object DDL
  ext/hartonomous_pg/sql/hartonomous--1.0.sql.in
                                          — C-binding declarations
                                            (point4d, traversal, BLAKE3,
                                            substrate.cp_*, etc.)

Output:
  ext/hartonomous_pg/sql/hartonomous--1.0.sql  (build artifact; gitignored)

Pipeline:
  1. Read bootstrap.sql; for each `-- @include path` directive, expand
     by recursively reading the referenced file. Same semantics as
     C# MigrationFileLoader.LoadResolved.
  2. Skip extensions/*.sql files — those become `requires` in the
     control file. Cannot CREATE EXTENSION inside an extension script.
  3. Strip psql meta-commands incompatible with extension context:
       \\set, \\echo, \\connect, \\quit, \\timing, \\pset
  4. Strip raw transaction-control statements (BEGIN/COMMIT/ROLLBACK):
     extension scripts run in an implicit transaction.
  5. Insert the hand-written .sql.in BEFORE the first functions/* include
     (C-binding declarations need to exist before SQL functions in
     functions/* can reference public.point4d, substrate.cp_hash, etc.).
  6. Write the consolidated output.

Determinism: same source tree → byte-identical output. Run via
scripts/build/ExtensionSql.ps1 in the build chain.
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


INCLUDE_RE = re.compile(r"^\s*--\s*@include\s+(?P<path>\S+)\s*$", re.MULTILINE)
META_LINE_RE = re.compile(
    r"^\s*\\(?:set|echo|connect|quit|timing|pset|c|cd|conninfo|encoding|password|gset)\b.*$",
    re.MULTILINE | re.IGNORECASE,
)
# Top-level transaction-control statements that are forbidden inside an
# extension script (extensions run in an implicit transaction).
#
# Important distinctions vs PL/pgSQL block syntax:
#  * `BEGIN` (no semicolon) inside a DO $$ ... $$ block opens a PL/pgSQL
#    block — DO NOT strip. Requiring trailing `;` excludes it.
#  * `END;` inside a CREATE FUNCTION / DO body closes a PL/pgSQL block
#    and IS followed by `;` (then `$$;`). DO NOT strip. We omit `END`
#    from the keyword list entirely; `END` as an alias for `COMMIT` is
#    obsolete and not used in our schema sources (verified empty).
#  * Top-level `BEGIN;` / `COMMIT;` / `ROLLBACK;` / `START TRANSACTION;`
#    should be stripped if any source file accidentally contains one.
TXN_LINE_RE = re.compile(
    r"^\s*(?:BEGIN|COMMIT|ROLLBACK|START\s+TRANSACTION)\s*;\s*$",
    re.MULTILINE | re.IGNORECASE,
)

# Extension prerequisites — these become `requires` in the control file
# instead of being CREATE EXTENSION'd inside our script.
PREREQUISITE_EXTENSIONS = {"postgis", "btree_gist", "pg_trgm"}


def strip_psql_meta(content: str) -> str:
    """Remove psql backslash meta-commands and raw transaction control."""
    content = META_LINE_RE.sub("", content)
    content = TXN_LINE_RE.sub("", content)
    return content


def is_extension_creation_file(rel_include_path: str) -> bool:
    """Files under schema/extensions/ that wrap CREATE EXTENSION calls."""
    norm = rel_include_path.replace("\\", "/")
    return norm.startswith("schema/extensions/") and norm.endswith(".sql")


def expand_file(path: Path, depth: int = 0) -> tuple[list[tuple[Path, str]], list[str]]:
    """Recursively expand @include directives, returning a list of
    (source_path, content) pairs in declaration order plus a parallel list
    of the original include strings (for inserting hooks)."""
    if depth > 16:
        raise RuntimeError(f"@include depth limit exceeded: {path}")
    text = path.read_text(encoding="utf-8")

    parts: list[tuple[Path, str]] = []
    cursor = 0
    for m in INCLUDE_RE.finditer(text):
        head = text[cursor : m.start()]
        if head.strip():
            parts.append((path, head))
        rel = m.group("path").replace("/", "/").replace("\\", "/")
        if is_extension_creation_file(rel):
            # Substitute a comment so the relative ordering is preserved
            parts.append((path, f"-- (skipped @include {rel} — handled via control file `requires`)\n"))
            cursor = m.end()
            continue
        target = (SQL_ROOT / rel).resolve()
        if not target.is_file():
            raise FileNotFoundError(
                f"@include not found: {rel} (resolved {target}) referenced from {path}"
            )
        sub_parts, _ = expand_file(target, depth + 1)
        parts.extend(sub_parts)
        cursor = m.end()
    tail = text[cursor:]
    if tail.strip():
        parts.append((path, tail))

    return parts, []


def assemble() -> str:
    if not BOOTSTRAP_FILE.is_file():
        raise FileNotFoundError(f"bootstrap.sql not found at {BOOTSTRAP_FILE}")
    if not EXT_TEMPLATE.is_file():
        raise FileNotFoundError(
            f".sql.in template not found at {EXT_TEMPLATE} — should contain "
            "C-binding declarations (point4d, traversal, BLAKE3, etc.)"
        )

    parts, _ = expand_file(BOOTSTRAP_FILE)

    # Native types are column types in core tables, so the C-binding
    # declarations must be available before the first table include.
    insert_index = None
    for i, (src, content) in enumerate(parts):
        srcstr = str(src).replace("\\", "/")
        if "/sql/schema/tables/" in srcstr:
            insert_index = i
            break
    if insert_index is None:
        raise RuntimeError("bootstrap.sql has no table includes; cannot place native type declarations")

    template_text = EXT_TEMPLATE.read_text(encoding="utf-8")
    template_block = (
        "\n-- ════════════════════════════════════════════════════════════════════\n"
        f"-- Native C-binding declarations (from {EXT_TEMPLATE.name})\n"
        "-- ════════════════════════════════════════════════════════════════════\n"
        f"{template_text}\n"
    )
    parts.insert(insert_index, (EXT_TEMPLATE, template_block))

    # Build output. Strip psql meta from each part.
    chunks: list[str] = []
    chunks.append(
        "/* GENERATED — do not edit by hand. Source: sql/schema/**/*.sql + "
        "ext/hartonomous_pg/sql/hartonomous--1.0.sql.in.\n"
        "   Build via: pwsh scripts/build/ExtensionSql.ps1\n"
        " * Concatenated by: scripts/build/concat_extension_sql.py\n"
        " * Order: sql/schema/bootstrap.sql @include directives.\n"
        " *\n"
        " * Prerequisite extensions (postgis, btree_gist, pg_trgm) are\n"
        " * declared in hartonomous.control's `requires` and installed\n"
        " * automatically by CREATE EXTENSION. */\n"
    )
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


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--output", default=str(EXT_OUTPUT))
    ap.add_argument("--check", action="store_true",
                    help="Verify output exists and is non-empty without rewriting")
    args = ap.parse_args()

    output_path = Path(args.output)
    if args.check:
        if not output_path.is_file() or output_path.stat().st_size < 1024:
            print(f"[concat_extension_sql] FAIL: missing or too small: {output_path}",
                  file=sys.stderr)
            return 1
        print(f"[concat_extension_sql] OK: {output_path} ({output_path.stat().st_size:,} bytes)")
        return 0

    text = assemble()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(text, encoding="utf-8")
    print(f"[concat_extension_sql] wrote {output_path} ({len(text):,} chars)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
