# Seed Strategy — Why Each Dataset, In What Order, And What We're Extracting vs. Throwing Away

> Status: working strategy doc. Written against the live codebase at 2026-04-23. Authoritative companion to `docs/specs/csharp/ingestion-pipeline.md` (how) — this file answers *why* and *what from where*.

---

## 1. The thesis the seed phase is supposed to deliver

The substrate is the model. An LLM encodes `P(hypernym("dog") = "mammal")` as a pattern of floats entangled across 12 billion weights, recovered approximately at inference time under softmax noise. The substrate encodes it as one edge of type `hypernym` between two specific `synset` entities with stable BLAKE3 identity, Glicko-2 significance, and a 4D trajectory in S³. Retrieval is a graph hop plus a geometric distance comparison. No sampling, no approximation, no loss.

For that to be true, the seed phase must populate — not just mention — the full relational structure the datasets carry. "Ingest WordNet" means ingest the hypernym/hyponym/meronym/antonym/entailment/cause/also-see/similar-to/attribute/derivationally-related/pertainym graph, not just the synset atoms with their glosses.

We are currently ingesting the headers and discarding the content.

---

## 2. What each dataset actually contains, what we extract, what we drop

### 2.1 UCD / UCA (Unicode Character Database + Collation)

**Authority**: Unicode Consortium. Absolute ground truth for text orthography.

**Actual content**:
- ~155k assigned codepoints, each with: numeric value, official name, General_Category (Lu/Ll/Lt/Mn/Mc/Nd/Po/Sm/…), Script (Latin/Cyrillic/Greek/Han/Arabic/Devanagari/…), Block, canonical and compatibility decompositions (NFC/NFD/NFKC/NFKD), Canonical_Combining_Class, Bidi_Class, Bidi_Mirrored, East_Asian_Width, Numeric_Type + Numeric_Value (for digits, fractions, Han numerals), case mappings (simple and full: upper/lower/title/fold), Line_Break, Word_Break, Sentence_Break, Grapheme_Cluster_Break, Indic_Syllabic_Category, Indic_Positional_Category, Jamo_Short_Name.
- ~300 derived binary properties: Alphabetic, Lowercase, Uppercase, ID_Start, ID_Continue, Math, White_Space, Hex_Digit, Quotation_Mark, Dash, Terminal_Punctuation, Default_Ignorable_Code_Point, Emoji / Emoji_Presentation / Emoji_Modifier / Emoji_Component / Extended_Pictographic, …
- Named sequences, standardized variants, emoji sequences / ZWJ sequences.
- UCA DUCET: per-codepoint primary/secondary/tertiary collation weights for locale-neutral sort.
- CLDR locale tailoring tables.

**What we extract** (verified against `UcdUcaDecomposer` + `codepoint_property` junction):
- Codepoint entities with BLAKE3 of codepoint integer — good.
- `codepoint_property` junction holds: general_category, script, block, grapheme_cluster_break, word_break, sentence_break, line_break — good, and a real asset.
- Case-fold edges and `has_collation_weight` edges exist as *types* — emission in current code is partial.

**What we drop**:
- All decomposition data (NFC/NFD chains). We cannot currently answer "é = e + ́" at the substrate level. This is foundational for cross-lingual matching and homograph collapse.
- Numeric_Value (we cannot answer "Ⅻ = 12", "½ = 0.5", "五 = 5").
- Bidi_Class (no RTL-aware reasoning).
- All ~300 derived binary properties beyond the seven we capture — Alphabetic, ID_Start/Continue, Math, Emoji family, White_Space, Hex_Digit, Quotation_Mark, Dash. Each is a one-bit junction; it's free.
- Indic_Syllabic_Category / Indic_Positional_Category (needed for any Indic script ingestion).
- Named sequences and emoji ZWJ sequences — these are lexicalized multi-codepoint atoms. `👨‍👩‍👧‍👦` IS one conceptual atom; we currently treat it as four codepoints joined by ZWJ.
- Jamo decomposition (no Korean syllable structure).
- Full case mappings (we do simple case fold; full case fold for Turkish ı/İ, German ß → SS, is different).

### 2.2 ISO 639

**Authority**: SIL International. Language identity.

**Actual content**:
- ISO 639-3: ~7,900 individual language codes.
- ISO 639-2/B (bibliographic) and 639-2/T (terminological) — the legacy 3-letter codes.
- ISO 639-1 — the legacy 2-letter codes.
- Macrolanguage membership (zho → cmn, yue, wuu, …; ara → arb, apc, acw, …).
- Scope: Individual / Macrolanguage / Special / Collection.
- Type: Living / Extinct / Ancient / Historical / Constructed.
- Retirement records (merged / split / deprecated codes).
- Ref_Name, Print_Name, Inverted_Name in English plus per-language common names.

**What we extract**:
- 7,927 `language_name` entities + `entity_language` junction rows + macrolanguage mapping and name-index entries. This is complete.

**What we drop**:
- Scope and type aren't structured into reference junctions — we have them but not queryable by type.
- Retired-code redirect edges (needed when old corpora reference obsolete codes).
- ISO 15924 script codes (not in ISO 639 but required to pair with it — "written in Cyrillic" vs "written in Latin" for Serbian).

### 2.3 WordNet 3.0 (Princeton, English)

**Authority**: the canonical English semantic graph.

**Actual content**:
- 117,659 synsets across 4 POS (noun, verb, adjective, adverb). Each synset: offset, POS, lexfile (one of 45 lexicographer files — `noun.animal`, `verb.motion`, …), SS_type (s for satellite adjective), list of lemma-forms that realize this meaning, gloss (definition + 0..N examples), pointers.
- 206,941 sense-index entries. Each entry: sense_key (lemma%POS:lexfile_num:lex_id:head_word:head_id), synset offset, sense number (1-indexed, frequency-ordered), tag_count (SemCor corpus frequency — **critical signal for sense priors**).
- **Pointers** (synset-level and word-level semantic relations):
  - `@` hypernym, `@i` instance_hypernym
  - `~` hyponym, `~i` instance_hyponym
  - `#m` member_holonym, `#s` substance_holonym, `#p` part_holonym
  - `%m` member_meronym, `%s` substance_meronym, `%p` part_meronym
  - `=` attribute
  - `+` derivationally_related_form (word-level)
  - `;c` domain_topic, `-c` member_topic
  - `;r` domain_region, `-r` member_region
  - `;u` domain_usage, `-u` member_usage
  - `*` entailment (verbs), `>` cause (verbs), `^` also_see, `$` verb_group, `&` similar_to (adjectives), `<` participle_of, `\` pertainym / derived_from
  - `!` antonym (word-level, bidirectional)
- Verb frames: 35 syntactic-realization templates per verb sense ("Somebody ----s something", "Something ----s somebody into V-ing something", …).
- Morph exceptions (`{noun,verb,adj,adv}.exc`): ~6k irregular inflections (mice → mouse, went → go).

**What we extract**:
- Synsets + lemmas + gloss text + example text — yes.
- `entity_pos` and `entity_sense` junctions — emitted. Glicko-2 priors exist on the junction row but we initialize μ=1500 σ=350 everywhere instead of priming from `tag_count`.
- Morph exceptions — emitted as inflected_form + `inflection_of` edge.
- Pointers are *attempted* in code (line 425-480 of `WordNetDecomposer.cs`): the code calls `batch.AddEdge("hypernym", …)`, `batch.AddEdge("hyponym", …)`, etc. — but those edge_type codes are not in `substrate.edge_type` and the decomposer never calls `UpsertStructuralEdgeTypeAsync` for them. So they either fail silently, throw at batch commit, or were supposed to be bootstrapped by a migration that doesn't exist.

**What we drop**:
- **SemCor tag_count**. This is the single most valuable disambiguation prior in WordNet. "bank" sense #1 (financial) has tag_count ≫ sense #6 (riverbank), so under ambiguity sense #1's Glicko-2 μ should start >1500 and sense #6's should start <1500. Instead we start them equal.
- Sense number ordering. Senses #1..#N are frequency-ordered — we should at minimum preserve this as an ordinal on `entity_sense`.
- Lexfile (noun.animal / verb.motion / …). 45 domain categories, each a junction row. This is what makes "wrench" (noun.artifact) distinguishable from "wrench" (verb.contact) at a glance.
- Word-level vs synset-level pointer distinction. Antonym `!` and derivationally_related `+` are word-level (apply to specific lemma-synset pairs, i.e. senses). We currently `continue` on word-level pointers and skip them entirely (`WordNetDecomposer.cs` line ~449).
- Verb frames — entirely ignored. These encode the syntactic realization of a verb sense and are the native way to connect WordNet verb senses to UD dependency patterns.
- Head synset pointers on adjective satellites. `s` satellite adjectives are cluster-organized around a head `a` — that cluster structure is lost.

### 2.4 OMW (Open Multilingual WordNet)

**Authority**: consortium of ~30 WordNet projects. The cross-lingual bridge.

**Actual content**:
- Per-language WordNets (JaWN, FinnWN, thai-wordnet, plWordNet, MultiWordNet, IceWordNet, sinhala-wordnet, mcr, BalkaNet, EstWN, HrvWN, …), each a mapping of lemmas in that language to Princeton synset IDs. Each alignment carries a confidence / corroboration level.
- Some OMW projects add language-local hypernym/hyponym relations (not just alignment), plus language-local glosses.

**What we extract**:
- `aligned_to_synset` edge (lemma → synset). Good. This is the critical isomorphism.
- `entity_language` junction. Good.

**What we drop**:
- Per-language hypernym graphs where they diverge from English (e.g., Japanese has classifier-based hyponym structures absent from Princeton).
- Per-language glosses.
- OMW confidence levels — these should prime `aligned_to_synset` edge Glicko-2 μ, not be uniform.

### 2.5 UD (Universal Dependencies)

**Authority**: academic consortium. Cross-lingually comparable gold-standard syntax.

**Actual content**:
- ~250 treebanks across ~130 languages. Each sentence: ID, text, comments, tokens.
- Each token: FORM, LEMMA, UPOS (17 universal POS), XPOS (language-specific POS), FEATS (morphological: `Case=Nom|Number=Sing|Gender=Masc|Tense=Past|Aspect=Perf|Mood=Ind|Voice=Act|Person=3|Degree=Pos|Definite=Def|PronType=Prs|…` — 20+ feature dimensions), HEAD (integer index of syntactic parent), DEPREL (universal dependency relation: `nsubj obj iobj obl nmod amod advmod det case mark aux cop conj cc punct root xcomp ccomp advcl acl:relcl nummod appos dislocated parataxis compound flat fixed goeswith list orphan reparandum vocative discourse expl csubj` — ~40 core relations plus language-specific subtypes).
- DEPS (enhanced dependencies — the DAG overlay with null nodes, shared heads, coreference-like passes for conjunction propagation, relcl subject sharing).
- MISC (SpaceAfter=No, typos, translation glosses, multi-word token ranges).

**What we extract**:
- `ud_sentence` and `ud_token` entities. Good.
- `has_lemma` edge (word_form → lemma). Good.
- `entity_pos` junction (UPOS). Good.
- `entity_morph_feature` junction (Case, Number, Gender, ...). Good.
- Dependency edges: `batch.AddEdge(tok.Deprel, …, [dependent, head])` — and crucially UD *does* call `refWriter.UpsertDeprelEdgeTypesAsync(deprels, ct)` to register each deprel as an edge type. This is the right pattern and the only seed decomposer doing it.
- `pattern_deprel` junction with Glicko-2.

**What we drop**:
- XPOS (language-specific POS tags like Penn Treebank NN/NNS/VB/VBD — useful for tokenizer-era model alignment).
- Enhanced dependencies (DEPS column). Conjunction propagation, relative-clause subject sharing, empty nodes. This is where UD transcends tree structure.
- Multi-word tokens (range IDs `1-2`) — Spanish `del = de + el`, German `zum = zu + dem`. We collapse these into the component tokens.
- MISC `SpaceAfter=No` — needed to reconstruct surface form. Our text AST loses whitespace placement.
- Sentence-level metadata (newdoc / newpar / sent_id / translit / text_en — the parallel-translation lines in many UD treebanks are a cross-lingual alignment resource equivalent to a mini-Tatoeba).

### 2.6 Wiktionary (wiktextract JSON dump)

**Authority**: community-curated lexicon across ~8000 languages (quality uneven, coverage vast).

**Actual content** per entry:
- Word, language, POS.
- Senses: list of definitions. Each sense has: gloss, categories (topic/register/dialect), tags (archaic, slang, rare, literary, figurative, poetic, technical, obsolete, uncountable, transitive, intransitive, ditransitive, impersonal, ergative, reflexive, pronominal, ambitransitive, …), examples (each with quote + translation + year + source), topics (mathematics, biology, law, computing, …), form_of (if this sense points to another lemma), alt_of (spelling variants), glosses in alternate registers, antonyms / synonyms / hypernyms / hyponyms / meronyms / holonyms / coordinate_terms / related / derived / instances / troponyms scoped to the sense.
- Etymology: text with structured templates ({{inh|en|enm|derke}} ← inherited from Middle English `derke`), which gives an explicit etymology graph (`en/dark ← enm/derke ← ang/deorc ← gem-pro/*derkaz`).
- Pronunciations: IPA per region (`/dɑːk/ (RP)`, `/dɑɹk/ (US)`, `/daːk/ (Australia)`, `/dɒːk/ (NZ)`); rhymes; audio file references; homophones.
- Hyphenation points.
- Inflection tables: full paradigm (case × number × gender × person × tense × mood × voice × aspect × definiteness).
- Derived terms (lemmas that contain this word), descendants (languages that inherited this word).
- Translations: per-sense mapping to words in other languages with optional gender / transliteration / notes.
- Wikidata / Wikipedia / Wikispecies / Commons links.
- Usage notes, conjugation irregularities, dialect markers, register labels.

**What we extract** (verified against `WiktionaryDecomposer` and `WiktEntry`):
- `wikt_sense`, `inflected_form`, `word_form` entities.
- `has_etymology`, `has_pronunciation`, `has_hyphenation`, `has_wikidata`, `translation_of`, `inflection_of`, `has_form` edges. But these have target `text_composition` — so we're storing etymology as a flat string, not as a derivation graph. IPA stored as flat string, not as a sequence of phoneme atoms.
- `wikt_synonym`, `wikt_antonym`, `wikt_hypernym`, `wikt_hyponym`, `wikt_meronym` — registered as edge types and emitted.

**What we drop**:
- Holonym, coordinate_term, derived, related, troponym, descendant, instance — parsed in `WiktEntry` but not emitted as edges.
- Etymology *graph*. `{{inh|en|enm|derke}}` carries explicit (language, form) pairs. We store the rendered text, not the derivation chain. The chain should be a sequence of `derived_from` edges each with an `entity_language` stamp, producing the etymology tree for every word.
- IPA as phoneme atoms. IPA is text under UCD, yes — and each IPA symbol is a codepoint with IPA-specific properties (place/manner/voicing for consonants, height/backness/rounding for vowels). The IPA Extensions block + Spacing Modifier Letters + Combining Diacritical Marks already contain this. We should attach phonetic-feature junctions to those codepoints once and then every pronunciation becomes a trajectory through phoneme atoms.
- Sense tags / register / dialect (archaic, slang, poetic, legal, medical). No `sense_register` junction exists.
- Wikidata QID. `has_wikidata` edge exists but target is text_composition (the URL). The QID should be an atom in its own right so Wikidata entities from different Wiktionary senses collapse.
- Full inflection paradigm structure (we have `inflection_of` → lemma but not the cell — case/number/person — that distinguishes this inflection from every other inflection of the same lemma).
- Usage examples with citations. Examples carry quote + year + author + work. These are themselves text_composition AST trees with metadata edges.

### 2.7 Tatoeba

**Authority**: community-contributed natural sentences, many-to-many translations, audio.

**Actual content**:
- ~12M sentences across ~400 languages.
- ~8M translation links (sentence ↔ sentence, transitively inferrable across languages).
- ~800k audio recordings with speaker metadata (accent, region).
- Sentence tags (language-learning difficulty, topic).
- Per-sentence contributors.

**What we extract**:
- `tatoeba_sentence` entity, `has_text` edge to a text_composition, `translation_link`, `recording_of`, `has_contributor`.

**What we drop**:
- Tatoeba IS natural parallel corpus. We should be able to answer "how does 'where is the bathroom' appear in 40 languages" as a single graph query — and we can, structurally, but since our Tatoeba sentence text_compositions today are NOT the same entities as any other source's text_compositions (see the documented text-decomposer anti-pattern), the cross-source collapse never happens.
- Word-level alignment across translations (not in source data — would need a downstream alignment pass, but could use UD + WordNet + OMW to bootstrap it; see §4).
- Audio as actual audio content. We record the file reference; we have not run the audio decomposer.

### 2.8 Safetensors (HuggingFace models)

**Authority**: the trained weights of existing LLMs. Not a seed — a *comparator*.

Why ingest at all in the seed phase: to have at least one model decomposed before inference, so the substrate has a point of comparison. Not to learn from the model (we do not learn from approximate statistics) but to map the model's tokenizer vocab, its architecture metadata, and the attention-head activity patterns against the gold substrate we built.

**What we extract**: tensor entities with dtype/shape/layer/model metadata, bpe_token entities with token_string text_composition, `in_vocabulary` and `co_occurrence` edges.

**What we drop** (for now, intentionally — inference-phase work):
- Actual weight-pattern analysis beyond structural catalog. SVD of attention Q/K, sparse neuron activations, superposition decomposition. This is the inference engine's job.
- Config.json as a JSON AST (currently flattened to key-value edges instead of decomposed as text).

---

## 3. Ingestion order — justified

The current phase order is `CoreAlgebra → UcdUca → Iso639 → WordNetOmw → UniversalDeps → ModelDecomp → Wiktionary → Tatoeba → TextDecomp → SignificanceField → InferenceEngine → Validation`.

The justification, phase-by-phase, with the dependency each later phase has on earlier phases:

1. **CoreAlgebra** — emits no entities. Installs the extension, warms pools. Zero dependencies.
2. **UcdUca** — *must* be first decomposer. Every subsequent decomposer's text content decomposes into codepoints, and codepoint properties (GCB/WB/SB/LB/Script) are the rules the text decomposer uses to segment. Without UCD loaded, the text decomposer cannot correctly tokenize anything.
3. **Iso639** — second, because every subsequent decomposer needs language atoms for `entity_language` junctions. WordNet needs `eng`, OMW needs 30+ languages, UD needs ~130, Wiktionary needs thousands, Tatoeba needs ~400.
4. **WordNetOmw** — the English semantic skeleton + multilingual alignment. Must come before Wiktionary because Wiktionary sense disambiguation benefits from mapping Wiktionary senses to WordNet synsets where possible (wikt_sense → aligned_to_synset). Must come before UD because UD lemmas should, when English, hit existing WordNet lemma entities (same hash).
5. **UniversalDeps** — syntax layer. Needs lemmas (some from WordNet, most newly created for non-English) and language atoms. Produces the deprel/morph_feature vocabulary that downstream phases (and the inference engine) query for syntactic patterns.
6. **ModelDecomp** — decompose the safetensors model(s). Depends on language atoms (for tokenizer language inference), and ideally on UCD (to validate tokenizer byte-pair merges against real codepoint properties). Can run earlier but placed here because it's long and must not block the seed-corpus phases.
7. **Wiktionary** — the lexicon expansion. Comes after WordNet so English senses can be linked to synsets. Comes after UD so inflection tables can validate against UD morph features. Comes after ModelDecomp so tokenizer vocab words can be cross-referenced to Wiktionary entries (BPE token `▁dark` == Wiktionary entry `dark` when the token represents a whole word).
8. **Tatoeba** — natural corpus. Comes last among sources because every Tatoeba sentence's text AST should collapse with WordNet example sentences (same gloss example text), Wiktionary citations, and UD sentences where content matches. Running Tatoeba after those phases makes the collapse happen at ingest via the same-content-same-hash invariant.
9. **TextDecomp** — *this is the critical misnamed phase*. Despite the name, this is where the text core decomposer's *output* gets its significance populated — not where text decomposition happens (text decomposition happens inline in every preceding phase via `ITextDecomposer` injection, per the centralized-pipeline invariant). Today this phase is stubbed.
10. **SignificanceField** — once all edges exist, populate their 4D trajectories and seed Glicko-2 μ/σ from frequency priors (SemCor tag_count, Wiktionary gloss count, OMW corroboration, UD treebank frequency).
11. **InferenceEngine** — bring up the engine; does not add content.
12. **Validation** — integrity checks across the 10 semantic regression cases.

### Deviation this strategy proposes

Move **UCD decomposition into the first phase proper** and add explicit sub-phases for the currently-dropped property junctions (decomposition chains, numeric values, derived binary properties, named sequences). Keep ISO 639 second. Swap **WordNetOmw ↔ UniversalDeps order** is NOT recommended — WordNet first is correct because WordNet's lemma atoms become the English backbone against which UD English treebanks collapse.

Add a seventh seed source we don't currently ingest: **CLDR** (Common Locale Data Repository). CLDR provides the locale tailoring tables that UCA needs, date/number/currency formats per locale, and character-range definitions per language (which codepoints are "Spanish letters" vs "French letters" vs "Turkish letters"). CLDR is the missing glue between UCD atoms and ISO 639 language atoms — it tells us `ñ ∈ Spanish`, `ß ∈ German` , `ı ∈ Turkish`. Without it we have language atoms and codepoint atoms but no edge saying which codepoints belong to which language.

---

## 4. How they compose — the substrate-exceeds-LLM claim made concrete

The invention only pays off if the datasets interlock. Each seed adds a dimension the others can traverse. Claim: with all eight datasets fully ingested, the substrate can answer queries no single LLM training run can answer correctly under noise, because the answers are graph lookups.

### 4.1 Disambiguation from structural lookup (no softmax)

"Show the most likely sense of 'bank' in the sentence 'I walked along the bank of the river.'"

LLM: computes attention over the whole sentence, retrieves an embedding, picks top token by softmax. Approximate.

Substrate path:
1. Text AST decomposes the sentence into word_form atoms.
2. For `bank`, lookup `entity_sense` junctions: N candidate senses with initial Glicko-2 μ primed from SemCor tag_count (financial dominant, riverbank secondary).
3. Traverse `bank`'s neighbors via UD deprel edges to `river` (`nmod:of`).
4. For each candidate sense of `bank`, compute the minimum graph distance to `river#n#1` through hypernym/meronym graph. `bank%noun:riverbank` has `part_meronym → river`. `bank%noun:financial` does not.
5. Apply Glicko-2 update: the riverbank sense wins by a large margin because its direct meronym edge beats the financial sense's prior.

Exact, reproducible, no sampling.

### 4.2 Cross-lingual identity (translation as isomorphism)

"Is 猫 a translation of cat?"

Substrate path:
1. Hash of `猫` as lemma (via text decomposer AST) is X.
2. `X` has `entity_language=jpn` junction and `aligned_to_synset → cat.n.01` edge.
3. Hash of `cat` as lemma is Y.
4. `Y` has `entity_language=eng` junction and `has_sense → cat.n.01` edge.
5. Both connect to the same synset entity. Translation confirmed.
6. The edge returned carries Glicko-2 μ reflecting OMW alignment confidence.

No parallel corpus training. No statistical alignment. The OMW corroboration *is* the knowledge.

### 4.3 Etymology chain (time dimension)

"Trace the etymology of 'dark' to its Proto-Indo-European root."

Substrate path after Wiktionary etymology graph ingestion (not yet implemented — currently stored as flat text):
1. `dark/eng` has `derived_from → derke/enm`.
2. `derke/enm` has `derived_from → deorc/ang`.
3. `deorc/ang` has `derived_from → *derkaz/gem-pro`.
4. `*derkaz/gem-pro` has `derived_from → *dʰerg-/ine-pro`.

Each node is a `lemma` entity with `entity_language` junction. Query is a graph walk. Every chain in Wiktionary is traversable.

### 4.4 Structural pattern matching across languages

"Find sentences across all languages with the same dependency structure as 'The cat sat on the mat.'"

LLM: effectively impossible — this is not what attention patterns encode for.

Substrate path:
1. Parse the query sentence via UD-trained patterns to get dependency skeleton: `root(sat) → nsubj(cat) → det(the); root(sat) → obl(mat) → case(on); root(sat) → obl(mat) → det(the)`.
2. Query `substrate.edge` filtered by `edge_type_id ∈ {nsubj, obl, det, case, root}` with matching role-positions.
3. Cluster by pattern signature (hash of ordered deprel sequence).
4. For each cluster, return one sentence per language that realizes that skeleton.

Deterministic. Cross-lingually comparable because UD deprels are universal by design.

### 4.5 Phonetic-orthographic-semantic triangulation

"Find homophones across English and French whose senses are related."

Substrate path (once IPA is decomposed into phoneme atoms, §2.6):
1. For each `wikt_sense`, walk `has_pronunciation → ipa_sequence` → sequence of phoneme atoms.
2. Group lemmas by phoneme-sequence hash across `entity_language=eng` and `entity_language=fra`.
3. For each cross-language phoneme-hash collision, check if either sense has `aligned_to_synset → same_synset` (semantic overlap).
4. Return: cognates with shared sound *and* shared meaning — an etymology prediction algorithm that isn't stochastic.

### 4.6 Model-substrate comparison

"Where does Llama-3's attention head L7H13 'overload' concept attend compared to the substrate's encoding of polysemy?"

Substrate path (inference phase):
1. `entity_sense` junctions for `overload` give N senses with Glicko-2 priors.
2. Decomposed attention-head activation pattern for L7H13 on the prompt containing `overload` gives a distribution over token positions.
3. Project each token position onto its `wikt_sense` and `entity_sense` pointer.
4. Compare: the substrate's sense-prior ranking vs. the attention head's token-attribution ranking. Divergence is an alignment diagnostic the LLM cannot provide about itself.

This is regression case #1 and depends on everything preceding being real, not stubbed.

---

## 5. What's required to make the claim true — ordered work list

These are the gaps that currently prevent the seed phase from delivering what the datasets contain. Each is actionable.

### 5.1 Edge type registration

Add to `substrate.edge_type` via a new migration (or dynamically via reference-writer calls at decomposer startup, same pattern as UD's deprel upsert):

- WordNet synset-level relations: `hypernym`, `instance_hypernym`, `hyponym`, `instance_hyponym`, `member_holonym`, `substance_holonym`, `part_holonym`, `member_meronym`, `substance_meronym`, `part_meronym`, `attribute`, `entailment`, `cause`, `also_see`, `verb_group`, `similar_to`, `participle_of`, `pertainym`, `domain_topic`, `member_topic`, `domain_region`, `member_region`, `domain_usage`, `member_usage`. Source/target = synset. Category = structural.
- WordNet word-level relations: `antonym`, `derivationally_related_form`. Source/target = word_sense (not synset). Category = structural.
- Wiktionary: add `wikt_holonym`, `wikt_coordinate`, `wikt_derived`, `wikt_related`, `wikt_troponym`, `wikt_descendant`, `wikt_instance`.
- Etymology: `derived_from` (lemma → lemma, carrying language_id on each node). Category = cross_lingual.
- CLDR: `written_in_script` (codepoint → script_atom), `used_in_locale` (codepoint → language_name). Category = unicode.
- Named sequences: `named_sequence` (codepoint → codepoint, n-ary via `edge_member` position).

### 5.2 WordNet decomposer

- Replace the word-level `continue` with real emission of `antonym` and `derivationally_related` edges between `word_sense` entities (resolve sense keys to sense entities — `word_sense` entity type already exists at id 14).
- Prime `entity_sense.mu` and `entity_pos.mu` from `tag_count`. Mapping: μ = 1500 + 80·log₁₀(1 + tag_count). tag_count=0 → μ=1500. tag_count=500 → μ≈1716.
- Store `lexfile_id` as a junction on each synset (add `entity_lexfile` junction; reference table already populated at migration 0005 line 95-100).
- Emit verb frames: attach a `frame_template` entity per frame and a `realizes_frame` edge from each verb sense.
- Preserve sense ordinal on `entity_sense` (new integer column `sense_number`).

### 5.3 Wiktionary decomposer

- Register and emit the four missing relation edge types (holonym/coordinate/derived/related) plus troponym/descendant/instance.
- Parse etymology templates (`{{inh}}`, `{{der}}`, `{{bor}}`, `{{cog}}`, `{{cal}}`, `{{compound}}`, …) and emit `derived_from` edges instead of storing rendered text.
- Decompose IPA: each IPA codepoint already exists (UCD). Attach IPA-specific phonetic-feature junctions once (one-time seed), then every pronunciation is a `linestring4d` trajectory of codepoint atoms in phoneme space.
- Sense register/tag junction: new `sense_register` reference table + `entity_register` junction for archaic/slang/rare/formal/technical/…
- Extract Wikidata QID as an atom (`wikidata_qid` entity type) instead of storing the full URL.
- Inflection cell tagging: `inflection_of` edge carries morph feature set already on the `inflected_form` via `entity_morph_feature` — audit that this is emitted.

### 5.4 UD decomposer

- Emit DEPS (enhanced dependencies) as a second edge layer with edge_type code `deps_<rel>`.
- Preserve XPOS on `ud_token` via a new `entity_xpos` junction.
- Handle multi-word tokens: emit the range token as a `word_form` entity linked to its component tokens via a `composed_of` edge.
- Preserve `SpaceAfter=No` from MISC so text reconstruction is exact.
- Ingest sentence-level `text_en` parallel translations as `translation_link` edges to the `ud_sentence`'s English rendering (this is free cross-lingual data).

### 5.5 UCD decomposer

- NFC/NFD/NFKC/NFKD decomposition edges: `decomposes_to` n-ary edge from one codepoint to a sequence of codepoints.
- Numeric_Value junction: `entity_numeric_value` with a real numeric column.
- Bidi_Class, East_Asian_Width, Decomposition_Type junctions: straightforward additions to `codepoint_property`.
- All missing derived binary properties as a bitmap column or per-property boolean junctions.
- Named sequences and emoji ZWJ sequences: emit as `lexicalized_compound` (edge type already exists per migration 0037).
- Full case mappings (Turkish, German sharp-s) as separate edges distinguished by `language_scope` role.

### 5.6 Add CLDR decomposer (new seed)

Two edges per language-codepoint pair: `written_in_script` and `used_in_locale`. CLDR `exemplars` data files give the codepoint inventories per locale. This closes the loop between UCD atoms and ISO 639 language atoms.

### 5.7 Add sense-linkage between WordNet and Wiktionary

After both have run, a linkage pass: for every `wikt_sense` whose lemma has a `has_sense → synset` edge in WordNet, emit `wikt_sense → aligned_to_synset → synset` where the gloss-similarity or tag overlap passes a threshold. This is done exactly via gloss AST comparison after the text decomposer has produced Merkle hashes for both glosses — graph-structural comparison, not embedding similarity.

### 5.8 Significance priors

`SignificanceField` phase today does nothing useful. It should:
- Prime `entity_sense.mu` from SemCor tag_count (WordNet) and Wiktionary definition order (earlier = more common).
- Prime `aligned_to_synset.mu` from OMW confidence levels.
- Prime `pattern_deprel.mu` from UD treebank frequency of each (UPOS → DEPREL → UPOS) triple.
- Prime `entity_pos.mu` per lemma from corpus frequency across UD + tag_count.

These numbers come directly from the source data. They are not statistical estimates — they are corpus counts recorded as Glicko-2 priors.

---

## 6. Regression guard

The 10 semantic regression cases in `.claude/skills/hartonomous-semantic-eval/cases.md` require:

- Case #1 (`overload` polysemy): §5.2 tag_count priming, §5.3 sense_register junction.
- Case #2 (`highrise` lexicalized compound): §5.5 named sequences + existing `lexicalized_compound` edge.
- Case #3 (`minute` time-varying POS): §5.2 sense ordinals, §5.4 XPOS/UPOS distinction.
- Case #4 (cross-lingual `cat`/`猫`/`gato`): OMW already handles; §5.6 CLDR adds script-grounding.
- Case #5 (decomposition levels): §5.1 all relation edges registered.
- Case #6 (infrastructure vs content): already correct in schema; §5.7 sense-linkage makes it queryable.
- Case #7 (identity vs reconstruction): same-content-same-hash invariant — requires the text-decomposer refactor already documented in ingestion-pipeline.md.
- Case #8 (inference vs ingestion): invariant — enforcement only.
- Case #9 (model weight sparsity): safetensors analysis pass, not seed work.
- Case #10 (terse examples as substrate probes): requires text decomposer routed everywhere.

---

## 7. What not to do

- **Do not** ingest more datasets before closing §5.1–§5.5. Coverage breadth with structural depth broken is worse than starting over.
- **Do not** store relations as flat text "for later parsing." If Wiktionary gives `{{inh|en|enm|derke}}`, emit the derived_from edge now. Storing the string defers work into an inference pass that will never match the seed phase for deterministic accuracy.
- **Do not** approximate Glicko-2 priors when exact corpus counts exist. tag_count is in the file. Use it.
- **Do not** treat Tatoeba sentences as atoms. Route through the text decomposer so they collapse with every other source's matching text.
- **Do not** re-order the phases casually. The dependency chain is real; changing it breaks same-content-same-hash at phase boundaries.
