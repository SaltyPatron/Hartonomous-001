#!/usr/bin/env python3
"""Linux-native repository discipline inventory and verifier.

Default mode reports findings and exits 0 so a guardrail-first pass can land
before every existing violation is remediated. Use --strict to make findings
fail the command once the current backlog is cleared or baselined.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Iterable


SQL_LITERAL = re.compile(
    r'(?:"|@")\s*'
    r"(SELECT|INSERT|UPDATE|DELETE|CALL|ALTER|DROP|TRUNCATE|COPY|"
    r"CREATE\s+(?:OR\s+REPLACE\s+)?(?:TABLE|FUNCTION|PROCEDURE|VIEW|EXTENSION|SCHEMA|INDEX|TYPE|DOMAIN))\b",
    re.IGNORECASE,
)
NPGSQL_DIRECT_NEW = re.compile(
    r"\bNpgsqlCommand\b.*\bnew\s*\(|\bnew\s+NpgsqlCommand\s*\(",
    re.IGNORECASE,
)
DB_ROUND_TRIP = re.compile(
    r"\b(ExecuteReaderAsync|ExecuteNonQueryAsync|ExecuteScalarAsync|ExecuteSql)\b|"
    r"\bNpgsqlCommand\b.*\bnew\s*\(|\bnew\s+NpgsqlCommand\s*\(",
    re.IGNORECASE,
)
CS_LOOP = re.compile(r"\b(foreach|for|while)\s*\(")
TYPE_DECL = re.compile(
    r"^\s*(public|internal)\s+"
    r"(?:(?:sealed|abstract|static|partial|readonly|ref)\s+)*"
    r"(?:(record)\s+(?:(class|struct)\s+)?)?"
    r"(class|interface|struct|enum|record)\s+"
    r"([A-Za-z_][A-Za-z0-9_]*)\b"
)
SQL_PRIMARY_OBJECT = re.compile(
    r"(?im)^\s*CREATE\s+(?:OR\s+REPLACE\s+)?"
    r"(?P<kind>DOMAIN|TYPE|TABLE|FUNCTION|PROCEDURE|VIEW|SCHEMA|EXTENSION|INDEX|TRIGGER|AGGREGATE)"
    r"\s+(?:IF\s+NOT\s+EXISTS\s+)?"
    r"(?P<name>(?:\"[^\"]+\"|[a-z_][a-z0-9_]*)(?:\s*\.\s*(?:\"[^\"]+\"|[a-z_][a-z0-9_]*))?)\b",
    re.IGNORECASE,
)
BOOTSTRAP_INCLUDE = re.compile(r"@include\s+(schema/\S+)", re.IGNORECASE)
SQL_LOOP = re.compile(r"\b(FOREACH|LOOP|CURSOR|WITH\s+RECURSIVE|WHILE)\b", re.IGNORECASE)
RAW_POSTGIS = re.compile(
    r"\bST_(Distance|Centroid|FrechetDistance|HausdorffDistance)\b",
    re.IGNORECASE,
)
PROHIBITED_COMPUTE_PACKAGES = re.compile(
    r'PackageReference\s+Include="'
    r"(MathNet\.Numerics|Microsoft\.ML|Accord[^\"<>]*|NumSharp|TensorFlow[^\"<>]*|TorchSharp|"
    r"ONNX[^\"<>]*|Eigen[^\"<>]*|MKL[^\"<>]*|Spectra[^\"<>]*)"
    r'"',
    re.IGNORECASE,
)
DECOMPOSER_PIPELINE_BYPASS = re.compile(
    r"\bChannel\.Create|BeginBinaryImport|ResolveEntityIdsAsync|"
    r"\bnew\s+BlockingCollection\b|\bnew\s+ConcurrentQueue\b",
    re.IGNORECASE,
)
LIBRARY_IMPORT = re.compile(r"\bLibraryImport\s*\(")
MANAGED_TEXT_SEGMENTATION_CALL = re.compile(
    r"\b(GraphemeClusters|WordBoundaries|SentenceBoundaries|LineBreaks|UnicodeNormalize|CaseFold|CanonicalTextDecomposer)\s*\.",
    re.IGNORECASE,
)

COMMENT_PREFIXES = ("//", "///", "*", "/*")
SKIP_DIRS = {"bin", "obj", ".git", ".vs", ".idea"}


@dataclass(frozen=True)
class Finding:
    rule: str
    path: str
    line: int
    text: str
    detail: str


@dataclass
class Inventory:
    csharp_files_by_project: dict[str, int]
    type_declarations: int
    interfaces: int
    abstract_classes: int
    base_classes: int
    static_classes: int
    sql_schema_files: int
    embedded_ingestion_sql_files: int
    native_source_files: int
    findings_by_rule: dict[str, int]
    classified_findings_by_rule: dict[str, int]


@dataclass(frozen=True)
class Classification:
    rule: str
    path: str
    text_contains: str
    classification: str
    reason: str


def repo_root() -> Path:
    cur = Path(__file__).resolve()
    for parent in [cur, *cur.parents]:
        if (parent / "Hartonomous.slnx").exists():
            return parent
    raise RuntimeError("Hartonomous.slnx not found")


def rel(root: Path, path: Path) -> str:
    return path.relative_to(root).as_posix()


def iter_files(root: Path, starts: Iterable[str], suffixes: tuple[str, ...]) -> Iterable[Path]:
    for start in starts:
        base = root / start
        if not base.exists():
            continue
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for filename in filenames:
                path = Path(dirpath) / filename
                if path.suffix.lower() in suffixes:
                    yield path


def read_lines(path: Path) -> list[str]:
    return path.read_text(encoding="utf-8-sig", errors="replace").splitlines()


def is_comment_line(line: str, sql: bool = False) -> bool:
    stripped = line.strip()
    if not stripped:
        return True
    if sql:
        return stripped.startswith("--") or stripped.startswith("/*") or stripped.startswith("*")
    return stripped.startswith(COMMENT_PREFIXES)


def normalize_sql_identifier(identifier: str) -> str:
    parts = [p.strip() for p in identifier.split(".")]
    name = parts[-1]
    if name.startswith('"') and name.endswith('"'):
        name = name[1:-1]
    return name.lower()


def remove_sql_comments(sql: str) -> str:
    sql = re.sub(r"/\*.*?\*/", "", sql, flags=re.DOTALL)
    return re.sub(r"(?m)^\s*--.*$", "", sql)


def remove_sql_strings(sql: str) -> str:
    return re.sub(r"'(?:''|[^'])*'", "''", sql)


def load_classifications(root: Path) -> list[Classification]:
    path = root / "scripts/verify/repo_discipline_classifications.json"
    if not path.exists():
        return []

    raw = json.loads(path.read_text(encoding="utf-8"))
    return [Classification(**item) for item in raw]


def classify_findings(
    findings: list[Finding],
    classifications: list[Classification],
) -> tuple[list[Finding], dict[str, int]]:
    active: list[Finding] = []
    classified_by_rule: dict[str, int] = {}

    for finding in findings:
        match = next(
            (
                item for item in classifications
                if item.rule == finding.rule
                and item.path == finding.path
                and item.text_contains in finding.text
            ),
            None,
        )
        if match is None:
            active.append(finding)
            continue

        key = f"{finding.rule}:{match.classification}"
        classified_by_rule[key] = classified_by_rule.get(key, 0) + 1

    return active, dict(sorted(classified_by_rule.items()))


def expected_sql_kinds(relative: str) -> set[str]:
    if relative.startswith("sql/schema/domains/"):
        return {"DOMAIN"}
    if relative.startswith("sql/schema/types/"):
        return {"TYPE"}
    if relative.startswith("sql/schema/tables/"):
        return {"TABLE"}
    if relative.startswith("sql/schema/indexes/"):
        return {"INDEX"}
    if relative.startswith("sql/schema/functions/"):
        return {"FUNCTION"}
    if relative.startswith("sql/schema/procedures/"):
        return {"PROCEDURE"}
    if relative.startswith("sql/schema/views/"):
        return {"VIEW"}
    if relative.startswith("sql/schema/schemas/"):
        return {"SCHEMA"}
    if relative.startswith("sql/schema/extensions/"):
        return {"EXTENSION"}
    return set()


def should_have_primary_sql_object(relative: str) -> bool:
    return relative != "sql/schema/bootstrap.sql" and not relative.startswith("sql/schema/seed/")


def add_finding(
    findings: list[Finding],
    root: Path,
    rule: str,
    path: Path,
    line: int,
    text: str,
    detail: str,
) -> None:
    findings.append(
        Finding(
            rule=rule,
            path=rel(root, path),
            line=line,
            text=text.strip()[:240],
            detail=detail,
        )
    )


def check_inline_sql(root: Path, findings: list[Finding]) -> None:
    allowed = {
        "src/Hartonomous.Core/Data/NpgsqlSubstrateCommand.cs",
        "src/Hartonomous.Core/Data/NpgsqlMonitorCommand.cs",
        "src/Hartonomous.Cli/Commands/BootstrapCommand.cs",
    }
    for path in iter_files(root, ["src"], (".cs",)):
        relative = rel(root, path)
        if relative in allowed or relative.startswith("src/Hartonomous.Engine/Ingestion/Sql/"):
            continue
        for idx, line in enumerate(read_lines(path), start=1):
            if is_comment_line(line):
                continue
            if SQL_LITERAL.search(line):
                add_finding(
                    findings,
                    root,
                    "inline-sql-csharp",
                    path,
                    idx,
                    line,
                    "Production C# SQL should live in sql/schema routines or embedded ingestion SQL resources.",
                )


def check_direct_npgsql(root: Path, findings: list[Finding]) -> None:
    allowed = {
        "src/Hartonomous.Core/Data/NpgsqlSubstrateCommand.cs",
        "src/Hartonomous.Core/Data/NpgsqlMonitorCommand.cs",
        "src/Hartonomous.Core/Data/BaseSubstrateRepository.cs",
        "src/Hartonomous.Cli/Commands/BootstrapCommand.cs",
    }
    allowed_prefixes = (
        "src/Hartonomous.Engine/Ingestion/",
        "tests/",
    )
    for path in iter_files(root, ["src", "tests"], (".cs",)):
        relative = rel(root, path)
        for idx, line in enumerate(read_lines(path), start=1):
            if is_comment_line(line):
                continue
            if not NPGSQL_DIRECT_NEW.search(line):
                continue
            if relative in allowed or relative.startswith(allowed_prefixes):
                continue
            add_finding(
                findings,
                root,
                "direct-npgsql-command",
                path,
                idx,
                line,
                "Construct commands through repository/routine helpers unless this is an approved boundary.",
            )


def check_db_loops(root: Path, findings: list[Finding]) -> None:
    for path in iter_files(root, ["src"], (".cs",)):
        lines = read_lines(path)
        for idx, line in enumerate(lines):
            if is_comment_line(line) or not CS_LOOP.search(line):
                continue
            body = loop_body(lines, idx)
            if DB_ROUND_TRIP.search(body):
                add_finding(
                    findings,
                    root,
                    "db-loop-review",
                    path,
                    idx + 1,
                    line,
                    "Loop contains database command/execute code; prove it is bounded chunking or refactor.",
                )


def loop_body(lines: list[str], start: int) -> str:
    """Return the lexical block for a loop, or a small following window."""
    depth = 0
    seen_open = False
    collected: list[str] = []
    for idx in range(start, min(len(lines), start + 200)):
        line = lines[idx]
        collected.append(line)
        for char in line:
            if char == "{":
                depth += 1
                seen_open = True
            elif char == "}":
                depth -= 1
                if seen_open and depth <= 0:
                    return "\n".join(collected)
        if not seen_open and idx > start + 5:
            return "\n".join(collected)
    return "\n".join(collected)


def check_one_type_per_file(root: Path, findings: list[Finding]) -> None:
    for path in iter_files(root, ["src"], (".cs",)):
        declarations: list[tuple[int, str, str]] = []
        for idx, line in enumerate(read_lines(path), start=1):
            if is_comment_line(line):
                continue
            match = TYPE_DECL.search(line)
            if match:
                kind = match.group(2) or match.group(4)
                name = match.group(5)
                declarations.append((idx, kind, name))
        if len(declarations) <= 1:
            continue
        summary = ", ".join(f"{kind} {name}@{line}" for line, kind, name in declarations)
        add_finding(
            findings,
            root,
            "one-type-per-file-review",
            path,
            declarations[1][0],
            summary,
            "Multiple public/internal type declarations in one file; split unless covered by the companion-record exception.",
        )


def check_compute_dependencies(root: Path, findings: list[Finding]) -> None:
    for path in iter_files(root, ["src", "tests"], (".csproj",)):
        relative = rel(root, path)
        for idx, line in enumerate(read_lines(path), start=1):
            match = PROHIBITED_COMPUTE_PACKAGES.search(line)
            if not match:
                continue
            if relative.startswith("src/Hartonomous.Core/"):
                continue
            add_finding(
                findings,
                root,
                "direct-compute-package",
                path,
                idx,
                line,
                f"Package {match.group(1)} is outside the Core compute/native boundary.",
            )


def check_raw_postgis(root: Path, findings: list[Finding]) -> None:
    for path in iter_files(root, ["src", "sql/schema"], (".cs", ".sql")):
        for idx, line in enumerate(read_lines(path), start=1):
            if is_comment_line(line, sql=path.suffix == ".sql"):
                continue
            if RAW_POSTGIS.search(line):
                add_finding(
                    findings,
                    root,
                    "raw-postgis-physicality-review",
                    path,
                    idx,
                    line,
                    "Raw PostGIS distance/centroid/Frechet/Hausdorff calls must not operate on substrate physicality.",
                )


def check_sql_loops(root: Path, findings: list[Finding]) -> None:
    for path in iter_files(root, ["sql/schema"], (".sql",)):
        for idx, line in enumerate(read_lines(path), start=1):
            if is_comment_line(line, sql=True):
                continue
            if SQL_LOOP.search(remove_sql_strings(line)):
                add_finding(
                    findings,
                    root,
                    "sql-loop-review",
                    path,
                    idx,
                    line,
                    "SQL loop/recursive construct needs classification as validation-only, bounded, set-based, or native-offload candidate.",
                )


def check_schema_shape(root: Path, findings: list[Finding]) -> None:
    schema_root = root / "sql/schema"
    bootstrap = schema_root / "bootstrap.sql"
    if not bootstrap.exists():
        add_finding(findings, root, "sql-schema-shape", bootstrap, 1, "", "Missing sql/schema/bootstrap.sql.")
        return

    included: set[str] = set()
    for match in BOOTSTRAP_INCLUDE.finditer(bootstrap.read_text(encoding="utf-8-sig", errors="replace")):
        include = match.group(1).replace("\\", "/")
        included.add(include.lower())
        full = root / "sql" / include
        if not full.exists():
            add_finding(
                findings,
                root,
                "sql-schema-shape",
                bootstrap,
                1,
                include,
                "Bootstrap include target does not exist.",
            )

    for path in iter_files(root, ["sql/schema"], (".sql",)):
        relative = rel(root, path)
        if relative != "sql/schema/bootstrap.sql":
            include_path = relative[4:].lower()
            if include_path not in included:
                add_finding(
                    findings,
                    root,
                    "sql-schema-shape",
                    path,
                    1,
                    relative,
                    "Canonical schema SQL file is not included by sql/schema/bootstrap.sql.",
                )

        if not should_have_primary_sql_object(relative):
            continue
        clean = remove_sql_comments(path.read_text(encoding="utf-8-sig", errors="replace"))
        objects = [
            (m.group("kind").upper(), normalize_sql_identifier(m.group("name")))
            for m in SQL_PRIMARY_OBJECT.finditer(clean)
        ]
        if len(objects) != 1:
            add_finding(
                findings,
                root,
                "sql-schema-shape",
                path,
                1,
                relative,
                f"Expected exactly one primary CREATE object; found {len(objects)}.",
            )
            continue

        kind, name = objects[0]
        expected = expected_sql_kinds(relative)
        if expected and kind not in expected:
            add_finding(
                findings,
                root,
                "sql-schema-shape",
                path,
                1,
                relative,
                f"Object kind {kind} does not belong here; expected {', '.join(sorted(expected))}.",
            )
        expected_name = path.stem.lower()
        if name != expected_name and not expected_name.endswith(f"_{name}"):
            add_finding(
                findings,
                root,
                "sql-schema-shape",
                path,
                1,
                relative,
                f"File name '{expected_name}' does not match {kind.lower()} '{name}'.",
            )


def check_linux_scripts_no_powershell(root: Path, findings: list[Finding]) -> None:
    invocation = re.compile(r"(^|[;&|()`\s])(pwsh|powershell)(\s|$)", re.IGNORECASE)
    for path in iter_files(root, ["scripts/linux"], (".sh",)):
        for idx, line in enumerate(read_lines(path), start=1):
            if is_comment_line(line):
                continue
            if invocation.search(line):
                add_finding(
                    findings,
                    root,
                    "linux-powershell-invocation",
                    path,
                    idx,
                    line,
                    "Linux scripts must not invoke PowerShell.",
                )


def check_decomposer_pipeline_bypass(root: Path, findings: list[Finding]) -> None:
    for path in iter_files(root, ["src/Hartonomous.Decomposers"], (".cs",)):
        for idx, line in enumerate(read_lines(path), start=1):
            if is_comment_line(line):
                continue
            if DECOMPOSER_PIPELINE_BYPASS.search(line):
                add_finding(
                    findings,
                    root,
                    "decomposer-pipeline-bypass",
                    path,
                    idx,
                    line,
                    "Decomposers must emit through IRecordSink/IIngestionPipeline, not own channels, COPY, queues, or phase-wide ID resolution.",
                )


def check_native_boundary(root: Path, findings: list[Finding]) -> None:
    allowed = {
        "src/Hartonomous.Core/Compute/Internal/NativeCompute.cs",
        "src/Hartonomous.Core/Native/TextDecomposeNative.cs",
    }
    for path in iter_files(root, ["src"], (".cs",)):
        relative = rel(root, path)
        for idx, line in enumerate(read_lines(path), start=1):
            if is_comment_line(line):
                continue
            if LIBRARY_IMPORT.search(line) and relative not in allowed:
                add_finding(
                    findings,
                    root,
                    "native-boundary-bypass",
                    path,
                    idx,
                    line,
                    "P/Invoke belongs in NativeCompute, except the text-decompose callback ABI in TextDecomposeNative.",
                )


def check_managed_text_segmentation_usage(root: Path, findings: list[Finding]) -> None:
    for path in iter_files(root, ["src"], (".cs",)):
        relative = rel(root, path)
        if relative.startswith("src/Hartonomous.Core/Text/Segmentation/"):
            continue
        if relative == "src/Hartonomous.Core/Text/CanonicalTextDecomposer.cs":
            continue
        for idx, line in enumerate(read_lines(path), start=1):
            if is_comment_line(line):
                continue
            if MANAGED_TEXT_SEGMENTATION_CALL.search(line):
                add_finding(
                    findings,
                    root,
                    "managed-text-segmentation-bypass",
                    path,
                    idx,
                    line,
                    "Production text decomposition must marshal to SubstrateTextDecomposer/native text_decompose; managed segmenters are reference/test-only.",
                )


def collect_inventory(
    root: Path,
    findings: list[Finding],
    classified_by_rule: dict[str, int],
) -> Inventory:
    csharp_files_by_project: dict[str, int] = {}
    type_declarations = 0
    interfaces = 0
    abstract_classes = 0
    base_classes = 0
    static_classes = 0

    for path in iter_files(root, ["src", "tests"], (".cs",)):
        relative = rel(root, path)
        parts = relative.split("/")
        project = "/".join(parts[:2]) if len(parts) >= 2 else parts[0]
        csharp_files_by_project[project] = csharp_files_by_project.get(project, 0) + 1

        for line in read_lines(path):
            if is_comment_line(line):
                continue
            match = TYPE_DECL.search(line)
            if not match:
                continue
            type_declarations += 1
            kind = match.group(2) or match.group(4)
            name = match.group(5)
            if kind == "interface":
                interfaces += 1
            if "abstract class" in line:
                abstract_classes += 1
            if name.startswith("Base"):
                base_classes += 1
            if "static class" in line:
                static_classes += 1

    findings_by_rule: dict[str, int] = {}
    for finding in findings:
        findings_by_rule[finding.rule] = findings_by_rule.get(finding.rule, 0) + 1

    return Inventory(
        csharp_files_by_project=dict(sorted(csharp_files_by_project.items())),
        type_declarations=type_declarations,
        interfaces=interfaces,
        abstract_classes=abstract_classes,
        base_classes=base_classes,
        static_classes=static_classes,
        sql_schema_files=sum(1 for _ in iter_files(root, ["sql/schema"], (".sql",))),
        embedded_ingestion_sql_files=sum(
            1 for _ in iter_files(root, ["src/Hartonomous.Engine/Ingestion/Sql"], (".sql",))
        ),
        native_source_files=sum(
            1
            for _ in iter_files(
                root,
                ["ext"],
                (".c", ".cc", ".cpp", ".h", ".hpp"),
            )
        ),
        findings_by_rule=dict(sorted(findings_by_rule.items())),
        classified_findings_by_rule=classified_by_rule,
    )


def print_text(inventory: Inventory, findings: list[Finding], max_findings: int) -> None:
    print("Hartonomous repository discipline inventory")
    print()
    print("C# files by project:")
    for project, count in inventory.csharp_files_by_project.items():
        print(f"  {project}: {count}")
    print()
    print("Type/SQL/native inventory:")
    print(f"  type declarations: {inventory.type_declarations}")
    print(f"  interfaces: {inventory.interfaces}")
    print(f"  abstract classes: {inventory.abstract_classes}")
    print(f"  Base* classes: {inventory.base_classes}")
    print(f"  static classes: {inventory.static_classes}")
    print(f"  sql/schema files: {inventory.sql_schema_files}")
    print(f"  embedded ingestion SQL files: {inventory.embedded_ingestion_sql_files}")
    print(f"  native source/header files: {inventory.native_source_files}")
    print()
    print("Findings by rule:")
    if inventory.findings_by_rule:
        for rule, count in inventory.findings_by_rule.items():
            print(f"  {rule}: {count}")
    else:
        print("  none")
    if inventory.classified_findings_by_rule:
        print()
        print("Classified non-findings:")
        for rule, count in inventory.classified_findings_by_rule.items():
            print(f"  {rule}: {count}")
    print()

    if findings:
        print(f"Findings (first {min(max_findings, len(findings))} of {len(findings)}):")
        for finding in findings[:max_findings]:
            print(f"  {finding.path}:{finding.line} [{finding.rule}] {finding.text}")
            print(f"    {finding.detail}")
        if len(findings) > max_findings:
            print(f"  ... {len(findings) - max_findings} more finding(s) omitted")
    else:
        print("No findings.")


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", action="store_true", help="emit JSON instead of text")
    parser.add_argument("--strict", action="store_true", help="exit non-zero when findings exist")
    parser.add_argument("--max-findings", type=int, default=120, help="maximum text findings to print")
    parser.add_argument(
        "--write-inventory",
        type=Path,
        help="write full inventory and findings JSON to this path",
    )
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    root = repo_root()
    findings: list[Finding] = []

    check_inline_sql(root, findings)
    check_direct_npgsql(root, findings)
    check_db_loops(root, findings)
    check_one_type_per_file(root, findings)
    check_compute_dependencies(root, findings)
    check_raw_postgis(root, findings)
    check_sql_loops(root, findings)
    check_schema_shape(root, findings)
    check_linux_scripts_no_powershell(root, findings)
    check_decomposer_pipeline_bypass(root, findings)
    check_native_boundary(root, findings)
    check_managed_text_segmentation_usage(root, findings)

    findings.sort(key=lambda f: (f.rule, f.path, f.line, f.text))
    findings, classified_by_rule = classify_findings(findings, load_classifications(root))
    inventory = collect_inventory(root, findings, classified_by_rule)
    payload = {
        "inventory": asdict(inventory),
        "findings": [asdict(f) for f in findings],
    }

    if args.write_inventory:
        args.write_inventory.parent.mkdir(parents=True, exist_ok=True)
        args.write_inventory.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")

    if args.json:
        print(json.dumps(payload, indent=2))
    else:
        print_text(inventory, findings, args.max_findings)
        if findings and not args.strict:
            print()
            print("Report-only mode: use --strict to fail on findings after remediation/baselining.")

    return 1 if args.strict and findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
