#!/usr/bin/env python3
"""
generate_unicode_tables.py — bake the complete UCD/UCA 17.0.0 catalog into
the hartonomous PG extension as generated C tables.

After this generator runs, the substrate has zero runtime dependency on
on-disk UCD/UCA files: every property, every collation weight, every
decomposition mapping is embedded in the extension binary as a flat C
array indexed by codepoint value. UCD/UCA only matters again when a new
Unicode version drops and the extension is rebuilt.

Inputs:  D:/Models/UCD/Public/UCD/latest/  (UCD 17.0.0 file tree)
Outputs (under ext/hartonomous_pg/src/generated/):

    pg_unicode_version.h         — UCD/UCA version stamp
    pg_unicode_props.h/.c        — per-codepoint flat byte/word arrays
                                   (gcb, wb, sb, lb, incb, ext_picto, gc,
                                    ccc, script, block, simple case maps,
                                    uca_index, bidi, eaw, hsy, num_type)
    pg_codepoint_atoms.h/.c      — per-codepoint precomputed BLAKE3 hash,
                                   4D Super-Fibonacci centroid, Hilbert
                                   index, sorted-by-hash reverse lookup
    pg_unicode_inventory.h/.c    — string code → enum tables (GC codes +
                                   descriptions, script names, block
                                   name+range, break property codes per
                                   category)
    pg_unicode_varlen.h/.c       — variable-length data tables:
                                     - decomposition mappings
                                     - full case folding
                                     - UCA weight tuples
                                     - codepoint names

Determinism (Law #6): the embedded tables freeze the UCD/UCA version at
extension compile time. Same UCD version → byte-identical answers.
A new UCD version requires rerunning this script + extension rebuild.

Run:  python scripts/build/generate_unicode_tables.py
      python scripts/build/generate_unicode_tables.py --ucd-root D:/Models/UCD/Public/UCD/17.1.0
"""
import argparse
import math
import os
import re
import struct
import sys
from pathlib import Path
from typing import Dict, List, Optional, Tuple

UNICODE_MAX = 0x110000
HASH_LEN    = 32
GOLDEN_PHI  = 1.6180339887498949
GOLDEN_PSI  = 1.5436890126920763

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
GEN_DIR   = REPO_ROOT / "ext" / "hartonomous_pg" / "src" / "generated"


def blake3_4byte(rune: int) -> bytes:
    data = struct.pack(">I", rune)
    try:
        import blake3 as _b3
        return _b3.blake3(data).digest()
    except ImportError:
        try:
            import ctypes
            for p in [REPO_ROOT / "ext/libhartonomous/build/bin/Release/hartonomous.dll",
                      REPO_ROOT / "ext/libhartonomous/build/lib/libhartonomous.so"]:
                if p.exists():
                    lib = ctypes.CDLL(str(p))
                    out = (ctypes.c_uint8 * 32)()
                    lib.hartonomous_blake3(
                        (ctypes.c_uint8 * 4).from_buffer_copy(data),
                        ctypes.c_size_t(4), out)
                    return bytes(out)
            raise RuntimeError("libhartonomous not built")
        except Exception as e:
            raise SystemExit(
                f"BLAKE3 unavailable: {e}\n"
                "Install: pip install blake3 (or build ext/libhartonomous first)"
            )


def super_fibonacci_4d(i: int, n: int) -> Tuple[float, float, float, float]:
    if n <= 0 or i < 0 or i >= n:
        return (0.0, 0.0, 0.0, 0.0)
    s = i + 0.5
    t = s / n
    d = 2.0 * math.pi * s
    r1 = math.sqrt(t)
    r2 = math.sqrt(1.0 - t)
    a1 = d / GOLDEN_PHI
    a2 = d / GOLDEN_PSI
    return (r1 * math.sin(a1), r1 * math.cos(a1),
            r2 * math.sin(a2), r2 * math.cos(a2))


HILBERT_BITS = 16

def hilbert_4d_encode(p: Tuple[float, float, float, float]) -> int:
    qmax = (1 << HILBERT_BITS) - 1
    X = [max(0, min(qmax, int(round((p[i] + 1.0) * 0.5 * qmax)))) for i in range(4)]
    M = 1 << (HILBERT_BITS - 1)
    Q = M
    while Q > 1:
        P = Q - 1
        for i in range(4):
            if X[i] & Q: X[0] ^= P
            else:
                t = (X[0] ^ X[i]) & P
                X[0] ^= t; X[i] ^= t
        Q >>= 1
    for i in range(1, 4): X[i] ^= X[i - 1]
    t = 0; Q = M
    while Q > 1:
        if X[3] & Q: t ^= Q - 1
        Q >>= 1
    for i in range(4): X[i] ^= t
    out = 0
    for bit in range(HILBERT_BITS - 1, -1, -1):
        for i in range(4):
            out = (out << 1) | ((X[i] >> bit) & 1)
    return out


# ── UCD parsers ──────────────────────────────────────────────────────────
def parse_ranged_property(path, allowed=None):
    out = {}
    with path.open("r", encoding="utf-8") as f:
        for raw in f:
            line = raw.split("#", 1)[0].strip()
            if not line: continue
            parts = [p.strip() for p in line.split(";")]
            if len(parts) < 2: continue
            rng, value = parts[0], parts[1]
            if allowed is not None and value not in allowed: continue
            if ".." in rng:
                lo, hi = rng.split("..")
                lo_i, hi_i = int(lo, 16), int(hi, 16)
            else:
                lo_i = hi_i = int(rng, 16)
            for cp in range(lo_i, hi_i + 1):
                out[cp] = value
    return out


def parse_codepoint_set_property(path, property_name):
    out = set()
    with path.open("r", encoding="utf-8") as f:
        for raw in f:
            line = raw.split("#", 1)[0].strip()
            if not line:
                continue
            parts = [p.strip() for p in line.split(";")]
            if len(parts) < 2 or parts[1] != property_name:
                continue
            rng = parts[0]
            if ".." in rng:
                lo, hi = rng.split("..")
                lo_i, hi_i = int(lo, 16), int(hi, 16)
            else:
                lo_i = hi_i = int(rng, 16)
            out.update(range(lo_i, hi_i + 1))
    return out


def parse_unicode_data(path):
    out = {}
    range_starts = {}
    with path.open("r", encoding="utf-8") as f:
        for raw in f:
            fields = raw.rstrip("\n").split(";")
            if len(fields) < 15: continue
            cp = int(fields[0], 16)
            entry = {
                "name": fields[1], "gc": fields[2],
                "ccc": int(fields[3]) if fields[3] else 0,
                "bidi": fields[4],
                "decomp": fields[5],
                "numeric_type_decimal": fields[6],
                "numeric_type_digit":   fields[7],
                "numeric_type_value":   fields[8],
                "upper": int(fields[12], 16) if fields[12] else 0,
                "lower": int(fields[13], 16) if fields[13] else 0,
                "title": int(fields[14], 16) if fields[14] else 0,
            }
            if entry["name"].endswith(", First>"):
                range_starts[cp] = entry; continue
            if entry["name"].endswith(", Last>"):
                start_cp = max(k for k in range_starts.keys() if k <= cp)
                start_entry = range_starts.pop(start_cp)
                for c in range(start_cp, cp + 1):
                    out[c] = dict(start_entry)
                continue
            out[cp] = entry
    return out


def parse_decomposition_field(s):
    s = s.strip()
    if not s: return (None, [])
    typ = None
    parts = s.split()
    if parts[0].startswith("<") and parts[0].endswith(">"):
        typ = parts[0][1:-1]
        parts = parts[1:]
    return (typ, [int(p, 16) for p in parts if p])


def parse_case_folding(path):
    simple, full = {}, {}
    with path.open("r", encoding="utf-8") as f:
        for raw in f:
            line = raw.split("#", 1)[0].strip()
            if not line: continue
            parts = [p.strip() for p in line.split(";")]
            if len(parts) < 4: continue
            cp = int(parts[0], 16)
            status = parts[1]
            mapping = [int(x, 16) for x in parts[2].split()]
            if status == "C" or status == "S":
                simple[cp] = mapping[0]
                full[cp] = mapping
            elif status == "F":
                full[cp] = mapping
    return simple, full


def parse_uca_allkeys(path):
    out = {}
    with path.open("r", encoding="utf-8") as f:
        for raw in f:
            line = raw.split("#", 1)[0].strip()
            if not line or line.startswith("@"): continue
            if ";" not in line: continue
            cps_part, weights_part = line.split(";", 1)
            cps = [int(x, 16) for x in cps_part.split()]
            if len(cps) != 1: continue
            weights = []
            for m in re.finditer(r"\[\.([0-9A-Fa-f]+)\.([0-9A-Fa-f]+)\.([0-9A-Fa-f]+)\]", weights_part):
                weights.append((int(m.group(1), 16), int(m.group(2), 16), int(m.group(3), 16)))
            out[cps[0]] = weights
    return out


def parse_blocks(path):
    out = []
    with path.open("r", encoding="utf-8") as f:
        for raw in f:
            line = raw.split("#", 1)[0].strip()
            if not line or ";" not in line: continue
            rng, name = [p.strip() for p in line.split(";", 1)]
            lo, hi = rng.split("..")
            out.append((int(lo, 16), int(hi, 16), name))
    return out


# ── Code emission helpers ────────────────────────────────────────────────
def emit_uint8_array(name, vals):
    L = [f"const uint8_t {name}[{len(vals)}] = {{"]
    for i in range(0, len(vals), 32):
        L.append("    " + ", ".join(f"{v:>3d}" for v in vals[i:i+32]) + ",")
    L.append("};")
    return "\n".join(L)

def emit_uint16_array(name, vals):
    L = [f"const uint16_t {name}[{len(vals)}] = {{"]
    for i in range(0, len(vals), 16):
        L.append("    " + ", ".join(f"{v:>5d}" for v in vals[i:i+16]) + ",")
    L.append("};")
    return "\n".join(L)

def emit_uint32_array(name, vals):
    L = [f"const uint32_t {name}[{len(vals)}] = {{"]
    for i in range(0, len(vals), 12):
        L.append("    " + ", ".join(f"{v:>10d}" for v in vals[i:i+12]) + ",")
    L.append("};")
    return "\n".join(L)

def emit_int32_array(name, vals):
    L = [f"const int32_t {name}[{len(vals)}] = {{"]
    for i in range(0, len(vals), 12):
        L.append("    " + ", ".join(f"{v:>10d}" for v in vals[i:i+12]) + ",")
    L.append("};")
    return "\n".join(L)

def emit_uint64_array(name, vals):
    L = [f"const uint64_t {name}[{len(vals)}] = {{"]
    for i in range(0, len(vals), 8):
        L.append("    " + ", ".join(f"0x{v:016x}ULL" for v in vals[i:i+8]) + ",")
    L.append("};")
    return "\n".join(L)

def emit_double_array(name, vals):
    L = [f"const double {name}[{len(vals)}] = {{"]
    for i in range(0, len(vals), 4):
        L.append("    " + ", ".join(f"{v:.17g}" for v in vals[i:i+4]) + ",")
    L.append("};")
    return "\n".join(L)

def emit_byte_blob(name, blob, items, item_bytes):
    L = [f"const uint8_t {name}[{items} * {item_bytes}] = {{"]
    for i in range(0, len(blob), 32):
        L.append("    " + ", ".join(f"0x{b:02x}" for b in blob[i:i+32]) + ",")
    L.append("};")
    return "\n".join(L)


# ── Property enums (must match the C #defines emitted in pg_unicode_props.h) ─
GCB = {"Other":0, "CR":1, "LF":2, "Control":3, "Extend":4, "ZWJ":5,
       "Regional_Indicator":6, "Prepend":7, "SpacingMark":8,
       "L":9, "V":10, "T":11, "LV":12, "LVT":13}
WB  = {"Other":0, "CR":1, "LF":2, "Newline":3, "Extend":4, "ZWJ":5,
       "Format":6, "Katakana":7, "Hebrew_Letter":8, "ALetter":9,
       "Single_Quote":10, "Double_Quote":11, "MidNumLet":12, "MidLetter":13,
       "MidNum":14, "Numeric":15, "ExtendNumLet":16, "Regional_Indicator":17,
       "WSegSpace":18, "Extended_Pictographic":19}
SB  = {"Other":0, "CR":1, "LF":2, "Sep":3, "Format":4, "Sp":5, "Lower":6,
       "Upper":7, "OLetter":8, "Numeric":9, "ATerm":10, "STerm":11,
       "Close":12, "SContinue":13, "Extend":14}
LB = {"XX":0, "BK":1, "CR":2, "LF":3, "CM":4, "NL":5, "SG":6, "WJ":7, "ZW":8,
      "GL":9, "SP":10, "B2":11, "BA":12, "BB":13, "HY":14, "CB":15, "CL":16,
      "CP":17, "EX":18, "IN":19, "NS":20, "OP":21, "QU":22, "IS":23, "NU":24,
      "PO":25, "PR":26, "SY":27, "AI":28, "AL":29, "CJ":30, "EB":31, "EM":32,
      "H2":33, "H3":34, "HL":35, "ID":36, "JL":37, "JV":38, "JT":39, "RI":40,
      "SA":41, "ZWJ":42, "AK":43, "AP":44, "AS":45, "VF":46, "VI":47}
GC = {"Cn":0, "Lu":1, "Ll":2, "Lt":3, "Lm":4, "Lo":5, "Mn":6, "Mc":7, "Me":8,
      "Nd":9, "Nl":10, "No":11, "Pc":12, "Pd":13, "Ps":14, "Pe":15, "Pi":16,
      "Pf":17, "Po":18, "Sm":19, "Sc":20, "Sk":21, "So":22, "Zs":23, "Zl":24,
      "Zp":25, "Cc":26, "Cf":27, "Cs":28, "Co":29}
GC_DESCRIPTIONS = {
    "Lu":"Uppercase_Letter", "Ll":"Lowercase_Letter", "Lt":"Titlecase_Letter",
    "Lm":"Modifier_Letter", "Lo":"Other_Letter", "Mn":"Nonspacing_Mark",
    "Mc":"Spacing_Mark", "Me":"Enclosing_Mark", "Nd":"Decimal_Number",
    "Nl":"Letter_Number", "No":"Other_Number", "Pc":"Connector_Punctuation",
    "Pd":"Dash_Punctuation", "Ps":"Open_Punctuation", "Pe":"Close_Punctuation",
    "Pi":"Initial_Punctuation", "Pf":"Final_Punctuation", "Po":"Other_Punctuation",
    "Sm":"Math_Symbol", "Sc":"Currency_Symbol", "Sk":"Modifier_Symbol",
    "So":"Other_Symbol", "Zs":"Space_Separator", "Zl":"Line_Separator",
    "Zp":"Paragraph_Separator", "Cc":"Control", "Cf":"Format",
    "Cs":"Surrogate", "Co":"Private_Use", "Cn":"Unassigned"
}
GC_GROUPS = {
    "Lu":"L", "Ll":"L", "Lt":"L", "Lm":"L", "Lo":"L",
    "Mn":"M", "Mc":"M", "Me":"M",
    "Nd":"N", "Nl":"N", "No":"N",
    "Pc":"P", "Pd":"P", "Ps":"P", "Pe":"P", "Pi":"P", "Pf":"P", "Po":"P",
    "Sm":"S", "Sc":"S", "Sk":"S", "So":"S",
    "Zs":"Z", "Zl":"Z", "Zp":"Z",
    "Cc":"C", "Cf":"C", "Cs":"C", "Co":"C", "Cn":"C"
}
INCB = {"None":0, "Linker":1, "Extend":2, "Consonant":3}
BIDI = {"L":0, "R":1, "AL":2, "EN":3, "ES":4, "ET":5, "AN":6, "CS":7, "NSM":8,
        "BN":9, "B":10, "S":11, "WS":12, "ON":13, "LRE":14, "LRO":15, "RLE":16,
        "RLO":17, "PDF":18, "LRI":19, "RLI":20, "FSI":21, "PDI":22}
EAW  = {"N":0, "Na":1, "A":2, "W":3, "F":4, "H":5}
HSY  = {"NA":0, "L":1, "V":2, "T":3, "LV":4, "LVT":5}
NUM_TYPE = {"None":0, "Decimal":1, "Digit":2, "Numeric":3}


def assign_ids(values, reserved_zero=None):
    out = {}
    next_id = 0
    if reserved_zero is not None:
        out[reserved_zero] = 0
        next_id = 1
    for v in values:
        if v not in out:
            out[v] = next_id
            next_id += 1
    return out


# ── Tier-1 ranges (modern-script coverage; ~75K codepoints) ───────────────
# Math-derived blobs (BLAKE3 hashes, S^3 centroids, Hilbert codes) are
# precomputed for ALL assigned codepoints (gc != Cn) — both tier-1 and
# tier-2 — and packed into the portable binary blob. The tier-1 range
# list is emitted separately so the extension can offer fast tier
# membership tests for SQL queries that want to scope to common scripts.
#
# Tier-1 covers Latin/Greek/Cyrillic/Arabic/Hebrew/Indic/Thai/Tibetan/
# Myanmar/Hangul Jamo+Syllables/Hiragana/Katakana/CJK Ext A+Unified/Yi/
# CJK Compat/Math+Currency+Punct+Arrows/Math Alphanumeric/common emoji.
# It does NOT include CJK Extensions B-G+H, ancient scripts, music
# notation, tags, etc. — those are tier-2 (still precomputed and mmap'd,
# but excluded from "common script" SRFs).
TIER1_RANGES = [
    (0x0000, 0x07FF),    # ASCII through Arabic Extended-B
    (0x0900, 0x0FFF),    # Devanagari through Tibetan
    (0x1000, 0x13FF),    # Myanmar / Georgian / Hangul Jamo / Ethiopic
    (0x1D00, 0x1FFF),    # IPA / Latin Extended Additional / Greek Extended
    (0x2000, 0x2BFF),    # General Punctuation / Math / Currency / Symbols
    (0x3000, 0x33FF),    # CJK Punct / Hiragana / Katakana / Bopomofo / Compat
    (0x3400, 0x9FFF),    # CJK Ext A + Unified Ideographs (modern usage)
    (0xA000, 0xD7AF),    # Yi + Hangul Syllables
    (0xF900, 0xFFEF),    # CJK Compat Forms / Arabic Forms / Halfwidth/Fullwidth
    (0x1D400, 0x1D7FF),  # Mathematical Alphanumeric Symbols
    (0x1F300, 0x1FAFF),  # Common emoji ranges
]


def cp_in_tier1(cp: int) -> bool:
    for lo, hi in TIER1_RANGES:
        if lo <= cp <= hi:
            return True
    return False


# ── Portable per-block binary layout ──────────────────────────────────────
# Math-derived atoms (BLAKE3 hashes, S^3 centroids, Hilbert codes) are
# split across ~400 small files — one per Unicode block from Blocks.txt
# plus synthetic "Reserved_NNNN_MMMM" gap blocks. Each file is a single
# contiguous codepoint range. This gives:
#
#   - Embedded selective ship: device speaking only Latin needs ~14 KB,
#     not 91 MB. Image-of-substrate-state shrinks to working set.
#   - Lazy mmap at file granularity: backend only opens block files for
#     ranges it queries. Cold backend = ~10 ms loading the index alone.
#   - Update granularity: a new Unicode version that adds one block
#     replaces one file, not the entire blob.
#
# Auxiliary files:
#   - hartonomous-ucd-17.0.0.idx          range table + tier-1 ranges + filenames
#   - hartonomous-ucd-17.0.0.reverse.bin  global sorted hash→cp reverse (40 MB)
#   - blocks/<startHex>-<name>.bin        per-block hash+centroid+hilbert
HUCD_VERSION_17_0_0 = 0x00170000
BLK_MAGIC = 0x4B4C4248  # 'HBLK' LE
IDX_MAGIC = 0x58444348  # 'HCDX' LE
REV_MAGIC = 0x56455248  # 'HREV' LE
HUCD_BLAKE3_LEN = 32

def _sanitize_block_name(name: str) -> str:
    return re.sub(r"[^A-Za-z0-9]", "_", name)


def _blake3_or_zero(payload: bytes) -> bytes:
    try:
        import blake3 as _b3
        return _b3.blake3(payload).digest()
    except ImportError:
        return b"\x00" * HUCD_BLAKE3_LEN


def _materialize_ranges(blocks):
    """Return [(range_start, range_end, name), ...] covering every cp in
    [0, UNICODE_MAX). Named ranges from Blocks.txt; gaps get synthesized
    as 'Reserved_<lo>_<hi>'. Loader binary-search on range_start."""
    ranges = []
    blocks_sorted = sorted(blocks, key=lambda b: b[0])
    cursor = 0
    for start, end, name in blocks_sorted:
        if cursor < start:
            ranges.append((cursor, start - 1, f"Reserved_{cursor:04X}_{start-1:04X}"))
        ranges.append((start, end, name))
        cursor = end + 1
    if cursor < UNICODE_MAX:
        ranges.append((cursor, UNICODE_MAX - 1,
                       f"Reserved_{cursor:04X}_{UNICODE_MAX-1:04X}"))
    return ranges


def write_block_file(path: Path, range_start: int, range_end: int,
                     hash_blob: bytes, centroid_blob: bytes, hilbert_blob: bytes) -> int:
    """One file per Unicode block. Layout:
        [magic 'HBLK' u32]
        [version u32]
        [range_start u32][range_end u32]
        [atom_count u32][reserved u32]
        [hash data: atom_count × 32]
        [centroid data: atom_count × 32]
        [hilbert data: atom_count × 8]
        [blake3 footer 32 B over header+data]
    """
    n = range_end - range_start + 1
    hash_slice     = hash_blob    [range_start*32 : (range_end+1)*32]
    centroid_slice = centroid_blob[range_start*32 : (range_end+1)*32]
    hilbert_slice  = hilbert_blob [range_start* 8 : (range_end+1)* 8]
    assert len(hash_slice)     == n * 32, (len(hash_slice), n)
    assert len(centroid_slice) == n * 32, (len(centroid_slice), n)
    assert len(hilbert_slice)  == n *  8, (len(hilbert_slice), n)
    body = bytearray()
    body.extend(struct.pack("<IIIIII",
                            BLK_MAGIC, HUCD_VERSION_17_0_0,
                            range_start, range_end, n, 0))
    body.extend(hash_slice)
    body.extend(centroid_slice)
    body.extend(hilbert_slice)
    body.extend(_blake3_or_zero(bytes(body)))
    path.write_bytes(body)
    return len(body)


def write_reverse_file(path: Path, reverse_blob: bytes) -> int:
    """Global hash→cp reverse table, sorted by hash. Layout:
        [magic 'HREV' u32]
        [version u32]
        [entry_count u32][reserved u32]
        [entries: entry_count × 36 B (32-byte hash, 4-byte cp), sorted]
        [blake3 footer 32 B]
    """
    n = len(reverse_blob) // 36
    body = bytearray()
    body.extend(struct.pack("<IIII", REV_MAGIC, HUCD_VERSION_17_0_0, n, 0))
    body.extend(reverse_blob)
    body.extend(_blake3_or_zero(bytes(body)))
    path.write_bytes(body)
    return len(body)


def write_index_file(path: Path, ranges, files, tier1_ranges) -> int:
    """Index over the per-block layout. Layout:
        [magic 'HCDX' u32]
        [version u32]
        [block_count u32][tier1_count u32]
        [reverse_filename_off u32][reserved u32]
        [blocks: block_count × 32 B {
            u32 range_start, u32 range_end, u32 atom_count,
            u32 file_path_offset_in_string_table,
            u64 file_size, u32 file_blake3_first4, u32 reserved
        }]
        [tier1_ranges: tier1_count × 8 B (u32 lo, u32 hi)]
        [string_table: NUL-terminated filenames + reverse filename]
        [blake3 footer 32 B]
    """
    string_table = bytearray()
    string_offsets = []
    for r, f in zip(ranges, files):
        rel = f[3]
        string_offsets.append(len(string_table))
        string_table.extend(rel.encode("utf-8"))
        string_table.append(0)
    rev_off = len(string_table)
    string_table.extend(b"hartonomous-ucd-17.0.0.reverse.bin\x00")

    header = bytearray()
    header.extend(struct.pack("<IIIIII",
                              IDX_MAGIC, HUCD_VERSION_17_0_0,
                              len(ranges), len(tier1_ranges),
                              rev_off, 0))
    blocks_section = bytearray()
    for (rs, re_, name), (range_start, range_end, n_cps, rel), so in zip(ranges, files, string_offsets):
        assert (rs, re_) == (range_start, range_end)
        # Cheap integrity check: first 4 bytes of file path's blake3
        b3_first4 = struct.unpack("<I", _blake3_or_zero(rel.encode("utf-8"))[:4])[0]
        blocks_section.extend(struct.pack("<IIIIQII",
                                          range_start, range_end, n_cps,
                                          so, n_cps * 72 + 24 + 32,  # rough header+body+footer; loader checks exact via fstat
                                          b3_first4, 0))
    tier1_section = bytearray()
    for lo, hi in tier1_ranges:
        tier1_section.extend(struct.pack("<II", lo, hi))

    body = bytearray()
    body.extend(header)
    body.extend(blocks_section)
    body.extend(tier1_section)
    body.extend(string_table)
    body.extend(_blake3_or_zero(bytes(body)))
    path.write_bytes(body)
    return len(body)


def write_per_block_layout(out_dir: Path,
                           hash_blob: bytes, centroid_blob: bytes, hilbert_blob: bytes,
                           reverse_blob: bytes, blocks):
    """Emit per-block files + index + global reverse. Returns total bytes
    written across all files."""
    blocks_dir = out_dir / "blocks"
    blocks_dir.mkdir(parents=True, exist_ok=True)
    ranges = _materialize_ranges(blocks)
    files = []
    total_block_bytes = 0
    print(f"[gen] writing {len(ranges)} block files...")
    for range_start, range_end, name in ranges:
        sanitized = _sanitize_block_name(name)
        filename = f"{range_start:05X}-{sanitized}.bin"
        rel_path = f"blocks/{filename}"
        full_path = blocks_dir / filename
        n_cps = range_end - range_start + 1
        size = write_block_file(full_path, range_start, range_end,
                                hash_blob, centroid_blob, hilbert_blob)
        files.append((range_start, range_end, n_cps, rel_path))
        total_block_bytes += size

    rev_path = out_dir / "hartonomous-ucd-17.0.0.reverse.bin"
    rev_size = write_reverse_file(rev_path, reverse_blob)

    idx_path = out_dir / "hartonomous-ucd-17.0.0.idx"
    idx_size = write_index_file(idx_path, ranges, files, TIER1_RANGES)

    print(f"[gen]   blocks: {len(ranges)} files, {total_block_bytes:,} bytes")
    print(f"[gen]   reverse: {rev_size:,} bytes")
    print(f"[gen]   index:   {idx_size:,} bytes")
    return total_block_bytes + rev_size + idx_size, ranges, files


# ── Modular per-family C source emitters ───────────────────────────────────
def emit_segmentation(out_dir, cp_gcb, cp_wb, cp_sb, cp_lb, cp_incb):
    h = ["/* GENERATED — UAX#29 segmentation properties. */\n",
         "#ifndef PG_UCD_SEGMENTATION_H\n#define PG_UCD_SEGMENTATION_H\n",
         "#include <stdint.h>\n#include \"pg_unicode_version.h\"\n\n"]
    for tbl, name in [(GCB,"GCB"),(WB,"WB"),(SB,"SB"),(LB,"LB"),(INCB,"INCB")]:
        for k, v in tbl.items():
            sym = re.sub(r"[^A-Za-z0-9_]", "_", k)
            h.append(f"#define UC_{name}_{sym}  {v}\n")
    h.append("\nextern const uint8_t uc_gcb [UNICODE_CODEPOINT_MAX];\n")
    h.append("extern const uint8_t uc_wb  [UNICODE_CODEPOINT_MAX];\n")
    h.append("extern const uint8_t uc_sb  [UNICODE_CODEPOINT_MAX];\n")
    h.append("extern const uint8_t uc_lb  [UNICODE_CODEPOINT_MAX];\n")
    h.append("extern const uint8_t uc_incb[UNICODE_CODEPOINT_MAX];\n")
    h.append("#endif\n")
    (out_dir / "pg_ucd_segmentation.h").write_text("".join(h), encoding="utf-8")
    c = ["/* GENERATED. */\n#include \"pg_ucd_segmentation.h\"\n\n"]
    c += [emit_uint8_array("uc_gcb",  cp_gcb),  "\n\n",
          emit_uint8_array("uc_wb",   cp_wb),   "\n\n",
          emit_uint8_array("uc_sb",   cp_sb),   "\n\n",
          emit_uint8_array("uc_lb",   cp_lb),   "\n\n",
          emit_uint8_array("uc_incb", cp_incb), "\n"]
    (out_dir / "pg_ucd_segmentation.c").write_text("".join(c), encoding="utf-8")


def emit_classification(out_dir, cp_gc, cp_ccc, cp_script, cp_block,
                        cp_bidi, cp_eaw, cp_hsy, cp_num_type):
    h = ["/* GENERATED — UCD classification properties. */\n",
         "#ifndef PG_UCD_CLASSIFICATION_H\n#define PG_UCD_CLASSIFICATION_H\n",
         "#include <stdint.h>\n#include \"pg_unicode_version.h\"\n\n"]
    for tbl, name in [(GC,"GC"),(BIDI,"BIDI"),(EAW,"EAW"),(HSY,"HSY"),(NUM_TYPE,"NUM_TYPE")]:
        for k, v in tbl.items():
            sym = re.sub(r"[^A-Za-z0-9_]", "_", k)
            h.append(f"#define UC_{name}_{sym}  {v}\n")
    h += ["\nextern const uint8_t  uc_gc       [UNICODE_CODEPOINT_MAX];\n",
          "extern const uint8_t  uc_ccc      [UNICODE_CODEPOINT_MAX];\n",
          "extern const uint16_t uc_script   [UNICODE_CODEPOINT_MAX];\n",
          "extern const uint16_t uc_block    [UNICODE_CODEPOINT_MAX];\n",
          "extern const uint8_t  uc_bidi     [UNICODE_CODEPOINT_MAX];\n",
          "extern const uint8_t  uc_eaw      [UNICODE_CODEPOINT_MAX];\n",
          "extern const uint8_t  uc_hsy      [UNICODE_CODEPOINT_MAX];\n",
          "extern const uint8_t  uc_num_type [UNICODE_CODEPOINT_MAX];\n",
          "#endif\n"]
    (out_dir / "pg_ucd_classification.h").write_text("".join(h), encoding="utf-8")
    c = ["/* GENERATED. */\n#include \"pg_ucd_classification.h\"\n\n"]
    c += [emit_uint8_array ("uc_gc",       cp_gc),       "\n\n",
          emit_uint8_array ("uc_ccc",      cp_ccc),      "\n\n",
          emit_uint16_array("uc_script",   cp_script),   "\n\n",
          emit_uint16_array("uc_block",    cp_block),    "\n\n",
          emit_uint8_array ("uc_bidi",     cp_bidi),     "\n\n",
          emit_uint8_array ("uc_eaw",      cp_eaw),      "\n\n",
          emit_uint8_array ("uc_hsy",      cp_hsy),      "\n\n",
          emit_uint8_array ("uc_num_type", cp_num_type), "\n"]
    (out_dir / "pg_ucd_classification.c").write_text("".join(c), encoding="utf-8")


def emit_casing(out_dir, cp_upper, cp_lower, cp_title, cp_simple_fold,
                cp_uca_index, total_uca):
    h = ["/* GENERATED — case maps + UCA-sorted index. */\n",
         "#ifndef PG_UCD_CASING_H\n#define PG_UCD_CASING_H\n",
         "#include <stdint.h>\n#include \"pg_unicode_version.h\"\n\n",
         "extern const int32_t uc_simple_uppercase [UNICODE_CODEPOINT_MAX];\n",
         "extern const int32_t uc_simple_lowercase [UNICODE_CODEPOINT_MAX];\n",
         "extern const int32_t uc_simple_titlecase [UNICODE_CODEPOINT_MAX];\n",
         "extern const int32_t uc_simple_case_fold [UNICODE_CODEPOINT_MAX];\n",
         "extern const int32_t uc_uca_index        [UNICODE_CODEPOINT_MAX];\n",
         f"#define UC_UCA_TOTAL  {total_uca}\n",
         "#endif\n"]
    (out_dir / "pg_ucd_casing.h").write_text("".join(h), encoding="utf-8")
    c = ["/* GENERATED. */\n#include \"pg_ucd_casing.h\"\n\n"]
    c += [emit_int32_array("uc_simple_uppercase", cp_upper),       "\n\n",
          emit_int32_array("uc_simple_lowercase", cp_lower),       "\n\n",
          emit_int32_array("uc_simple_titlecase", cp_title),       "\n\n",
          emit_int32_array("uc_simple_case_fold", cp_simple_fold), "\n\n",
          emit_int32_array("uc_uca_index",        cp_uca_index),   "\n"]
    (out_dir / "pg_ucd_casing.c").write_text("".join(c), encoding="utf-8")


def emit_pictographic(out_dir, cp_picto):
    """extended_pictographic packed as a bitmap (1 bit/cp = ~140 KB)."""
    bitmap = bytearray((UNICODE_MAX + 7) // 8)
    for cp, v in enumerate(cp_picto):
        if v:
            bitmap[cp >> 3] |= 1 << (cp & 7)
    h = ["/* GENERATED — Extended_Pictographic bitmap. */\n",
         "#ifndef PG_UCD_PICTOGRAPHIC_H\n#define PG_UCD_PICTOGRAPHIC_H\n",
         "#include <stdint.h>\n#include \"pg_unicode_version.h\"\n\n",
         f"#define UC_EXT_PICTOGRAPHIC_BITMAP_LEN  {len(bitmap)}\n",
         "extern const uint8_t uc_ext_pictographic_bitmap[UC_EXT_PICTOGRAPHIC_BITMAP_LEN];\n",
         "static inline int uc_extended_pictographic(int32_t cp) {\n",
         "    if (cp < 0 || cp >= UNICODE_CODEPOINT_MAX) return 0;\n",
         "    return (uc_ext_pictographic_bitmap[cp >> 3] >> (cp & 7)) & 1;\n",
         "}\n",
         "#endif\n"]
    (out_dir / "pg_ucd_pictographic.h").write_text("".join(h), encoding="utf-8")
    c = ["/* GENERATED. */\n#include \"pg_ucd_pictographic.h\"\n\n",
         emit_byte_blob("uc_ext_pictographic_bitmap", bytes(bitmap), len(bitmap), 1), "\n"]
    (out_dir / "pg_ucd_pictographic.c").write_text("".join(c), encoding="utf-8")


def emit_decomp(out_dir, cp_decomp_type, cp_decomp_off, cp_decomp_len, decomp_data, composition_pairs):
    h = ["/* GENERATED — UCD canonical/compat decomposition mappings. */\n",
         "#ifndef PG_UCD_DECOMP_H\n#define PG_UCD_DECOMP_H\n",
         "#include <stdint.h>\n#include \"pg_unicode_version.h\"\n\n"]
    DECOMP_TYPES = ["", "canonical", "compat", "circle", "final", "font", "fraction",
                    "initial", "isolated", "medial", "narrow", "noBreak", "small",
                    "square", "sub", "super", "vertical", "wide"]
    for i, t in enumerate(DECOMP_TYPES):
        sym = re.sub(r"[^A-Za-z0-9_]", "_", t) if t else "None"
        h.append(f"#define UC_DECOMP_TYPE_{sym}  {i}\n")
    h += ["\ntypedef struct { int32_t first; int32_t second; int32_t composite; } UcCompositionPair;\n",
          "\nextern const uint8_t  uc_decomp_type[UNICODE_CODEPOINT_MAX];\n",
          "extern const uint32_t uc_decomp_off [UNICODE_CODEPOINT_MAX];\n",
          "extern const uint16_t uc_decomp_len [UNICODE_CODEPOINT_MAX];\n",
          f"#define UC_DECOMP_DATA_LEN  {len(decomp_data)}\n",
          "extern const int32_t  uc_decomp_data[UC_DECOMP_DATA_LEN];\n",
          f"#define UC_COMPOSITION_PAIR_COUNT  {len(composition_pairs)}\n",
          "extern const UcCompositionPair uc_composition_pairs[UC_COMPOSITION_PAIR_COUNT];\n",
          "#endif\n"]
    (out_dir / "pg_ucd_decomp.h").write_text("".join(h), encoding="utf-8")

    c = ["/* GENERATED. */\n#include \"pg_ucd_decomp.h\"\n\n"]
    c += [emit_uint8_array ("uc_decomp_type", cp_decomp_type), "\n\n",
          emit_uint32_array("uc_decomp_off",  cp_decomp_off),  "\n\n",
          emit_uint16_array("uc_decomp_len",  cp_decomp_len),  "\n\n",
          emit_int32_array ("uc_decomp_data", decomp_data),    "\n\n"]
    c.append("const UcCompositionPair uc_composition_pairs[UC_COMPOSITION_PAIR_COUNT] = {\n")
    for first, second, composite in composition_pairs:
        c.append(f"    {{ {first}, {second}, {composite} }},\n")
    c.append("};\n")
    (out_dir / "pg_ucd_decomp.c").write_text("".join(c), encoding="utf-8")


def emit_fcf(out_dir, cp_fcf_off, cp_fcf_len, fcf_data):
    h = ["/* GENERATED — full case fold expansions. */\n",
         "#ifndef PG_UCD_FCF_H\n#define PG_UCD_FCF_H\n",
         "#include <stdint.h>\n#include \"pg_unicode_version.h\"\n\n",
         "extern const uint32_t uc_fcf_off[UNICODE_CODEPOINT_MAX];\n",
         "extern const uint16_t uc_fcf_len[UNICODE_CODEPOINT_MAX];\n",
         f"#define UC_FCF_DATA_LEN  {len(fcf_data)}\n",
         "extern const int32_t  uc_fcf_data[UC_FCF_DATA_LEN];\n",
         "#endif\n"]
    (out_dir / "pg_ucd_fcf.h").write_text("".join(h), encoding="utf-8")
    c = ["/* GENERATED. */\n#include \"pg_ucd_fcf.h\"\n\n"]
    c += [emit_uint32_array("uc_fcf_off",  cp_fcf_off), "\n\n",
          emit_uint16_array("uc_fcf_len",  cp_fcf_len), "\n\n",
          emit_int32_array ("uc_fcf_data", fcf_data),   "\n"]
    (out_dir / "pg_ucd_fcf.c").write_text("".join(c), encoding="utf-8")


def emit_uca(out_dir, cp_uca_off, cp_uca_len, uca_data):
    h = ["/* GENERATED — UCA collation weights. */\n",
         "#ifndef PG_UCD_UCA_H\n#define PG_UCD_UCA_H\n",
         "#include <stdint.h>\n#include \"pg_unicode_version.h\"\n\n",
         "extern const uint32_t uc_uca_off[UNICODE_CODEPOINT_MAX];\n",
         "extern const uint16_t uc_uca_len[UNICODE_CODEPOINT_MAX];\n",
         f"#define UC_UCA_DATA_TUPLES  {len(uca_data) // 3}\n",
         "extern const uint32_t uc_uca_data[UC_UCA_DATA_TUPLES * 3];\n",
         "#endif\n"]
    (out_dir / "pg_ucd_uca.h").write_text("".join(h), encoding="utf-8")
    c = ["/* GENERATED. */\n#include \"pg_ucd_uca.h\"\n\n"]
    c += [emit_uint32_array("uc_uca_off",  cp_uca_off), "\n\n",
          emit_uint16_array("uc_uca_len",  cp_uca_len), "\n\n",
          emit_uint32_array("uc_uca_data", uca_data),   "\n"]
    (out_dir / "pg_ucd_uca.c").write_text("".join(c), encoding="utf-8")


def emit_names(out_dir, cp_name_off, cp_name_len, name_blob):
    h = ["/* GENERATED — codepoint names. */\n",
         "#ifndef PG_UCD_NAMES_H\n#define PG_UCD_NAMES_H\n",
         "#include <stdint.h>\n#include \"pg_unicode_version.h\"\n\n",
         "extern const uint32_t uc_name_off[UNICODE_CODEPOINT_MAX];\n",
         "extern const uint16_t uc_name_len[UNICODE_CODEPOINT_MAX];\n",
         f"#define UC_NAME_BLOB_LEN  {len(name_blob)}\n",
         "extern const uint8_t  uc_name_blob[UC_NAME_BLOB_LEN];\n",
         "#endif\n"]
    (out_dir / "pg_ucd_names.h").write_text("".join(h), encoding="utf-8")
    c = ["/* GENERATED. */\n#include \"pg_ucd_names.h\"\n\n"]
    c += [emit_uint32_array("uc_name_off",  cp_name_off), "\n\n",
          emit_uint16_array("uc_name_len",  cp_name_len), "\n\n",
          emit_byte_blob("uc_name_blob", bytes(name_blob), len(name_blob), 1), "\n"]
    (out_dir / "pg_ucd_names.c").write_text("".join(c), encoding="utf-8")


def emit_inventory(out_dir, GC_local, script_ids, blocks, block_ids, break_props):
    inv_h = [
        "/* GENERATED — UCD inventory tables. */\n",
        "#ifndef PG_UCD_INVENTORY_H\n#define PG_UCD_INVENTORY_H\n",
        "#include <stdint.h>\n#include \"pg_unicode_version.h\"\n\n",
        "typedef struct { const char* code; const char* description; const char* group; } GCEntry;\n",
        "typedef struct { const char* code; } ScriptEntry;\n",
        "typedef struct { const char* code; int32_t range_start; int32_t range_end; } BlockEntry;\n",
        "typedef struct { const char* category; const char* code; uint8_t enum_id; } BreakPropEntry;\n\n",
        f"#define UC_GC_COUNT      {len(GC_local)}\n",
        f"#define UC_SCRIPT_COUNT  {len(script_ids)}\n",
        f"#define UC_BLOCK_COUNT   {len(block_ids)}\n",
        f"#define UC_BREAK_COUNT   {len(break_props)}\n\n",
        "extern const GCEntry        uc_inv_gc[UC_GC_COUNT];\n",
        "extern const ScriptEntry    uc_inv_scripts[UC_SCRIPT_COUNT];\n",
        "extern const BlockEntry     uc_inv_blocks[UC_BLOCK_COUNT];\n",
        "extern const BreakPropEntry uc_inv_break_props[UC_BREAK_COUNT];\n",
        "#endif\n"]
    (out_dir / "pg_ucd_inventory.h").write_text("".join(inv_h), encoding="utf-8")
    inv_c = ["/* GENERATED. */\n#include \"pg_ucd_inventory.h\"\n\n",
             "const GCEntry uc_inv_gc[UC_GC_COUNT] = {\n"]
    for code, eid in sorted(GC_local.items(), key=lambda kv: kv[1]):
        inv_c.append(f'    {{ "{code}", "{GC_DESCRIPTIONS[code]}", "{GC_GROUPS[code]}" }},\n')
    inv_c.append("};\n\nconst ScriptEntry uc_inv_scripts[UC_SCRIPT_COUNT] = {\n")
    for code, sid in sorted(script_ids.items(), key=lambda kv: kv[1]):
        inv_c.append(f'    {{ "{code}" }},\n')
    inv_c.append("};\n\nconst BlockEntry uc_inv_blocks[UC_BLOCK_COUNT] = {\n")
    inv_c.append('    { "No_Block", 0, 0 },\n')
    for start, end, name in blocks:
        c_name = name.replace("\\", "\\\\").replace("\"", "\\\"")
        inv_c.append(f'    {{ "{c_name}", {start}, {end} }},\n')
    inv_c.append("};\n\nconst BreakPropEntry uc_inv_break_props[UC_BREAK_COUNT] = {\n")
    for cat, code in break_props:
        if   cat == "GCB":  eid = GCB[code]
        elif cat == "WB":   eid = WB[code]
        elif cat == "SB":   eid = SB[code]
        elif cat == "LB":   eid = LB[code]
        else:               eid = INCB[code]
        inv_c.append(f'    {{ "{cat}", "{code}", {eid} }},\n')
    inv_c.append("};\n")
    (out_dir / "pg_ucd_inventory.c").write_text("".join(inv_c), encoding="utf-8")


def emit_tier1(out_dir):
    """Tier-1 range list — small, eligible for inline lookup."""
    h = ["/* GENERATED — tier-1 codepoint range list (~75K cps). */\n",
         "#ifndef PG_UCD_TIER1_H\n#define PG_UCD_TIER1_H\n",
         "#include <stdint.h>\n\n",
         f"#define UC_TIER1_RANGE_COUNT  {len(TIER1_RANGES)}\n",
         "typedef struct { int32_t lo, hi; } UcTier1Range;\n",
         "extern const UcTier1Range uc_tier1_ranges[UC_TIER1_RANGE_COUNT];\n",
         "/* O(log K) range membership test. K = UC_TIER1_RANGE_COUNT (small). */\n",
         "int uc_cp_in_tier1(int32_t cp);\n",
         "#endif\n"]
    (out_dir / "pg_ucd_tier1.h").write_text("".join(h), encoding="utf-8")
    rows = ", ".join(f"{{0x{lo:X},0x{hi:X}}}" for lo, hi in TIER1_RANGES)
    c = ["/* GENERATED. */\n#include \"pg_ucd_tier1.h\"\n\n",
         f"const UcTier1Range uc_tier1_ranges[UC_TIER1_RANGE_COUNT] = {{ {rows} }};\n\n",
         "int uc_cp_in_tier1(int32_t cp)\n{\n",
         "    int lo = 0, hi = UC_TIER1_RANGE_COUNT - 1;\n",
         "    while (lo <= hi) {\n",
         "        int mid = (lo + hi) >> 1;\n",
         "        if (cp <  uc_tier1_ranges[mid].lo) hi = mid - 1;\n",
         "        else if (cp > uc_tier1_ranges[mid].hi) lo = mid + 1;\n",
         "        else return 1;\n",
         "    }\n    return 0;\n}\n"]
    (out_dir / "pg_ucd_tier1.c").write_text("".join(c), encoding="utf-8")


def emit_atoms_loader_header(out_dir):
    """Header consumed by hand-written ext/hartonomous_pg/src/pg_ucd_atoms_blob.c.
    Declares the per-block lazy-mmap accessors + binary-search reverse fn.

    The math-derived per-cp data lives in ~400 small files under blocks/,
    one per Unicode block. Each backend lazy-mmaps blocks on first access
    via huc_cp_*_at(cp). The pointer-style symbols (uc_cp_hash etc.) are
    kept as NULL stubs for build compatibility — callers must use the
    accessor functions. """
    h = [
        "/* GENERATED — per-block math-derived atom layout.\n",
        " *\n",
        " * The blob is split across one file per Unicode block (Blocks.txt\n",
        " * range or synthesized 'Reserved_NNNN_MMMM' gap). A backend that\n",
        " * touches CJK loads ~1.5 MB; ASCII-only loads ~9 KB. mmap is lazy\n",
        " * at OS page level; backends that never query a block never\n",
        " * page in those bytes. */\n",
        "#ifndef PG_UCD_ATOMS_BLOB_H\n#define PG_UCD_ATOMS_BLOB_H\n",
        "#include <stdint.h>\n#include \"pg_unicode_version.h\"\n\n",
        "#define CP_HASH_LEN 32\n",
        "#define UC_CP_REVERSE_ENTRY_SIZE 36\n\n",
        "/* Loader: dir contains hartonomous-ucd-17.0.0.idx, .reverse.bin, blocks/. */\n",
        "int  huc_load_atoms_blob(const char* dir);\n",
        "void huc_unload_atoms_blob(void);\n\n",
        "/* O(log B) block lookup + O(1) within-block index = microsecond hot path.\n",
        " * Returns NULL when the relevant block file is unavailable (allowed\n",
        " * for embedded subset deployments). */\n",
        "const uint8_t* huc_cp_hash_at    (int32_t cp);\n",
        "const double*  huc_cp_centroid_at(int32_t cp);  /* 4 doubles */\n",
        "uint64_t       huc_cp_hilbert_at (int32_t cp);  /* 0 if unmapped */\n\n",
        "/* O(log N_total) reverse over the global sorted hash→cp table. */\n",
        "int32_t uc_cp_from_hash(const uint8_t* hash32);\n\n",
        "/* Compatibility stubs — kept NULL-initialized so existing code that\n",
        " * declares them as extern const pointers keeps linking. New code should\n",
        " * use the huc_cp_*_at() accessors. */\n",
        "extern const uint8_t*  uc_cp_hash;\n",
        "extern const double*   uc_cp_centroid;\n",
        "extern const uint64_t* uc_cp_hilbert;\n",
        "extern const uint8_t*  uc_cp_hash_to_value;\n",
        "extern uint32_t        uc_cp_reverse_count;\n",
        "#endif\n"]
    (out_dir / "pg_ucd_atoms_blob.h").write_text("".join(h), encoding="utf-8")


def emit_umbrella(out_dir):
    """Single header for downstream code — includes all modular pieces. """
    h = ["/* GENERATED — umbrella header for the modular UCD tables. */\n",
         "#ifndef PG_UCD_H\n#define PG_UCD_H\n",
         "#include \"pg_unicode_version.h\"\n",
         "#include \"pg_ucd_segmentation.h\"\n",
         "#include \"pg_ucd_classification.h\"\n",
         "#include \"pg_ucd_casing.h\"\n",
         "#include \"pg_ucd_pictographic.h\"\n",
         "#include \"pg_ucd_decomp.h\"\n",
         "#include \"pg_ucd_fcf.h\"\n",
         "#include \"pg_ucd_uca.h\"\n",
         "#include \"pg_ucd_names.h\"\n",
         "#include \"pg_ucd_inventory.h\"\n",
         "#include \"pg_ucd_tier1.h\"\n",
         "#include \"pg_ucd_atoms_blob.h\"\n",
         "#endif\n"]
    (out_dir / "pg_ucd.h").write_text("".join(h), encoding="utf-8")


# ── Main pipeline ──────────────────────────────────────────────────────────
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ucd-root", default="D:/Models/UCD/Public/UCD/latest")
    ap.add_argument("--out", default=str(GEN_DIR))
    ap.add_argument("--blob-out", default=None,
                    help="Path for the portable binary blob. Default: <out>/hartonomous-ucd-17.0.0.bin")
    ap.add_argument("--no-zstd", action="store_true",
                    help="Skip zstd compression of the blob")
    ap.add_argument("--decomp-only", action="store_true",
                    help="Regenerate only pg_ucd_decomp.{h,c}; skips unrelated UCD atom blobs")
    args = ap.parse_args()
    ucd_root = Path(args.ucd_root)
    out_dir  = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    blob_path = Path(args.blob_out) if args.blob_out else (out_dir / "hartonomous-ucd-17.0.0.bin")

    print(f"[gen] UCD root: {ucd_root}")
    print(f"[gen] output:   {out_dir}")

    if args.decomp_only:
        print("[gen] parsing UnicodeData.txt..."); udata = parse_unicode_data(ucd_root / "ucd" / "UnicodeData.txt")
        print("[gen] parsing DerivedNormalizationProps.txt..."); full_comp_exclusion = parse_codepoint_set_property(
            ucd_root / "ucd" / "DerivedNormalizationProps.txt", "Full_Composition_Exclusion")

        DECOMP_TYPES = ["", "canonical", "compat", "circle", "final", "font", "fraction",
                        "initial", "isolated", "medial", "narrow", "noBreak", "small",
                        "square", "sub", "super", "vertical", "wide"]
        decomp_type_id = {t: i for i, t in enumerate(DECOMP_TYPES)}
        cp_decomp_type = [0] * UNICODE_MAX
        decomp_data = []
        cp_decomp_off = [0] * UNICODE_MAX
        cp_decomp_len = [0] * UNICODE_MAX
        for cp in range(UNICODE_MAX):
            d_field = udata.get(cp, {}).get("decomp", "")
            if not d_field:
                continue
            typ, mapping = parse_decomposition_field(d_field)
            cp_decomp_type[cp] = decomp_type_id["canonical"] if typ is None else decomp_type_id.get(typ, 0)
            cp_decomp_off[cp] = len(decomp_data)
            cp_decomp_len[cp] = len(mapping)
            decomp_data.extend(mapping)

        composition_pairs = []
        for cp in range(UNICODE_MAX):
            if cp in full_comp_exclusion:
                continue
            if cp_decomp_type[cp] != decomp_type_id["canonical"] or cp_decomp_len[cp] != 2:
                continue
            off = cp_decomp_off[cp]
            composition_pairs.append((decomp_data[off], decomp_data[off + 1], cp))
        composition_pairs.sort()

        emit_decomp(out_dir, cp_decomp_type, cp_decomp_off, cp_decomp_len, decomp_data, composition_pairs)
        print(f"[gen] emitted pg_ucd_decomp with {len(composition_pairs):,} composition pairs")
        return

    print("[gen] parsing UnicodeData.txt..."); udata = parse_unicode_data(ucd_root / "ucd" / "UnicodeData.txt")
    print("[gen] parsing GraphemeBreakProperty.txt..."); gcb_map = parse_ranged_property(ucd_root / "ucd" / "auxiliary" / "GraphemeBreakProperty.txt")
    print("[gen] parsing WordBreakProperty.txt..."); wb_map = parse_ranged_property(ucd_root / "ucd" / "auxiliary" / "WordBreakProperty.txt")
    print("[gen] parsing SentenceBreakProperty.txt..."); sb_map = parse_ranged_property(ucd_root / "ucd" / "auxiliary" / "SentenceBreakProperty.txt")
    print("[gen] parsing LineBreak.txt..."); lb_map = parse_ranged_property(ucd_root / "ucd" / "LineBreak.txt")
    print("[gen] parsing emoji-data.txt...");
    ext_picto_map = parse_ranged_property(ucd_root / "ucd" / "emoji" / "emoji-data.txt", allowed={"Extended_Pictographic"})
    print("[gen] parsing DerivedCoreProperties.txt for InCB...")
    incb_map = {}
    with (ucd_root / "ucd" / "DerivedCoreProperties.txt").open("r", encoding="utf-8") as f:
        for raw in f:
            line = raw.split("#", 1)[0].strip()
            if not line: continue
            parts = [p.strip() for p in line.split(";")]
            if len(parts) < 3 or parts[1] != "InCB": continue
            rng = parts[0]; v = parts[2]
            if ".." in rng:
                lo, hi = rng.split("..")
                lo_i, hi_i = int(lo, 16), int(hi, 16)
            else:
                lo_i = hi_i = int(rng, 16)
            for cp in range(lo_i, hi_i + 1):
                incb_map[cp] = v
    print("[gen] parsing Scripts.txt..."); script_map = parse_ranged_property(ucd_root / "ucd" / "Scripts.txt")
    print("[gen] parsing Blocks.txt...");  blocks = parse_blocks(ucd_root / "ucd" / "Blocks.txt")
    print("[gen] parsing CaseFolding.txt..."); simple_fold, full_fold = parse_case_folding(ucd_root / "ucd" / "CaseFolding.txt")
    print("[gen] parsing UCA allkeys.txt..."); uca = parse_uca_allkeys(ucd_root / "uca" / "allkeys.txt")
    print("[gen] parsing EastAsianWidth.txt..."); eaw_map = parse_ranged_property(ucd_root / "ucd" / "EastAsianWidth.txt")
    print("[gen] parsing HangulSyllableType.txt..."); hsy_map = parse_ranged_property(ucd_root / "ucd" / "HangulSyllableType.txt")
    print("[gen] parsing DerivedNormalizationProps.txt..."); full_comp_exclusion = parse_codepoint_set_property(
        ucd_root / "ucd" / "DerivedNormalizationProps.txt", "Full_Composition_Exclusion")

    # Inventory IDs
    script_ids = assign_ids([script_map[cp] for cp in sorted(script_map.keys())], reserved_zero="Unknown")
    block_codes = ["No_Block"] + [name for _, _, name in blocks]
    block_ids = assign_ids(block_codes)
    break_props = []
    for code in sorted(GCB.keys(),  key=lambda k: GCB[k]):  break_props.append(("GCB", code))
    for code in sorted(WB.keys(),   key=lambda k: WB[k]):   break_props.append(("WB", code))
    for code in sorted(SB.keys(),   key=lambda k: SB[k]):   break_props.append(("SB", code))
    for code in sorted(LB.keys(),   key=lambda k: LB[k]):   break_props.append(("LB", code))
    for code in sorted(INCB.keys(), key=lambda k: INCB[k]): break_props.append(("InCB", code))

    print("[gen] building per-codepoint property arrays...")
    cp_gcb     = [GCB.get(gcb_map.get(cp, "Other"), 0) for cp in range(UNICODE_MAX)]
    cp_wb      = [WB.get(wb_map.get(cp, "Other"), 0) for cp in range(UNICODE_MAX)]
    cp_sb      = [SB.get(sb_map.get(cp, "Other"), 0) for cp in range(UNICODE_MAX)]
    cp_lb      = [LB.get(lb_map.get(cp, "XX"), 0) for cp in range(UNICODE_MAX)]
    cp_incb    = [INCB.get(incb_map.get(cp, "None"), 0) for cp in range(UNICODE_MAX)]
    cp_picto   = [1 if ext_picto_map.get(cp) == "Extended_Pictographic" else 0 for cp in range(UNICODE_MAX)]
    cp_gc      = [GC.get(udata.get(cp, {}).get("gc", "Cn"), 0) for cp in range(UNICODE_MAX)]
    cp_ccc     = [int(udata.get(cp, {}).get("ccc", 0)) for cp in range(UNICODE_MAX)]
    cp_script  = [script_ids.get(script_map.get(cp, "Unknown"), 0) for cp in range(UNICODE_MAX)]
    cp_block   = [0] * UNICODE_MAX
    for start, end, name in blocks:
        bid = block_ids.get(name, 0)
        for cp in range(start, end + 1):
            cp_block[cp] = bid
    cp_upper   = [udata.get(cp, {}).get("upper", 0) for cp in range(UNICODE_MAX)]
    cp_lower   = [udata.get(cp, {}).get("lower", 0) for cp in range(UNICODE_MAX)]
    cp_title   = [udata.get(cp, {}).get("title", 0) for cp in range(UNICODE_MAX)]
    cp_simple_fold = [simple_fold.get(cp, 0) for cp in range(UNICODE_MAX)]
    cp_bidi    = [BIDI.get(udata.get(cp, {}).get("bidi", "L"), 0) for cp in range(UNICODE_MAX)]
    cp_eaw     = [EAW.get(eaw_map.get(cp, "N"), 0) for cp in range(UNICODE_MAX)]
    cp_hsy     = [HSY.get(hsy_map.get(cp, "NA"), 0) for cp in range(UNICODE_MAX)]
    cp_num_type = []
    for cp in range(UNICODE_MAX):
        e = udata.get(cp, {})
        if e.get("numeric_type_decimal", ""):
            cp_num_type.append(NUM_TYPE["Decimal"])
        elif e.get("numeric_type_digit", ""):
            cp_num_type.append(NUM_TYPE["Digit"])
        elif e.get("numeric_type_value", ""):
            cp_num_type.append(NUM_TYPE["Numeric"])
        else:
            cp_num_type.append(NUM_TYPE["None"])

    # UCA-sorted index
    print("[gen] computing UCA-sorted index...")
    weighted = []
    for cp in range(UNICODE_MAX):
        w = uca.get(cp)
        primary = tuple((p, s, t) for p, s, t in w) if w else ((0xFFFF, 0xFFFF, 0xFFFF),)
        weighted.append((primary, cp))
    weighted.sort()
    uca_index = [0] * UNICODE_MAX
    for idx, (_, cp) in enumerate(weighted):
        uca_index[cp] = idx
    total_uca = len(weighted)
    del weighted

    cp_uca_off = [0] * UNICODE_MAX
    cp_uca_len = [0] * UNICODE_MAX
    uca_data = []
    for cp, w in uca.items():
        cp_uca_off[cp] = len(uca_data) // 3
        cp_uca_len[cp] = len(w)
        for p, s, t in w:
            uca_data.extend([p, s, t])
    del uca

    # ── Math-derived atoms — full 1.1M plane ───────────────────────────────
    # Compute hash + centroid + Hilbert for EVERY codepoint (not just
    # assigned). Cn slots become substrate-internal scaffolding (universal
    # 32-bit-int → atom registry, geometric Voronoi density, Mendeleev
    # prediction slots, sentinel atoms). Per-block file split + lazy mmap
    # at file granularity means cost is per-touched-block, not per-1.1M.
    print(f"[gen] computing {UNICODE_MAX:,} S^3 centroids...")
    centroid_blob = bytearray(UNICODE_MAX * 32)
    for cp in range(UNICODE_MAX):
        x, y, z, m = super_fibonacci_4d(uca_index[cp], total_uca)
        struct.pack_into("<dddd", centroid_blob, cp * 32, x, y, z, m)

    print(f"[gen] computing {UNICODE_MAX:,} Hilbert indices...")
    hilbert_blob = bytearray(UNICODE_MAX * 8)
    for cp in range(UNICODE_MAX):
        x = struct.unpack_from("<d", centroid_blob, cp * 32 + 0)[0]
        y = struct.unpack_from("<d", centroid_blob, cp * 32 + 8)[0]
        z = struct.unpack_from("<d", centroid_blob, cp * 32 + 16)[0]
        m = struct.unpack_from("<d", centroid_blob, cp * 32 + 24)[0]
        h = hilbert_4d_encode((x, y, z, m))
        struct.pack_into("<Q", hilbert_blob, cp * 8, h)

    print(f"[gen] computing {UNICODE_MAX:,} BLAKE3 hashes...")
    hash_blob = bytearray(UNICODE_MAX * HASH_LEN)
    for cp in range(UNICODE_MAX):
        if cp % 100000 == 0 and cp > 0:
            print(f"       {cp:,}/{UNICODE_MAX:,}")
        hash_blob[cp*HASH_LEN:(cp+1)*HASH_LEN] = blake3_4byte(cp)

    print(f"[gen] building reverse table ({UNICODE_MAX:,} entries, sorted by hash)...")
    pairs = [(bytes(hash_blob[cp*HASH_LEN:(cp+1)*HASH_LEN]), cp)
             for cp in range(UNICODE_MAX)]
    pairs.sort(key=lambda kv: kv[0])
    reverse_blob = bytearray(UNICODE_MAX * (HASH_LEN + 4))
    for i, (h, cp) in enumerate(pairs):
        off = i * (HASH_LEN + 4)
        reverse_blob[off:off+HASH_LEN] = h
        struct.pack_into("<I", reverse_blob, off + HASH_LEN, cp)
    n_assigned = sum(1 for cp in range(UNICODE_MAX) if cp_gc[cp] != GC["Cn"])
    print(f"[gen]   ({n_assigned:,} assigned + {UNICODE_MAX - n_assigned:,} reserved/Cn — all included)")

    # Variable-length data
    print("[gen] packing variable-length data (decomp / fcf / uca / names)...")
    DECOMP_TYPES = ["", "canonical", "compat", "circle", "final", "font", "fraction",
                    "initial", "isolated", "medial", "narrow", "noBreak", "small",
                    "square", "sub", "super", "vertical", "wide"]
    decomp_type_id = {t: i for i, t in enumerate(DECOMP_TYPES)}
    cp_decomp_type = [0] * UNICODE_MAX
    decomp_data = []
    cp_decomp_off = [0] * UNICODE_MAX
    cp_decomp_len = [0] * UNICODE_MAX
    for cp in range(UNICODE_MAX):
        d_field = udata.get(cp, {}).get("decomp", "")
        if not d_field: continue
        typ, mapping = parse_decomposition_field(d_field)
        cp_decomp_type[cp] = decomp_type_id["canonical"] if typ is None else decomp_type_id.get(typ, 0)
        cp_decomp_off[cp] = len(decomp_data)
        cp_decomp_len[cp] = len(mapping)
        decomp_data.extend(mapping)

    composition_pairs = []
    for cp in range(UNICODE_MAX):
        if cp in full_comp_exclusion:
            continue
        if cp_decomp_type[cp] != decomp_type_id["canonical"] or cp_decomp_len[cp] != 2:
            continue
        off = cp_decomp_off[cp]
        composition_pairs.append((decomp_data[off], decomp_data[off + 1], cp))
    composition_pairs.sort()

    cp_fcf_off = [0] * UNICODE_MAX
    cp_fcf_len = [0] * UNICODE_MAX
    fcf_data = []
    for cp, m in full_fold.items():
        cp_fcf_off[cp] = len(fcf_data)
        cp_fcf_len[cp] = len(m)
        fcf_data.extend(m)

    cp_name_off = [0] * UNICODE_MAX
    cp_name_len = [0] * UNICODE_MAX
    name_blob = bytearray()
    for cp in range(UNICODE_MAX):
        nm = udata.get(cp, {}).get("name", "")
        if not nm or nm.startswith("<"): continue
        b = nm.encode("ascii", errors="replace")
        cp_name_off[cp] = len(name_blob)
        cp_name_len[cp] = len(b)
        name_blob.extend(b)

    # ── Emit version header ──────────────────────────────────────────────
    (out_dir / "pg_unicode_version.h").write_text(
        "/* GENERATED — do not edit. UCD/UCA version pinned at extension build time. */\n"
        "#ifndef PG_UNICODE_VERSION_H\n#define PG_UNICODE_VERSION_H\n"
        "#define UCD_VERSION_STRING \"17.0.0\"\n"
        "#define UCA_VERSION_STRING \"17.0.0\"\n"
        "#define UNICODE_CODEPOINT_MAX 0x110000\n"
        "#endif\n",
        encoding="utf-8")

    # ── Emit modular per-family C source ────────────────────────────────
    print("[gen] emitting modular property source...")
    emit_segmentation(out_dir, cp_gcb, cp_wb, cp_sb, cp_lb, cp_incb)
    emit_classification(out_dir, cp_gc, cp_ccc, cp_script, cp_block,
                        cp_bidi, cp_eaw, cp_hsy, cp_num_type)
    emit_casing(out_dir, cp_upper, cp_lower, cp_title, cp_simple_fold,
                uca_index, total_uca)
    emit_pictographic(out_dir, cp_picto)
    emit_decomp(out_dir, cp_decomp_type, cp_decomp_off, cp_decomp_len, decomp_data, composition_pairs)
    emit_fcf(out_dir, cp_fcf_off, cp_fcf_len, fcf_data)
    emit_uca(out_dir, cp_uca_off, cp_uca_len, uca_data)
    emit_names(out_dir, cp_name_off, cp_name_len, name_blob)
    emit_inventory(out_dir, GC, script_ids, blocks, block_ids, break_props)
    emit_tier1(out_dir)
    emit_atoms_loader_header(out_dir)
    emit_umbrella(out_dir)

    # ── Emit per-block math layout + index + global reverse ─────────────
    total_size, ranges, files = write_per_block_layout(
        out_dir, bytes(hash_blob), bytes(centroid_blob),
        bytes(hilbert_blob), bytes(reverse_blob), blocks)

    # ── Optional zstd-compressed bundle for embedded distribution ───────
    if not args.no_zstd:
        try:
            import zstandard as zstd
            import io, tarfile
            buf = io.BytesIO()
            with tarfile.open(fileobj=buf, mode="w") as tf:
                tf.add(out_dir / "hartonomous-ucd-17.0.0.idx",
                       arcname="hartonomous-ucd-17.0.0.idx")
                tf.add(out_dir / "hartonomous-ucd-17.0.0.reverse.bin",
                       arcname="hartonomous-ucd-17.0.0.reverse.bin")
                for rs, re_, n_cps, rel in files:
                    tf.add(out_dir / rel, arcname=rel)
            tar_bytes = buf.getvalue()
            compressed = zstd.ZstdCompressor(level=19).compress(tar_bytes)
            bundle_path = out_dir / "hartonomous-ucd-17.0.0.bundle.tar.zst"
            bundle_path.write_bytes(compressed)
            print(f"[gen]   bundle: {bundle_path.name}  "
                  f"({len(compressed):,} bytes, "
                  f"{len(compressed) * 100 // len(tar_bytes)}% of raw {len(tar_bytes):,})")
        except ImportError:
            print("[gen]   (skipping zstd — install: pip install zstandard)")


    print("[gen] DONE")
    for p in sorted(out_dir.glob("*")):
        print(f"  {p.name:40s}  {p.stat().st_size:>14,d} bytes")


if __name__ == "__main__":
    main()
