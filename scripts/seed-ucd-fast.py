#!/usr/bin/env python3
"""
Direct-source UCD substrate seeder. Reads ucd.all.flat.xml + allkeys.txt
directly (NOT the perf-cache blob), computes BLAKE3 + Super-Fibonacci S³
inline matching the C# / native conventions, and bulk COPYs into
substrate via psql stdin — bypassing the streaming pipeline, the C
extension blob, and the C# producer entirely.

Populates:
- substrate.entity                     (codepoint hash, ON CONFLICT DO NOTHING)
- substrate.entity_classification      (codepoint × unicode_consortium)
- substrate.physicality                (codepoint POINTZM by UCA-rank Super-Fibonacci S³)
- substrate.cp_general_category, cp_script, cp_block, cp_bidi_class,
  cp_east_asian_width, cp_grapheme_break, cp_word_break, cp_sentence_break,
  cp_line_break                        (9 property junctions)

Run:
    time python3 /tmp/seed_ucd_atoms_from_xml.py
"""

import math
import os
import subprocess
import sys
import time
import zipfile
import xml.etree.ElementTree as ET
import blake3

UCD_XML_ZIP = "/vault/Data/Unicode/Public/UCD/latest/ucdxml/ucd.all.flat.zip"
UCD_XML_INNER = "ucd.all.flat.xml"
UCA_ALLKEYS = "/vault/Data/Unicode/Public/UCA/latest/allkeys.txt"
NS = "{http://www.unicode.org/ns/2003/ucd/1.0}"
DB = os.environ.get("HARTONOMOUS_DB_NAME", "hartonomous")

PHI = 1.6180339887498949
PSI = 1.4142135623730951


def psql_query(sql):
    r = subprocess.run(
        ["psql", "-d", DB, "-At", "-F", "\t", "-c", sql],
        capture_output=True, text=True, check=True,
    )
    return [tuple(line.split("\t")) for line in r.stdout.strip().splitlines() if line]


def psql_exec(sql):
    subprocess.run(["psql", "-d", DB, "-v", "ON_ERROR_STOP=1", "-c", sql],
                   capture_output=True, text=True, check=True)


def psql_copy(table, columns, rows_iter, fmt="text"):
    cmd = ["psql", "-d", DB, "-v", "ON_ERROR_STOP=1",
           "-c", f"COPY {table} ({','.join(columns)}) FROM STDIN (FORMAT {fmt})"]
    proc = subprocess.Popen(cmd, stdin=subprocess.PIPE,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            text=True)
    n = 0
    for row in rows_iter:
        proc.stdin.write("\t".join(row) + "\n")
        n += 1
    proc.stdin.close()
    proc.wait()
    if proc.returncode != 0:
        err = proc.stderr.read()
        raise RuntimeError(f"COPY {table} failed rc={proc.returncode}: {err}")
    return n


def cp_hash(cp):
    """BLAKE3 of 4-byte big-endian codepoint integer (matches
    Blake3.HashCodepoint in src/Hartonomous.Core/Compute/Common/Blake3.cs:178)."""
    return blake3.blake3(cp.to_bytes(4, "big")).digest()


def hex_bytea(b):
    return "\\\\x" + b.hex()


def super_fib_s3(i, n):
    """Super-Fibonacci S³ projection. Matches
    ext/libhartonomous/src/super_fibonacci.c (i + 0.5, total).
    Unit-length 4-tuple on the 3-sphere."""
    s = i + 0.5
    t = s / n
    d = 2.0 * math.pi * s
    r = math.sqrt(t)
    R = math.sqrt(1.0 - t)
    alpha = d / PHI
    beta = d / PSI
    return (r * math.sin(alpha), r * math.cos(alpha),
            R * math.sin(beta),  R * math.cos(beta))


def parse_uca_ranks(path):
    """Parse allkeys.txt for codepoint UCA rank.
    Returns dict {cp: rank} for single-codepoint entries (ranked by
    line-order in the file)."""
    ranks = {}
    rank = 0
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#") or line.startswith("@"):
                continue
            # "0061  ; [.weight] # comment" — single cp
            key, _, _ = line.partition(";")
            key = key.strip()
            if " " in key:
                # Multi-cp contraction (e.g. "0041 030A") — skip; ranks
                # are per-atom codepoint here.
                continue
            try:
                cp = int(key, 16)
            except ValueError:
                continue
            if cp not in ranks:
                ranks[cp] = rank
                rank += 1
    return ranks, rank


def main():
    if not os.path.exists(UCD_XML_ZIP):
        sys.exit(f"missing {UCD_XML_ZIP}")
    if not os.path.exists(UCA_ALLKEYS):
        sys.exit(f"missing {UCA_ALLKEYS}")

    t0 = time.monotonic()

    print(f"[setup] loading reference vocab id maps from {DB}")
    gc_map  = {c: int(i) for i, c in psql_query("SELECT id, code FROM substrate.general_category")}
    sc_map  = {c: int(i) for i, c in psql_query("SELECT id, code FROM substrate.script")}
    bc_map  = {c: int(i) for i, c in psql_query("SELECT id, code FROM substrate.bidi_class")}
    eaw_map = {c: int(i) for i, c in psql_query("SELECT id, code FROM substrate.east_asian_width")}
    # Block: substrate uses space-form ("Basic Latin"), XML uses underscore-form ("Basic_Latin").
    blk_rows = psql_query("SELECT id, code FROM substrate.block")
    blk_map = {}
    for i, c in blk_rows:
        blk_map[c] = int(i)
        blk_map[c.replace(" ", "_")] = int(i)
    bp_map = {}
    for i, cat, c in psql_query("SELECT id, category, code FROM substrate.break_property"):
        bp_map[(cat, c)] = int(i)

    codepoint_type_id = int(psql_query(
        "SELECT id FROM substrate.entity_type WHERE code='codepoint'")[0][0])
    uc_prov_id = int(psql_query(
        "SELECT id FROM substrate.provenance WHERE code='unicode_consortium'")[0][0])
    physicality_entity_type_id = int(psql_query(
        "SELECT id FROM substrate.physicality_type WHERE code='entity'")[0][0])

    print(f"[setup] vocab maps + reference ids loaded in {time.monotonic()-t0:.2f}s")

    t1 = time.monotonic()
    print(f"[uca]   parsing {UCA_ALLKEYS} for UCA rank per codepoint")
    uca_ranks, uca_total = parse_uca_ranks(UCA_ALLKEYS)
    print(f"[uca]   {len(uca_ranks):,} ranked codepoints (uca_total={uca_total}) in {time.monotonic()-t1:.2f}s")

    print(f"[parse] streaming {UCD_XML_ZIP}")
    t2 = time.monotonic()

    entity_rows         = []
    classification_rows = []
    physicality_rows    = []  # (entity_hash_hex, x, y, z, m)
    cp_gc, cp_sc, cp_blk, cp_bc, cp_eaw = [], [], [], [], []
    cp_gcb, cp_wb, cp_sb, cp_lb = [], [], [], []

    n_chars = 0
    n_phys = 0
    n_unranked = 0
    seen_cps = set()
    with zipfile.ZipFile(UCD_XML_ZIP) as z, z.open(UCD_XML_INNER) as xml_stream:
        for _, elem in ET.iterparse(xml_stream, events=("end",)):
            if elem.tag != NS + "char":
                continue
            cp_str = elem.get("cp")
            if cp_str is None:
                first = elem.get("first-cp")
                last = elem.get("last-cp")
                if first is None or last is None:
                    elem.clear()
                    continue
                cp_iter = range(int(first, 16), int(last, 16) + 1)
            else:
                cp_iter = (int(cp_str, 16),)
            gc = elem.get("gc"); sc = elem.get("sc"); blk = elem.get("blk")
            bc = elem.get("bc"); eaw = elem.get("ea")
            gcb = elem.get("GCB"); wb = elem.get("WB")
            sb = elem.get("SB"); lb = elem.get("lb")
            elem.clear()
            for cp in cp_iter:
                seen_cps.add(cp)
                h = cp_hash(cp)
                hh = hex_bytea(h)
                entity_rows.append((hh,))
                classification_rows.append((hh, str(codepoint_type_id), str(uc_prov_id)))
                rank = uca_ranks.get(cp)
                if rank is not None:
                    x, y, zc, m = super_fib_s3(rank, uca_total)
                    physicality_rows.append((hh, f"{x:.17g}", f"{y:.17g}", f"{zc:.17g}", f"{m:.17g}"))
                    n_phys += 1
                else:
                    n_unranked += 1
                if gc and gc in gc_map:  cp_gc.append((hh, str(gc_map[gc])))
                if sc and sc in sc_map:  cp_sc.append((hh, str(sc_map[sc])))
                if blk and blk in blk_map: cp_blk.append((hh, str(blk_map[blk])))
                if bc and bc in bc_map:  cp_bc.append((hh, str(bc_map[bc])))
                if eaw and eaw in eaw_map: cp_eaw.append((hh, str(eaw_map[eaw])))
                if gcb and ("GCB", gcb) in bp_map: cp_gcb.append((hh, str(bp_map[("GCB", gcb)])))
                if wb and ("WB", wb) in bp_map:   cp_wb.append((hh, str(bp_map[("WB", wb)])))
                if sb and ("SB", sb) in bp_map:   cp_sb.append((hh, str(bp_map[("SB", sb)])))
                if lb and ("LB", lb) in bp_map:   cp_lb.append((hh, str(bp_map[("LB", lb)])))
                n_chars += 1
    t_parse = time.monotonic() - t2
    print(f"[parse] {n_chars:,} XML-attested codepoints | {n_phys:,} physicalities | "
          f"{n_unranked:,} unranked-cp (no physicality) in {t_parse:.2f}s")

    # Backfill the full Unicode codepoint range (0..0x10FFFF). The XML
    # carries only assigned characters + range descriptors; unassigned /
    # reserved / surrogate codepoints don't have property data but their
    # entity rows + content-addressed classifications still need to exist
    # so the substrate can refer to them by hash from text trajectories.
    # No properties → no physicality (no UCA rank, no break props).
    t_backfill = time.monotonic()
    n_missing = 0
    for cp in range(0x110000):
        if cp in seen_cps:
            continue
        h = cp_hash(cp)
        hh = hex_bytea(h)
        entity_rows.append((hh,))
        classification_rows.append((hh, str(codepoint_type_id), str(uc_prov_id)))
        n_missing += 1
    print(f"[range-fill] {n_missing:,} unattested codepoints (entity + classification only) "
          f"in {time.monotonic()-t_backfill:.2f}s; "
          f"total = {len(entity_rows):,} entities, {len(physicality_rows):,} physicalities")

    # --- DB writes ---
    print("[write] entity / classification via temp staging + ON CONFLICT DO NOTHING")
    t3 = time.monotonic()

    psql_exec("CREATE TEMP TABLE _ent_in (h bytea) ON COMMIT DROP;"  # ignored (separate sessions)
              ) if False else None

    # Use a single psql session with multiple ops via heredoc for transactional clarity
    setup_sql = """
    CREATE TEMP TABLE _ent_in   (h bytea);
    CREATE TEMP TABLE _cls_in   (h bytea, t int, p int);
    CREATE TEMP TABLE _phys_in  (h bytea, x double precision, y double precision,
                                 z double precision, m double precision);
    CREATE TEMP TABLE _cp_gc    (h bytea, v int);
    CREATE TEMP TABLE _cp_sc    (h bytea, v int);
    CREATE TEMP TABLE _cp_blk   (h bytea, v int);
    CREATE TEMP TABLE _cp_bc    (h bytea, v int);
    CREATE TEMP TABLE _cp_eaw   (h bytea, v int);
    CREATE TEMP TABLE _cp_gcb   (h bytea, v int);
    CREATE TEMP TABLE _cp_wb    (h bytea, v int);
    CREATE TEMP TABLE _cp_sb    (h bytea, v int);
    CREATE TEMP TABLE _cp_lb    (h bytea, v int);
    """
    # Build a single SQL pipeline that creates temp tables, COPYs in via
    # \copy, then INSERT-SELECTs into substrate.* with ON CONFLICT DO NOTHING.
    # Using bash heredoc into psql -- substantially fewer round trips and
    # single transaction for atomicity.

    pipeline = []
    pipeline.append("BEGIN;")
    pipeline.append(setup_sql)

    # Marker that switches the python driver from sending pipeline SQL into
    # COPY-STDIN for the upcoming COPY commands. We do this by issuing each
    # COPY individually via separate psql_copy calls AFTER opening the
    # transaction in a single persistent connection — but the persistent-
    # connection abstraction here is shell + psql so each call is its own
    # connection. Workaround: COPY into TEMP via a single psql -c per COPY,
    # but TEMP tables disappear when the session closes. Compromise: do a
    # single big psql session that runs the SQL pipeline via heredoc with
    # \copy commands for each surface. That requires file-based input for
    # each COPY (psql \copy reads from a file).

    # Write each surface to a tab file under /tmp.
    files = []
    def dump(name, rows):
        path = f"/tmp/_ucd_{name}.tsv"
        with open(path, "w") as f:
            for r in rows:
                # COPY TEXT file input: same escape rules as STDIN. bytea
                # hex literal needs "\\x{hex}" (literal backslash+x+hex).
                # hex_bytea() already produced that form.
                f.write("\t".join(r) + "\n")
        files.append(path)
        return path

    p_ent  = dump("ent",  entity_rows)
    p_cls  = dump("cls",  classification_rows)
    p_phys = dump("phys", physicality_rows)
    p_gc   = dump("gc",   cp_gc)
    p_sc   = dump("sc",   cp_sc)
    p_blk  = dump("blk",  cp_blk)
    p_bc   = dump("bc",   cp_bc)
    p_eaw  = dump("eaw",  cp_eaw)
    p_gcb  = dump("gcb",  cp_gcb)
    p_wb   = dump("wb",   cp_wb)
    p_sb   = dump("sb",   cp_sb)
    p_lb   = dump("lb",   cp_lb)

    sql_path = "/tmp/_ucd_pipeline.sql"
    sql = f"""
BEGIN;

CREATE TEMP TABLE _ent_in   (h bytea);
CREATE TEMP TABLE _cls_in   (h bytea, t int, p int);
CREATE TEMP TABLE _phys_in  (h bytea, x double precision, y double precision,
                             z double precision, m double precision);
CREATE TEMP TABLE _cp_gc    (h bytea, v int);
CREATE TEMP TABLE _cp_sc    (h bytea, v int);
CREATE TEMP TABLE _cp_blk   (h bytea, v int);
CREATE TEMP TABLE _cp_bc    (h bytea, v int);
CREATE TEMP TABLE _cp_eaw   (h bytea, v int);
CREATE TEMP TABLE _cp_gcb   (h bytea, v int);
CREATE TEMP TABLE _cp_wb    (h bytea, v int);
CREATE TEMP TABLE _cp_sb    (h bytea, v int);
CREATE TEMP TABLE _cp_lb    (h bytea, v int);

\\copy _ent_in   FROM '{p_ent}'  (FORMAT text)
\\copy _cls_in   FROM '{p_cls}'  (FORMAT text)
\\copy _phys_in  FROM '{p_phys}' (FORMAT text)
\\copy _cp_gc    FROM '{p_gc}'   (FORMAT text)
\\copy _cp_sc    FROM '{p_sc}'   (FORMAT text)
\\copy _cp_blk   FROM '{p_blk}'  (FORMAT text)
\\copy _cp_bc    FROM '{p_bc}'   (FORMAT text)
\\copy _cp_eaw   FROM '{p_eaw}'  (FORMAT text)
\\copy _cp_gcb   FROM '{p_gcb}'  (FORMAT text)
\\copy _cp_wb    FROM '{p_wb}'   (FORMAT text)
\\copy _cp_sb    FROM '{p_sb}'   (FORMAT text)
\\copy _cp_lb    FROM '{p_lb}'   (FORMAT text)

-- Pre-existing rows safe via ON CONFLICT DO NOTHING.
INSERT INTO substrate.entity (hash)
SELECT h FROM _ent_in
ON CONFLICT (hash) DO NOTHING;

INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id)
SELECT h, t, p FROM _cls_in
ON CONFLICT (entity_hash, entity_type_id, provenance_id) DO NOTHING;

INSERT INTO substrate.physicality (physicality_type_id, entity_hash, content_hash, geom, partition_bucket)
SELECT {physicality_entity_type_id}, h, h,
       ST_SetSRID(ST_MakePoint(x, y, z, m), 0),
       (get_byte(h, 0) & 7)::SMALLINT
  FROM _phys_in
ON CONFLICT (physicality_type_id, entity_hash, content_hash, partition_bucket) DO NOTHING;

TRUNCATE substrate.cp_general_category, substrate.cp_script, substrate.cp_block,
         substrate.cp_bidi_class, substrate.cp_east_asian_width,
         substrate.cp_grapheme_break, substrate.cp_word_break,
         substrate.cp_sentence_break, substrate.cp_line_break;

INSERT INTO substrate.cp_general_category SELECT h, v FROM _cp_gc;
INSERT INTO substrate.cp_script           SELECT h, v FROM _cp_sc;
INSERT INTO substrate.cp_block            SELECT h, v FROM _cp_blk;
INSERT INTO substrate.cp_bidi_class       SELECT h, v FROM _cp_bc;
INSERT INTO substrate.cp_east_asian_width SELECT h, v FROM _cp_eaw;
INSERT INTO substrate.cp_grapheme_break   SELECT h, v FROM _cp_gcb;
INSERT INTO substrate.cp_word_break       SELECT h, v FROM _cp_wb;
INSERT INTO substrate.cp_sentence_break   SELECT h, v FROM _cp_sb;
INSERT INTO substrate.cp_line_break       SELECT h, v FROM _cp_lb;

COMMIT;
"""
    with open(sql_path, "w") as f:
        f.write(sql)
    proc = subprocess.run(["psql", "-d", DB, "-v", "ON_ERROR_STOP=1", "-f", sql_path],
                          capture_output=True, text=True)
    t_write = time.monotonic() - t3
    if proc.returncode != 0:
        print("STDOUT:", proc.stdout)
        print("STDERR:", proc.stderr, file=sys.stderr)
        sys.exit(f"psql pipeline failed rc={proc.returncode}")
    print(proc.stdout)
    print(f"[write] full pipeline in {t_write:.2f}s")

    # Cleanup tsv files
    for f in files:
        try:
            os.unlink(f)
        except OSError:
            pass

    t_total = time.monotonic() - t0
    print(f"\n[done] {n_chars:,} codepoints → "
          f"{n_chars} entity, {n_chars} classification, {n_phys} physicality, "
          f"{len(cp_gc)+len(cp_sc)+len(cp_blk)+len(cp_bc)+len(cp_eaw)+len(cp_gcb)+len(cp_wb)+len(cp_sb)+len(cp_lb)} property junction rows "
          f"in {t_total:.2f}s")


if __name__ == "__main__":
    main()
