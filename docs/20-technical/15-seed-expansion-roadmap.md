# Seed Expansion Roadmap — Communication Dimensions and Free Datasets

**Status:** Canonical (initial draft); living document
**Last verified:** 2026-04-29
**Audience:** Anyone planning what to ingest beyond the M1–M9 foundational seeds. Anyone designing decomposers for new sources.

---

## What the foundational seeds already cover

After M2–M5 (UCD/UCA, ISO 639, WordNet, OMW, UD, Wiktionary, Tatoeba), the substrate covers:

- **Atom layer:** every Unicode codepoint with deterministic S³ position, full UAX #29 segmentation properties, NFC/NFD/case-fold mappings, confusables, emoji
- **Lexical layer (English-anchored):** ~117K WordNet synsets, ~150K English lemmas, hypernym/hyponym/meronym/holonym/antonym/entailment edges, glosses, examples
- **Multilingual lexical:** OMW grafts ~30 languages onto Princeton synset spine; Wiktionary adds breadth across hundreds of languages with etymology, IPA, inflections, translations
- **Syntactic layer:** UD v2.17 across 339 treebanks in 100+ languages — POS tags, dependency relations, morphological features
- **Sentence-level usage:** Tatoeba sentence pairs across 400+ languages with audio recordings for some
- **Common-sense knowledge graph:** **ATOMIC 2020** (1.07M tuples, 23 relations — already on disk)

That covers atoms → graphemes → words → senses → syntax → sentences → some commonsense. What it does NOT cover:

- **Pragmatics** — what speakers DO with utterances (request, promise, apologize, hedge, etc.)
- **Discourse structure** — rhetorical/coherence relations across sentences
- **Reference / coreference** — entity tracking across discourse
- **Named entities** with rich metadata
- **Spatial language** — locations, directions, motion
- **Temporal language beyond UD's tense morphology** — explicit time expressions, durations, sequences
- **Causal language** — cause/effect/enablement/prevention
- **Semantic roles / predicate-argument structure** beyond UD's basic deprel
- **Frame semantics** — situations and participant roles
- **Multiword expressions / idioms**
- **Emotion / affect / sentiment**
- **Social norms / morality / ethics**
- **Hate speech / offense / bias**
- **Metaphor / figurative language**
- **Genre / register / style**
- **Phonology beyond IPA spellings** — phoneme inventories, sound patterns
- **Typological features** — language similarity beyond OMW's lexical alignment
- **Cultural / world knowledge** — entities, events, structured facts
- **Mathematical / formal reasoning**
- **Sign languages**
- **Historical languages**

Each of these is a layer of human communication the substrate currently has no edges for. Every layer that's missing is a substrate capability that doesn't exist yet — substrate inference traversing in any of these arenas would return nothing or default-mu.

## ISO 24617 — the canonical model

ISO/TC 37/SC 4 maintains the **Semantic Annotation Framework (SemAF)** as ISO 24617, a multi-part standard. Each part standardizes annotation in a specific semantic dimension, with formal markup languages and conformance test corpora. The series is exactly the right model for substrate seed expansion: each part is a domain-specific seed with authoritative-standard provenance.

| Part | Domain | Status | Substrate value |
|---|---|---|---|
| **24617-1** | Time and events (ISO-TimeML) | Published 2012 | Temporal expressions, events, temporal links |
| **24617-2** | Dialogue acts (DiAML / DialogBank) | Published 2012, revised 2020 | Speech acts, communicative functions, multi-dimensional dialogue annotation |
| **24617-3** | Named entities | Published 2017 | Person/organization/location/etc with structured properties |
| **24617-4** | Semantic roles | Published 2014 | Predicate-argument structure (agent/theme/instrument/etc.) |
| **24617-5** | Discourse structure | Published 2014 | Discourse units and their hierarchical organization |
| **24617-6** | Spatial information (ISO-Space) | Published 2016 | Locations, directions, paths, spatial relations |
| **24617-7** | Time and events (revision/integration) | Published 2020 | Refinement of Part 1 |
| **24617-8** | Discourse relations (ISO-DR-Core) | Published 2016 | Coherence relations between discourse units |
| **24617-9** | Reference annotation | Published 2019 | Coreference, anaphora, deixis |
| **24617-11** | Measurable quantitative information | Published 2023 | Quantities, measurements, units |

Each ISO 24617 part has **conformance corpora** annotated with the standard. Some are freely available:

- **DialogBank** (24617-2) — multi-corpus collection at `dialogbank.uvt.nl`, includes Map Task, Schiphol, AMI Meeting, Switchboard SWBD-DAMSL re-annotations, Verbmobil, SWITCH (Spanish/German), TRAINS, OVIS. Free for research.
- **ISO-Space corpora** (24617-6) — SpaceEval 2015 SemEval shared task data.
- **ISO-TimeML corpora** (24617-1/7) — TimeBank 1.2 is the original (paid via LDC); TempEval shared task data is free.
- **GUM corpus** — Georgetown University Multilayer corpus (CC BY 4.0) — 200+ documents annotated for RST discourse, coreference, UD syntax, named entities, discourse markers across 14 genres. Single corpus covering ISO 24617 parts 3, 4, 5, 8, 9 simultaneously.

When a free ISO 24617 conformance corpus exists for a part, that's the highest-value seed for that semantic dimension. When it doesn't (Part 11 quantities), you fall back to alternative datasets.

## Per-dimension catalog of freely-available seeds

Organized by communication dimension, with license, format, decomposer effort estimate, and substrate-structural value.

Effort tiers:
- **L (low)** — flat structured data (TSV/CSV/JSONL with regular schema), maps directly to typed edges. ~1-3 days of decomposer engineering.
- **M (medium)** — structured but non-trivial (XML annotation layers, RDF, multi-file relations). ~1-2 weeks.
- **H (high)** — requires custom parser, multi-pass extraction, or schema design work. ~2-6 weeks.

### Dimension: Commonsense / world knowledge

| Dataset | Size | License | Format | Effort | Path / source |
|---|---|---|---|---|---|
| **ATOMIC 2020** | 1.33M tuples, 23 relations | CC BY 4.0 | TSV (head/relation/tail) | **L** | ON DISK: `D:\Models\atomic2020_data-feb2021\` |
| **ConceptNet 5** | 21M edges, 83 languages | CC BY-SA 4.0 | CSV/JSON dumps | **L** | `conceptnet.io/downloads` |
| **GLUCOSE** | 670k explanations of everyday events | CC BY-NC 4.0 | JSON | **M** | `tinyurl.com/glucose-data` |
| **Wikidata** | 100M+ entities, billions of statements | **CC0** (public domain) | JSON dumps weekly | **H** | `dumps.wikimedia.org/wikidatawiki/entities/` |
| **DBpedia** | Wikipedia-extracted RDF | CC BY-SA + GFDL | RDF Turtle/N-Triples | **H** | `databus.dbpedia.org/dbpedia/` |
| **YAGO 4** | Wikidata + WordNet + schema.org | CC BY-SA | RDF | **M** | `yago-knowledge.org/downloads` |

**What commonsense KGs add structurally:** explicit if-then world knowledge that no curated lexical resource captures. ATOMIC tells you "if PersonX drops a glass" → "the glass breaks" (oEffect). ConceptNet tells you "knife" → `UsedFor` → "cut food." Wikidata tells you Albert Einstein was born 1879-03-14 in Ulm. WordNet doesn't have any of this.

For inference recipes that need to reason about practical consequences, social dynamics, or factual claims, these are foundational. Without them, the substrate's answers are "lexically correct but commonsensically empty."

### Dimension: Pragmatics / dialogue acts (ISO 24617-2)

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **DialogBank** | Multiple corpora annotated with DiAML | Free for research | XML (DiAML) | **M** | `dialogbank.uvt.nl/` |
| **Switchboard SWBD-DAMSL** | 1,155 conversations, ~200k utterances | Research license (some free) | Custom XML | **M** | `web.stanford.edu/~jurafsky/ws97/` |
| **AMI Meeting Corpus** | 100h meetings, dialogue acts | CC BY 4.0 | XML | **H** | `groups.inf.ed.ac.uk/ami/corpus/` |
| **MultiWOZ** | 10k task-oriented dialogues | MIT | JSON | **L** | `github.com/budzianowski/multiwoz` |

**What dialogue acts add structurally:** the substrate gains a layer above lexical/syntactic that tells it WHAT IS BEING DONE by an utterance. "Can you pass the salt?" is a Request, not a question about ability. "I'm sorry" is an Apologize. The substrate's inference can then traverse `dialogue_act` edges in a `pragmatic_intent` arena to disambiguate intent independent of surface form.

DiAML is multi-dimensional: each utterance can have multiple co-occurring functions (informing + apologizing + turn-management). Substrate edges should preserve this — one utterance entity gets multiple `has_dialogue_act` edges, each in its own arena.

### Dimension: Discourse structure (ISO 24617-5, 8)

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **GUM corpus** | 200+ docs, 14 genres, multilayer (RST + coref + UD + NER) | CC BY 4.0 | CoNLL-U + XML | **M** | `corpling.uis.georgetown.edu/gum/` |
| **DiscoDisco / DISRPT shared task** | Discourse relation parsing data, multi-language | Mixed, mostly free | CoNLL | **M** | `disrpt.github.io/` |
| **PDTB-3** | 53k discourse relations | LDC license (NOT free) | — | — | (skip; paywalled) |
| **RST-DT** | 385 WSJ docs | LDC license (NOT free) | — | — | (skip; paywalled) |

**What discourse structure adds:** rhetorical/coherence relations between discourse units. "It rained. So we stayed home." has a Cause-Effect relation; "He's smart. But lazy." has a Concession. Substrate edges of type `discourse_relation:cause`, `discourse_relation:concession`, `discourse_relation:elaboration`, `discourse_relation:contrast`, etc. between text_composition entities at sentence/clause granularity.

GUM is the right primary target: free, multilayer, covers RST + ISO 24617-3 NER + ISO 24617-9 coreference + UD all on the same documents. One ingestion, multiple substrate dimensions populated.

### Dimension: Reference / coreference (ISO 24617-9)

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **GUM corpus** | (same as above; has coreference layer) | CC BY 4.0 | CoNLL with coref column | **M** | (already listed) |
| **OntoNotes 5.0** | Large multilayer corpus | LDC license (NOT free) | — | — | (skip; paywalled) |
| **WinoBias / Winogrande** | Coreference benchmark with bias dimension | MIT / Apache 2.0 | JSON | **L** | `github.com/uclanlp/corefBias`, `winogrande.allenai.org/` |
| **PreCo** | 38k multi-document coreference | CC BY 4.0 | JSON | **L** | `preschool-lab.github.io/PreCo/` |

**What coreference adds:** entity tracking across discourse. The substrate gains `corefers_with` edges between mention entities; downstream queries like "what does 'it' refer to here" become traversal.

### Dimension: Named entities (ISO 24617-3)

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **GUM corpus** | (NER layer) | CC BY 4.0 | (already listed) |   |   |
| **WikiNER** | Multilingual NER from Wikipedia | CC BY-SA | CoNLL | **L** | `figshare.com/articles/Learning_multilingual_named_entity_recognition_from_Wikipedia/5462500` |
| **CoNLL-2003** | English/German NER benchmark | Free for research | CoNLL | **L** | `www.clips.uantwerpen.be/conll2003/ner/` |
| **OntoNotes** | (paywalled) | — | — | — | (skip) |
| **Few-NERD** | Hierarchical NER, 188K sentences, 66 fine-grained types | CC BY-SA 4.0 | JSON | **L** | `github.com/thunlp/Few-NERD` |

**What NER adds:** structured entity recognition with type hierarchy. The substrate gains `is_named_entity_of_type` edges from text_composition mentions to entity types (PERSON, ORGANIZATION, LOCATION, EVENT, PRODUCT, etc.), composable with Wikidata for full structured entity lookup.

### Dimension: Semantic roles / frame semantics (ISO 24617-4)

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **FrameNet 1.7 (Berkeley)** | 1,224 frames, 13K lexical units, 200K annotated example sentences | Free for research (registration) | XML | **H** | `framenet.icsi.berkeley.edu/` |
| **VerbNet 3.4** | 270+ verb classes with syntactic frames + semantic predicates | Unicode/Apache 2.0 license | XML | **M** | `verbs.colorado.edu/verbnet/` |
| **PropBank** | (paywalled via LDC for full Penn Treebank version, but English PropBank frame files are free) | Free for frame files | XML | **M** | `propbank.github.io/` |
| **Universal Propositions Bank** | Multilingual PropBank-style annotation | Apache 2.0 | XML | **M** | `github.com/UniversalPropositions/UP` |
| **FrameNet languages (Spanish, Japanese, German, etc.)** | Per-language FrameNets | Mixed, often free | XML | **M** | various |

**What frame semantics adds:** verb-centric scenes with structured participants. "Mary gave Bob a book" → `Giving` frame with FE_Donor=Mary, FE_Theme=book, FE_Recipient=Bob. The substrate gains a deeper-than-UD-deprel layer that captures WHICH ROLE each argument plays in the event-frame.

VerbNet is more directly substrate-friendly than FrameNet: cleaner XML, principled class structure, semantic predicates as logical formulas. Apache-licensed. Should be M4-tier (alongside WordNet/OMW/UD).

### Dimension: Spatial information (ISO 24617-6 / ISO-Space)

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **SpaceEval 2015 (SemEval Task 8)** | Annotated spatial language | Free for research | XML | **M** | `alt.qcri.org/semeval2015/task8/` |
| **ISO-Space annotated corpora** | Various | Mixed | XML | **M** | per-corpus |
| **Talk of the Town / Map Task** | Spatial dialogue corpora | Free | XML | **M** | various |

**What spatial annotation adds:** "the cup on the table near the window" parses into spatial entities + relations (ON, NEAR, IN_FRONT_OF, etc.). Composable with Wikidata's geographic entities and OpenStreetMap.

### Dimension: Temporal information (ISO 24617-1, 7)

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **TimeBank 1.2** | 183 docs | LDC license (NOT free directly) | TimeML XML | — | (skip unless LDC-licensed) |
| **TempEval-3 corpora** | TimeML-annotated, multilingual | Free | TimeML XML | **M** | `www.cs.york.ac.uk/semeval-2013/task1/` |
| **Causal-TimeBank** | TimeBank extension with causal links | Free | XML | **M** | `github.com/paramitamirza/Causal-TimeBank` |
| **MEANTIME corpus** | 480 docs, 4 languages, news | CC BY-NC 4.0 | XML | **M** | `www.newsreader-project.eu/results/data/wikinews/` |

**What temporal annotation adds:** explicit time expressions (TIMEX3 — dates, times, durations, frequencies), events, and temporal links (BEFORE, AFTER, SIMULTANEOUS, OVERLAPS, etc.). The substrate's temporal-reasoning queries become traversals over `temporally_before` / `temporally_after` edges.

### Dimension: Causality

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **Causal-TimeBank** | (also temporal) | Free | XML | **M** | (listed above) |
| **BECauSE 2.0** | Causal language corpus | CC BY-SA 4.0 | XML | **L** | `github.com/duncanka/BECAUSE` |
| **EventCausalityData** | Events with causal links | Free for research | JSON | **L** | various papers |
| **Cause-Effect Pairs** | Structured cause-effect from various corpora | Mixed | TSV | **L** | various |

**What causality adds:** explicit cause/effect/enablement/prevention edges. ATOMIC has commonsense causation; these add corpus-attested causal language with linguistic markers ("because", "due to", "led to", "caused by", etc.).

### Dimension: Multiword expressions / idioms

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **PARSEME 1.3** | 26 languages, multiword expression annotations | CC BY 4.0 | CUPT (CoNLL extension) | **M** | `parsemefr.lis-lab.fr/` |
| **AIDLE** | English idiom expressions | Free | JSON | **L** | various |
| **MAGPIE corpus** | 56k idiom usages with idiomatic/literal labels | CC BY 4.0 | JSON | **L** | `github.com/hslh/magpie-corpus` |

**What MWEs add:** the lexicalized-vs-compositional distinction the substrate's idiomaticity geometry needs ground truth for. Substrate edges of type `is_lexicalized_idiom`, `is_verbal_mwe`, `is_named_compound`, etc. on `text_composition` entities.

### Dimension: Emotion / affect / sentiment

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **GoEmotions** | 58k Reddit comments, 27 emotions + neutral | Apache 2.0 | TSV | **L** | `github.com/google-research/google-research/tree/master/goemotions` |
| **EmoBank** | 10k sentences with VAD (valence/arousal/dominance) ratings | CC BY-SA 4.0 | CSV | **L** | `github.com/JULIELab/EmoBank` |
| **ISEAR** | 7,666 emotion-eliciting situations, 7 emotions | Free for research | CSV | **L** | various |
| **EmoLex (NRC Word-Emotion Association)** | 14k English words × 8 emotions | Free for research | TXT | **L** | `saifmohammad.com/WebPages/NRC-Emotion-Lexicon.htm` |
| **NRC VAD Lexicon** | 20k words with VAD | Free for research | TXT | **L** | `saifmohammad.com/WebPages/nrc-vad.html` |
| **SemEval 2018 Task 1 (Affect in Tweets)** | Multilingual emotion corpora | Free for research | TSV | **L** | `competitions.codalab.org/competitions/17751` |

**What emotion data adds:** each text_composition entity can be linked to emotion-category entities via `expresses_emotion` edges with significance from corpus annotation. Substrate query "what emotion does this passage convey" becomes traversal.

### Dimension: Social norms / morality / ethics

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **Social Chemistry 101** | 292k rules of thumb, 4.5M annotations | CC BY 4.0 | TSV | **L** | `github.com/mbforbes/social-chemistry-101` |
| **ETHICS** (Hendrycks et al.) | 130k ethical scenario judgments across 5 sub-tasks | MIT | CSV | **L** | `github.com/hendrycks/ethics` |
| **Moral Stories** | 12k narratives with norm/situation/intention/action | CC BY 4.0 | JSONL | **L** | `github.com/demelin/moral_stories` |
| **MoralIntegrityCorpus** | Moral-language annotations | Mixed | JSON | **L** | various |

**What ethics data adds:** substrate edges from situations to moral judgments (acceptable/unacceptable, harmful/helpful, fair/unfair). Crucial for any inference recipe involving ethical reasoning, alignment, or harm detection.

### Dimension: Hate / offense / bias

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **HurtLex** | ~1k lemmas × 50+ languages | CC BY-NC-SA 4.0 (non-commercial) | TSV | **L** | `github.com/valeriobasile/hurtlex` |
| **HateXplain** | 20k posts with rationales + targets | MIT | JSON | **L** | `github.com/hate-alert/HateXplain` |
| **HateCheck** | 3,901 functional test cases | CC BY 4.0 | CSV | **L** | `github.com/paul-rottger/hatecheck-data` |
| **MultilingualHateCheck** | 10 languages | CC BY 4.0 | CSV | **L** | `github.com/paul-rottger/multilingual-hatecheck` |
| **Social Bias Frames** | Implicit social biases | CC BY 4.0 | TSV | **L** | `homes.cs.washington.edu/~msap/social-bias-frames/` |
| **StereoSet** | Stereotype evaluation | CC BY-SA 4.0 | JSON | **L** | `stereoset.mit.edu/` |

**What hate/bias data adds:** explicit substrate edges flagging offensive language patterns, target groups, and bias frames. Critical for compliance-regulated customer recipes.

**Note on HurtLex CC BY-NC-SA:** non-commercial license. Cannot be ingested into a substrate that produces commercial outputs unless excluded from those outputs. Substrate-operator decision.

### Dimension: Metaphor / figurative language

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **VUA Metaphor Corpus** | 190k lexical units annotated | Free for research | XML | **M** | `pragglejaz.org/` |
| **MOH-X** | Adjective-noun metaphor pairs | Free | TSV | **L** | `github.com/UKPLab/EMNLP2017-MetaphorIdentification` |
| **TroFi** | Verb metaphor (literal vs figurative) | Free | TSV | **L** | `natlang.cs.sfu.ca/software/trofi.html` |
| **Magpie Idioms** | 56k idiom usages | CC BY 4.0 | (also listed under MWE) | **L** |   |

**What metaphor data adds:** ground truth for the substrate's geometric idiomaticity detection. Compositional centroid vs lexicalized centroid divergence (4D operator) gets validated against VUA's hand-annotated metaphors.

### Dimension: Phonology / phonetics

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **PHOIBLE 2.0** | 2,186 inventories, ~105k phonemes, 2,099 languages | CC BY-SA 3.0 | CSV/TSV | **L** | `phoible.org/` |
| **CMU Pronunciation Dictionary** | 134k English pronunciations | BSD-style | TXT | **L** | `www.speech.cs.cmu.edu/cgi-bin/cmudict` |
| **CLD3 / EpiTran** | G2P transducers | Apache 2.0 | code | **M** | `github.com/dmort27/epitran` |
| **WikiPron** | 250+ language pronunciation dictionaries | CC BY-SA | TSV | **L** | `github.com/CUNY-CL/wikipron` |
| **IPA Helps** | IPA chart and feature mappings | CC BY-SA | various | **L** |   |

**What phonology data adds:** per-language phoneme inventories (as substrate entities), phoneme features (place/manner of articulation), grapheme-to-phoneme mappings. The substrate gains explicit phonetic-similarity geometry beyond IPA codepoint Fréchet — inventory-level alignment across languages.

### Dimension: Typology / language similarity

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **WALS (World Atlas of Language Structures)** | ~200 features × ~2,700 languages | CC BY 4.0 | CSV | **L** | `wals.info/download` |
| **Glottolog 5.x** | ~25k language varieties with genealogy | CC BY 4.0 | CSV/RDF | **L** | `glottolog.org/meta/downloads` |
| **AUTOTYP** | Typological database with relational structure | CC BY 4.0 | CSV | **L** | `github.com/autotyp/autotyp-data` |
| **URIEL & lang2vec** | Pre-computed typological feature vectors | CC BY 4.0 | NumPy | **L** | `www.cs.cmu.edu/~dmortens/uriel.html` |

**What typology adds:** language-level feature vectors (SVO vs SOV, tonal vs non-tonal, has-articles vs no-articles, etc.) as language-entity properties. Cross-language queries get language-feature-aware filtering. Cross-lingual transfer recipes can prefer typologically similar source languages.

### Dimension: Cultural / world knowledge

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **Wikidata** | 100M+ items, 1B+ statements | **CC0** (no attribution required) | JSON dumps weekly | **H** | `dumps.wikimedia.org/wikidatawiki/entities/` |
| **DBpedia** | Wikipedia-extracted RDF, 125 languages | CC BY-SA + GFDL | Turtle/N-Triples | **H** | `databus.dbpedia.org/dbpedia/` |
| **YAGO 4** | Wikidata + WordNet + schema.org integrated | CC BY-SA | RDF | **M** | `yago-knowledge.org/downloads` |
| **Wikipedia dumps (per language)** | All articles as XML | CC BY-SA + GFDL | XML | **H** | `dumps.wikimedia.org/<lang>wiki/` |
| **BabelNet** | Princeton + Wikipedia + OmegaWiki integrated | Research license (free, registration) | RDF/JSON | **H** | `babelnet.org/` |

**What world knowledge adds:** the substrate gains every named entity Wikidata covers (people, places, events, organizations, scientific concepts, works of art, etc.) with structured properties (birth dates, locations, occupations, parent organizations, etc.). This is the layer that turns "do you know about X" into a structural-traversal query.

Wikidata is **CC0** — most permissive license possible. No attribution needed even for commercial use. The substrate can ingest aggressively without licensing constraints.

Wikipedia text dumps are CC BY-SA — usable, but provenance attribution needs to be preserved through any commercial export.

### Dimension: Code / programming languages

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **tiny-codes** | 1.63M NL↔code pairs, many languages | (already on disk) | parquet | (already covered) |   |
| **The Stack v2** (BigCode) | ~67TB code from permissive-license repos | Per-file licenses preserved | parquet | **H** | `huggingface.co/datasets/bigcode/the-stack-v2` |
| **CodeSearchNet** | 6 languages, ~6M functions with docstrings | MIT | JSONL | **L** | `github.com/github/CodeSearchNet` |
| **CommitPackFT** | 2GB permissively-licensed commits | Per-file licenses | parquet | **L** | `huggingface.co/datasets/bigcode/commitpackft` |

**What more code data adds:** beyond tiny-codes' synthetic pairs, real-world code patterns from production. The Stack covers all major languages at scale. CommitPackFT adds before/after code transformation pairs (refactor patterns).

### Dimension: Mathematics / formal reasoning

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **Mathlib (Lean)** | ~1.5M theorems formalized | Apache 2.0 | Lean source | **H** | `github.com/leanprover-community/mathlib4` |
| **MetaMath** | ~38k theorems in formal logic | CC BY 4.0 | metamath format | **M** | `us.metamath.org/` |
| **NaturalProofs / MMA** | Math proofs in natural language + symbolic | CC BY 4.0 | JSON | **M** | `naturalproofs.org/` |
| **ProofPile** | Math text from ArXiv + Wikipedia | Mixed | text | **L** | `huggingface.co/datasets/hoskinson-center/proof-pile-2` |

### Dimension: Music

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **Lakh MIDI Dataset (LMD-Full)** | 176,581 MIDI files | CC0 | MIDI | **M** | `colinraffel.com/projects/lmd/` |
| **MAESTRO** | 200h piano performances paired MIDI+audio | CC BY-NC-SA 4.0 | MIDI + WAV | **M** | `magenta.tensorflow.org/datasets/maestro` |
| **MusicNet** | 330 classical music recordings with note labels | CC BY 4.0 | WAV + CSV | **M** | `www.cs.washington.edu/research/musicnet/` |
| **MTG-Jamendo** | 55k tracks with tags | CC BY-NC-SA / CC0 | MP3 + JSON | **M** | `mtg.github.io/mtg-jamendo-dataset/` |
| **Free MusicXML scores** | various sources | Per-piece | MusicXML | **L** |   |

### Dimension: Audio / sound environment

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **AudioSet** | 2M+ YouTube clips with sound event labels (527 classes) | CC BY 4.0 (metadata) | CSV | **L** | `research.google.com/audioset/` |
| **FSD50K** | 51k Freesound clips, 200 classes | CC BY 4.0 | WAV + JSON | **M** | `zenodo.org/record/4060432` |
| **ESC-50** | 2k clips, 50 classes | CC BY-NC | WAV | **L** | `github.com/karoldvl/ESC-50` |
| **Common Voice** | Multilingual speech, hundreds of GB | **CC0** | MP3 + TSV | **M** | `commonvoice.mozilla.org/en/datasets` |
| **LibriSpeech** | 1000h English audiobook | CC BY 4.0 | FLAC + TXT | **M** | `www.openslr.org/12/` |
| **VoxLingua107** | 6,628h multilingual, 107 languages | CC BY 4.0 | WAV | **M** | `bark.phon.ioc.ee/voxlingua107/` |

### Dimension: Visual / scene

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **COCO** | 330k images, captions, segmentations | CC BY 4.0 | JSON + JPG | **M** | `cocodataset.org/` |
| **Visual Genome** | 100k images with dense scene graphs | CC BY 4.0 | JSON + JPG | **M** | `homes.cs.washington.edu/~ranjay/visualgenome/` |
| **OpenImages V7** | 9M images with annotations | CC BY 4.0 | CSV + JPG | **M** | `storage.googleapis.com/openimages/web/index.html` |
| **LAION-5B** | 5.85B image-text pairs (URLs) | CC BY 4.0 (metadata) | parquet | **H** | `laion.ai/blog/laion-5b/` |
| **Flickr30k** | 31k images with 5 captions each | CC BY 4.0 | TSV + JPG | **L** | `shannon.cs.illinois.edu/DenotationGraph/` |

**Visual Genome scene graphs** are particularly valuable for substrate cross-modal: each image gets a structured scene graph (objects, attributes, relations) that maps directly onto substrate edges between image-region entities and concept entities.

### Dimension: Sign languages

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **WLASL (American Sign Language)** | 21k videos, 2,000 word classes | C-UDA (research-only) | MP4 + JSON | **H** | `dxli94.github.io/WLASL/` |
| **DGS Korpus (German Sign Language)** | Large multimodal | Free for research | EAF + MP4 | **H** | `www.sign-lang.uni-hamburg.de/dgs-korpus/` |
| **ASL-LEX** | 2,723 ASL lexical items with linguistic features | CC BY-NC 4.0 | CSV | **L** | `asl-lex.org/` |
| **BSL Corpus** | British Sign Language | Research access | EAF | **H** | `bslcorpusproject.org/` |

**What sign-language data adds:** the substrate's text-centric foundation extends to manual languages. Each sign becomes a video-derived entity with phonological features (handshape, location, movement, palm orientation). Cross-modal `signs_for` edges connect ASL signs to English glosses; cross-language equivalences (ASL ↔ DGS for the same concept) become substrate edges.

### Dimension: Historical / ancient languages

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **PROIEL Treebank** | Old Church Slavonic, Latin, Ancient Greek, Old English, Gothic, Armenian | CC BY-NC-SA | CoNLL-U | **L** | `proiel.github.io/` |
| **Perseus Digital Library** | Greek/Latin classical texts with morphology | CC BY-SA | TEI XML | **M** | `www.perseus.tufts.edu/` |
| **EpiDoc** | Epigraphic XML standard for inscriptions | varies | TEI XML | **M** | `epidoc.stoa.org/` |
| **Vedic Sanskrit Treebank** | UD-format Sanskrit | CC BY-SA | CoNLL-U | **L** | `github.com/OliverHellwig/sanskrit/` |

PROIEL and Vedic Sanskrit Treebank fold into the existing UD ingestion path with minimal extra work — they're CoNLL-U files, just with non-modern languages.

### Dimension: Locale / formatting (Unicode CLDR)

| Dataset | Size | License | Format | Effort | Source |
|---|---|---|---|---|---|
| **Unicode CLDR** | Locale-specific data: date/number formatting, names, plural rules, calendars, currencies for ~700 locales | Unicode license (free) | XML + JSON | **M** | `cldr.unicode.org/index/downloads` |

CLDR is what makes "format this date for a French user" work correctly. Substrate adds locale-aware formatting rules as `locale_formats_<feature>` edges per language entity.

## Recommended ingestion priority (post-M9)

Tiered by structural value × decomposer effort:

### Tier-1 (immediate after M9 — low effort, high coverage)

| # | Dataset | License | Why first |
|---|---|---|---|
| 1 | **ATOMIC 2020** | CC BY 4.0 | On disk; ~1 day decomposer; massive commonsense lift |
| 2 | **ConceptNet 5** | CC BY-SA 4.0 | Multilingual commonsense; 21M edges; extends ATOMIC |
| 3 | **Wikidata** | **CC0** | Most permissive; structured world knowledge; foundation for entity linking |
| 4 | **GoEmotions** | Apache 2.0 | Emotion arena; ~1 day decomposer |
| 5 | **Social Chemistry 101** | CC BY 4.0 | Moral/social norms arena |
| 6 | **VerbNet** | Apache 2.0 | Frame semantics; ~1 week; complements UD |
| 7 | **CMU Pronunciation Dictionary + WikiPron** | BSD/CC BY-SA | English + multilingual phonology |
| 8 | **PHOIBLE 2.0** | CC BY-SA 3.0 | Per-language phoneme inventories |
| 9 | **WALS + Glottolog** | CC BY 4.0 | Typological features per language |
| 10 | **GUM corpus** | CC BY 4.0 | RST + coref + NER + UD all on same docs |

### Tier-2 (after Tier-1, moderate effort)

11. **DBpedia / YAGO 4** — Wikipedia-derived structured knowledge
12. **HateXplain + HateCheck + Social Bias Frames** — harm/bias edges
13. **MultiWOZ + DialogBank** — dialogue acts (ISO 24617-2)
14. **PARSEME 1.3** — multiword expressions (26 languages)
15. **VUA Metaphor + MAGPIE** — metaphor / figurative ground truth
16. **Causal-TimeBank + BECauSE 2.0** — causality
17. **Visual Genome** — scene graphs as cross-modal substrate edges
18. **CLDR** — locale-aware formatting

### Tier-3 (specialized; ingest as use cases demand)

19. **The Stack v2** — production code at scale
20. **Lakh MIDI / MAESTRO** — music
21. **AudioSet / FSD50K / Common Voice / LibriSpeech** — audio environment + speech
22. **Mathlib / MetaMath** — formal mathematics
23. **PROIEL / Perseus / Vedic Sanskrit** — historical languages
24. **WLASL / DGS Korpus / ASL-LEX** — sign languages
25. **GLUCOSE** — explanatory commonsense
26. **EmoBank + EmoLex + NRC VAD** — fine-grained affect

### Tier-4 (paywalled / restricted — only if license budget exists)

- TimeBank 1.2, RST-DT, PDTB-3, OntoNotes (LDC license; substantial license fees)
- BabelNet (research-only license; commercial license substantial)

## Decomposer architecture implications

Most of these datasets fit into one of three decomposer patterns:

**Pattern A: Flat structured triples (TSV/CSV)** — ATOMIC 2020, ConceptNet (CSV form), GoEmotions, Social Chemistry, ETHICS, NRC lexicons, EmoLex, HateXplain, Causal-TimeBank causal links. ~1-3 days each. The decomposer reads each row, calls `text_decompose` for any text-bearing field, emits one or two edges per row.

**Pattern B: Multilayer XML annotation (CoNLL+ / TEI / XML)** — GUM, PARSEME, FrameNet, VUA Metaphor, ISO-Space corpora, DialogBank. ~1-2 weeks each. The decomposer streams XML, builds parallel substrate entities for each annotation layer, emits cross-layer edges.

**Pattern C: Large structured RDF / JSON dumps** — Wikidata, DBpedia, BabelNet, ConceptNet (RDF form), YAGO. ~2-6 weeks. The decomposer needs streaming RDF parser, entity-canonicalization (Wikidata QIDs / DBpedia URIs → substrate hashes), property-mapping vocabulary.

The substrate's decomposer infrastructure should support all three patterns from a common base. Adding a new dataset in Pattern A becomes a 1-day engineering task once the pattern's framework exists.

## What this expansion does for product positioning

Each tier-1 dataset opens a new arena:

- ATOMIC + ConceptNet → `commonsense_relevance` arena (extends `semantic_relevance` with if-then world knowledge)
- Social Chemistry → `social_norm_alignment` arena
- ETHICS → `ethical_judgment` arena
- GoEmotions → `affective_resonance` arena
- HateXplain → `harm_detection` arena
- VerbNet → `frame_semantics` arena
- Wikidata → `factual_correctness` arena (entity claims grounded in Wikidata's structured properties)
- GUM → `discourse_coherence` arena
- DialogBank → `pragmatic_intent` arena

Customer recipes can then compose recipes with these arenas. A medical customer's recipe might restrict to `factual_correctness` + `medical_consensus` (if Wikidata + medical-corpus arenas are populated). A content-moderation recipe targets `harm_detection` + `ethical_judgment`. A creative-writing recipe weighs `affective_resonance` + `frame_semantics`.

The substrate's commercial value scales with how many of these dimensions are populated. Each dimension is a per-recipe filter customers can specify. Each is also a Laplace-original architecture target (Laplace-Ethics for ethical reasoning, Laplace-Affect for emotion-aware text generation, Laplace-Commonsense for chained reasoning).

## What I haven't explored

This catalog is comprehensive for English-anchored Western corpora and major world languages with research traditions. Gaps where seed coverage is genuinely thin:

- **Indigenous languages** beyond what UD and OMW cover. Most native North American, Australian Aboriginal, Pacific languages have minimal digital coverage. Substrate could be the consolidating layer if/when those resources emerge.
- **Programming languages beyond mainstream** — esoteric languages, domain-specific languages, older mainframe languages. The Stack covers most active ones.
- **Mathematical/scientific notation** as structured content (LaTeX semantic parsing, MathML). ProofPile partly covers this.
- **Legal language** — open-source legal corpora exist (Caselaw Access Project, etc.) but vary in licensing.
- **Medical / scientific** — PubMed abstracts (free, ~35M), full-text PubMed Central (CC license subset).
- **Domain-specific commonsense** beyond ATOMIC's social/physical default — culinary, mechanical, biological commonsense are all underserved.

These are future expansion targets. The Tier-1/2 listings above are the foundation; specialized verticals get added as customer demand justifies the decomposer effort.

## Cross-references

- Provenance catalog: `20-technical/13-provenance-catalog.md`
- Verified data asset paths: `50-reference/04-data-asset-paths.md`
- UCD inventory: `20-technical/14-ucd-inventory.md`
- Implementation roadmap (Tier-1 expansion fits as M10+): `40-process/04-implementation-roadmap.md`
- Decomposer contract (decomposers for new sources): `10-architecture/05-decomposer-contract.md`
- Arenas catalog (each new dataset opens new arenas): `20-technical/10-arenas-catalog.md`

## External references

- ISO 24617 series: <https://www.iso.org/committee/297592/x/catalogue/p/0/u/1/w/0/d/0>
- DialogBank: <https://dialogbank.uvt.nl/>
- ConceptNet: <https://conceptnet.io/>
- ATOMIC 2020 paper: Hwang et al., AAAI 2021, <https://arxiv.org/abs/2010.05953>
- Social Chemistry 101: <https://maxwellforbes.com/social-chemistry/>
- Wikidata downloads: <https://www.wikidata.org/wiki/Wikidata:Database_download>
- DBpedia downloads: <https://databus.dbpedia.org/dbpedia/>
- PHOIBLE: <https://phoible.org/>
- WALS: <https://wals.info/>
- Glottolog: <https://glottolog.org/>
- GUM corpus: <https://corpling.uis.georgetown.edu/gum/>
