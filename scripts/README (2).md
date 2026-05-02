# Substrate seed download scripts

This directory contains the orchestration for downloading the curated seed datasets the substrate ingests.

## Quick start

```powershell
# Install dependencies
pip install pyyaml requests tqdm huggingface_hub

# See what's in the catalog (no download)
python download_seeds.py --list --tier all

# See what tier 1 would do (no download)
python download_seeds.py --tier 1 --dry-run

# Actually download tier 1
python download_seeds.py --tier 1

# Download a specific dataset
python download_seeds.py --dataset conceptnet-5.7

# Re-download something (overwrite manifest entry)
python download_seeds.py --dataset conceptnet-5.7 --force

# Backfill manifest from already-existing data (for tier 0 stuff already on disk)
python download_seeds.py --tier 0 --manifest-only

# Use a different data root
python download_seeds.py --tier 1 --data-root D:\OtherModels
```

## Files

- `seeds.yaml` — declarative catalog of every curated dataset (URL, source type, license, target path, tree-sitter grammar that will eventually decompose it). The single source of truth.
- `download_seeds.py` — the driver. Reads `seeds.yaml`, dispatches to handlers per source type, tracks state in `D:\Models\seed_manifest.json`.
- `README.md` — this file.

## How it works

The driver dispatches on the `source` field of each dataset entry:

| Source | Handler | What it does |
|---|---|---|
| `http_file` | `handle_http_file` | Download single non-archive file into `target/` |
| `http_files` | `handle_http_files` | Download multiple files into `target/`; auto-extract any zips |
| `http_archive` | `handle_http_archive` | Download archive, extract to `target/`, remove archive if >500 MB |
| `git` | `handle_git` | `git clone --depth 1 --no-tags <url> <target>` |
| `hf_dataset` | `handle_hf_dataset` | `huggingface_hub.snapshot_download` for `repo_id` |
| `manual` | `handle_manual` | Print instructions, skip (registration required, etc.) |

Decompression is auto-detected from filename suffix (`.zip`, `.tar.gz`, `.tar.bz2`, `.gz`, `.bz2`, `.tar`).

## Manifest

`<data-root>/seed_manifest.json` tracks completed downloads:

```json
{
  "conceptnet-5.7": {
    "name": "conceptnet-5.7",
    "source": "http_file",
    "target": "D:/Models/conceptnet-5.7",
    "license": "CC-BY-SA-4.0",
    "license_flags": ["share_alike"],
    "size_mb_estimated": 500,
    "grammar": "tree-sitter-conceptnet-csv (TO AUTHOR; ~60 lines)",
    "downloaded_at": "2026-04-29T14:32:11",
    "downloaded_by_version": "1.0",
    "files": [
      {"path": "conceptnet-assertions-5.7.0.csv.gz", "size": 524288000, "sha256": "..."},
      {"path": "conceptnet-assertions-5.7.0.csv", "size": 3221225472}
    ]
  },
  ...
}
```

The manifest is the substrate operator's audit trail: what was downloaded, when, with what license, what's on disk per dataset.

`sha256` is computed only for files <100 MB to keep manifest size reasonable.

## Tiers

| Tier | Meaning | Approx total size |
|---|---|---|
| 0 | Already on disk (UCD, ISO 639, WordNet, OMW, UD, Wiktionary, Tatoeba, ATOMIC 2020) | ~33 GB |
| 1 | Foundational additions (small, high value) | ~1.5 GB |
| 2 | Moderate effort, includes Visual Genome images | ~25 GB |
| 3 | Specialized + small-but-valuable extras + formal math | ~5 GB |

Excludes by user direction: Wikidata, DBpedia, YAGO, full Common Voice, full LibriSpeech, full Lakh MIDI, The Stack v2 (use HF streaming for that one when needed).

## Manual-download datasets

Some datasets require registration / form submission and CANNOT be auto-downloaded. The driver flags these and prints instructions. They are:

- WordNet Domains (https://wndomains.fbk.eu/)
- NRC EmoLex (https://saifmohammad.com/WebPages/NRC-Emotion-Lexicon.htm)
- NRC VAD Lexicon (https://saifmohammad.com/WebPages/nrc-vad.html)
- DialogBank (https://dialogbank.uvt.nl/)
- AMI Meeting Corpus text portion (https://groups.inf.ed.ac.uk/ami/download/)
- VUA Metaphor Corpus (http://www.vismet.org/metcor/)
- TempEval-3 (https://www.cs.york.ac.uk/semeval-2013/task1/)
- MEANTIME corpus (http://www.newsreader-project.eu/results/data/wikinews/)
- SpaceEval 2015 (https://alt.qcri.org/semeval2015/task8/)
- ISEAR (https://www.unige.ch/cisa/research/materials-and-online-research/research-material/)
- CoNLL-2003 (https://www.clips.uantwerpen.be/conll2003/ner/)
- PreCo (https://preschool-lab.github.io/PreCo/)
- ASL-LEX (https://asl-lex.org/download.html)
- GLUCOSE (https://tinyurl.com/glucose-data)
- VerbAtlas (https://verbatlas.org/download)
- LeanDojo Benchmark 4 (https://zenodo.org/records/12740403)

Place these into the indicated `target` subdirectory, then run `python download_seeds.py --manifest-only --tier all` to register them.

## License flags

Datasets are tagged with license flags the substrate must respect:

- `permissive` — CC0/MIT/Apache/BSD/CC-BY — commercial use OK
- `share_alike` — CC*-SA — derivatives must preserve license
- `non_commercial` — CC*-NC — substrate must NOT include in commercial outputs (Laplace exports, Refinement-as-Service results)
- `research_only` — free for research only — verify per use case

The substrate's recomposer should consult provenance license flags when materializing safetensors. Edges from `non_commercial`-flagged provenance must be filterable out for commercial export targets.

## Tree-sitter grammar references

Each dataset entry's `grammar:` field names the tree-sitter grammar (existing or to-author) that the substrate's decomposer will use to parse it. See `docs/20-technical/16-tree-sitter-grammar-strategy.md` for the grammar authorship plan.

The download is independent of the grammar work — getting bytes onto disk does not require grammars to exist yet. But ingestion (turning bytes into substrate state) requires the corresponding grammar plus an AST→substrate mapping function.

## Verifying installation

After running the script, verify with:

```powershell
# How many datasets are in the manifest
python -c "import json; m = json.load(open(r'D:\Models\seed_manifest.json')); print(len(m), 'datasets'); [print(' ', n) for n in sorted(m)]"

# How much disk used per dataset
python -c "import json, pathlib; m = json.load(open(r'D:\Models\seed_manifest.json')); [print(f'{sum(f[\"size\"] for f in d[\"files\"]) / 1024 / 1024:>10.1f} MB  {n}') for n, d in sorted(m.items())]"
```

## Idempotency and re-runs

Re-running the script:

- Datasets already in the manifest are SKIPPED unless `--force`
- Datasets whose target directory exists but aren't in the manifest are RECORDED in the manifest without re-download
- `--force` re-downloads (re-clones for git) and overwrites manifest entry
- `--dry-run` previews actions without modifying anything

## Adding a new dataset

Add an entry to `seeds.yaml`:

```yaml
  - name: my-new-dataset
    description: "..."
    tier: 2
    source: http_archive
    url: https://example.com/data.zip
    target: my-new-dataset
    license: CC-BY-4.0
    license_flags: [permissive]
    size_mb: 50
    grammar: tree-sitter-mynewdata-xxx (TO AUTHOR; ~N lines)
```

Then `python download_seeds.py --dataset my-new-dataset --dry-run` to verify.

## Updating Tier 0 datasets

The Tier 0 datasets (already-on-disk seeds) are listed in `seeds.yaml` for manifest completeness. To re-fetch them (e.g., new Unicode release, new UD release):

- **UCD**: `rsync -av --delete ftp://ftp.unicode.org/Public/UCD/latest/ D:/Models/UCD/Public/UCD/latest/`
- **ISO 639**: download fresh .tab files from sil.org
- **WordNet 3.0**: re-download tarball from Princeton; structure is stable
- **OMW**: pull each language's data from per-language sources
- **UD treebanks**: download new tarball from `https://lindat.mff.cuni.cz/repository/xmlui/handle/11234/...` (URL changes per release)
- **Wiktionary**: download fresh from `https://kaikki.org/dictionary/rawdata.html`
- **Tatoeba**: re-download CSVs from `https://tatoeba.org/eng/downloads`
- **ATOMIC 2020**: stable; this version is canonical

These are deliberately `source: manual` in `seeds.yaml` to avoid accidental re-download.

## Cross-references

- The seeds catalog: `seeds.yaml`
- Tree-sitter grammar strategy: `../docs/20-technical/16-tree-sitter-grammar-strategy.md`
- Seed expansion roadmap: `../docs/20-technical/15-seed-expansion-roadmap.md`
- Verified data asset paths: `../docs/50-reference/04-data-asset-paths.md`
- Provenance catalog (license flag semantics): `../docs/20-technical/13-provenance-catalog.md`
- Implementation roadmap: `../docs/40-process/04-implementation-roadmap.md`
