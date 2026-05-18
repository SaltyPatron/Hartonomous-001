#!/usr/bin/env python3
"""
concat_extension_sql.py — assemble two outputs that together install the
Hartonomous substrate:

  (1) ext/hartonomous_pg/sql/hartonomous--1.0.sql
      = the hand-written `.sql.in` template, lightly cleaned (psql meta /
        raw transaction control stripped). This file is C-binding declarations
        ONLY — CREATE TYPE / CREATE FUNCTION ... LANGUAGE C / operators /
        opclasses / aggregates / casts / domains / views over those types /
        thin plpgsql wrappers. NO substrate tables. NO seed INSERTs. NO
        substrate.* SQL functions that operate on substrate tables.
        `CREATE EXTENSION hartonomous` runs this script, which installs the
        .so's exposed surface into the database.

  (2) ext/hartonomous_pg/sql/substrate-schema.sql
      = the bootstrap.sql `@include` walk expanded — substrate / monitor
        schemas, domains, composite types, reference + core + junction +
        model + monitor + meta tables, indexes, seed inserts, substrate.*
        SQL/plpgsql functions, procedures, views. Applied via plain
        `psql -f` in user mode (no sudo). This file contains everything
        that does NOT depend on creating an extension — i.e. everything
        the user owns under their own role rather than the extension's
        owner.

Pipeline:
  1. Read bootstrap.sql; for each `-- @include path` directive, expand
     recursively. Same semantics as the original.
  2. Skip extensions/*.sql files — those become `requires` in the control
     file. Cannot CREATE EXTENSION inside an extension script. (For (2),
     they ALSO skip, because the runtime apply path installs CREATE
     EXTENSION hartonomous separately and its prerequisites cascade.)
  3. Strip psql meta-commands and raw transaction control.
  4. Output (1) = the .sql.in template (cleaned). NO bootstrap content.
  5. Output (2) = the bootstrap @include walk. NO .sql.in content.

Splitting the install:
  - `CREATE EXTENSION hartonomous` previously dragged in all substrate
    table definitions as extension-owned objects, which (a) required root
    to copy the consolidated SQL into PG share, and (b) caused at least
    one PG-extension-ownership quirk that stripped GENERATED columns from
    substrate.entity. With this split, the extension only owns the .so's
    declarative surface (4D types, operators, BLAKE3, traversal, UCD
    catalog accessors) and the substrate schema is owned by the user.

Determinism: same source tree → byte-identical outputs.
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
SUBSTRATE_OUTPUT = EXT_SRC / "substrate-schema.sql"


INCLUDE_RE = re.compile(r"^\s*--\s*@include\s+(?P<path>\S+)\s*$", re.MULTILINE)
META_LINE_RE = re.compile(
    r"^\s*\\(?:set|echo|connect|quit|timing|pset|c|cd|conninfo|encoding|password|gset)\b.*$",
    re.MULTILINE | re.IGNORECASE,
)
# Top-level transaction-control statements that are forbidden inside an
# extension script (extensions run in an implicit transaction). The runtime
# substrate-schema apply uses an outer BEGIN/COMMIT around the psql -f so
# stripping these here is still correct — the wrapper provides the
# transaction.
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


def render_parts(parts: list[tuple[Path, str]], header: str) -> str:
    chunks: list[str] = [header]
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


def assemble_extension_sql() -> str:
    """Output (1) — C-binding declarations only.

    Just the .sql.in template, cleaned. Installed by `CREATE EXTENSION
    hartonomous` as the .so's declarative surface in PostgreSQL.
    """
    if not EXT_TEMPLATE.is_file():
        raise FileNotFoundError(
            f".sql.in template not found at {EXT_TEMPLATE} — should contain "
            "C-binding declarations (point4d, traversal, BLAKE3, etc.)"
        )

    template_text = EXT_TEMPLATE.read_text(encoding="utf-8")
    template_text = strip_psql_meta(template_text)

    header = (
        "/* GENERATED — do not edit by hand. Source: "
        f"{EXT_TEMPLATE.name}.\n"
        " * Concatenated by: scripts/build/concat_extension_sql.py\n"
        " *\n"
        " * This script is C-binding declarations ONLY. Installed by\n"
        " *   CREATE EXTENSION hartonomous\n"
        " * which runs this script under the extension owner. Substrate\n"
        " * schema (tables, indexes, junctions, seed, plpgsql functions,\n"
        " * views) is owned by the user and applied separately via\n"
        " *   psql -f ext/hartonomous_pg/sql/substrate-schema.sql\n"
        " * after CREATE EXTENSION succeeds. See\n"
        " * scripts/linux/db-bootstrap.sh for the runtime apply path.\n"
        " *\n"
        " * Prerequisite extensions (postgis, btree_gist, pg_trgm) are\n"
        " * declared in hartonomous.control's `requires` and installed\n"
        " * automatically by CREATE EXTENSION ... CASCADE. */\n"
    )
    if not template_text.endswith("\n"):
        template_text += "\n"
    return header + template_text


def assemble_substrate_schema() -> str:
    """Output (2) — substrate schema (no C bindings).

    Bootstrap.sql @include walk expanded. Applied via plain psql -f after
    CREATE EXTENSION hartonomous installs the C-binding surface. This file
    references public.point4d, substrate.cp_hash, etc. — those C bindings
    must exist in the database before this script runs.
    """
    if not BOOTSTRAP_FILE.is_file():
        raise FileNotFoundError(f"bootstrap.sql not found at {BOOTSTRAP_FILE}")

    parts = expand_file(BOOTSTRAP_FILE)

    header = (
        "/* GENERATED — do not edit by hand. Source: "
        "sql/schema/bootstrap.sql + included files.\n"
        " * Concatenated by: scripts/build/concat_extension_sql.py\n"
        " *\n"
        " * This script is substrate schema content — substrate / monitor\n"
        " * schemas, domains, composite types, tables, indexes, seed inserts,\n"
        " * substrate.* SQL/plpgsql functions, procedures, views. Applied\n"
        " * via plain psql -f under the user's database role (no sudo).\n"
        " *\n"
        " * Prerequisite — the hartonomous extension must already be\n"
        " * installed via CREATE EXTENSION hartonomous (which cascades the\n"
        " * postgis + btree_gist + pg_trgm prerequisites and installs the\n"
        " * .so's C-binding declarations). The substrate / monitor schemas\n"
        " * must also exist before this script runs — the extension's\n"
        " * C-binding script creates functions inside substrate.*. See\n"
        " * scripts/linux/db-bootstrap.sh for the runtime apply path. */\n"
    )
    return render_parts(parts, header)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument(
        "--ext-output",
        default=str(EXT_OUTPUT),
        help="Output path for the C-binding extension script.",
    )
    ap.add_argument(
        "--substrate-output",
        default=str(SUBSTRATE_OUTPUT),
        help="Output path for the substrate schema script.",
    )
    ap.add_argument(
        "--check",
        action="store_true",
        help="Verify both outputs exist and are non-empty without rewriting",
    )
    args = ap.parse_args()

    ext_path = Path(args.ext_output)
    substrate_path = Path(args.substrate_output)

    if args.check:
        ok = True
        for label, p, min_size in (
            ("extension", ext_path, 1024),
            ("substrate", substrate_path, 1024),
        ):
            if not p.is_file() or p.stat().st_size < min_size:
                print(
                    f"[concat_extension_sql] FAIL ({label}): missing or too small: {p}",
                    file=sys.stderr,
                )
                ok = False
            else:
                print(
                    f"[concat_extension_sql] OK ({label}): {p} ({p.stat().st_size:,} bytes)"
                )
        return 0 if ok else 1

    ext_text = assemble_extension_sql()
    substrate_text = assemble_substrate_schema()

    ext_path.parent.mkdir(parents=True, exist_ok=True)
    substrate_path.parent.mkdir(parents=True, exist_ok=True)
    ext_path.write_text(ext_text, encoding="utf-8")
    substrate_path.write_text(substrate_text, encoding="utf-8")
    print(
        f"[concat_extension_sql] wrote {ext_path} ({len(ext_text):,} chars)"
    )
    print(
        f"[concat_extension_sql] wrote {substrate_path} ({len(substrate_text):,} chars)"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
