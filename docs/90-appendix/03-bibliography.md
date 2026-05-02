# Bibliography

**Status:** Canonical
**Last verified:** 2026-04-29

External references the substrate's design and implementation depend on.

---

## Cryptographic and identity primitives

- **BLAKE3 specification.** O'Connor, Aumasson, Neves, Wilcox-O'Hearn (2020). <https://github.com/BLAKE3-team/BLAKE3-specs/blob/master/blake3.pdf>
- **BLAKE3 reference implementation.** <https://github.com/BLAKE3-team/BLAKE3>

## Rating systems

- **Glicko-2 specification.** Glickman, M.E. (2013). *Example of the Glicko-2 system*. Boston University. <https://glicko.net/glicko/glicko2.pdf>
- **Original Glicko paper.** Glickman, M.E. (1999). *Parameter estimation in large dynamic paired comparison experiments*. Applied Statistics, 48: 377–394.

## Unicode

- **Unicode Standard.** The Unicode Consortium. <https://www.unicode.org/versions/latest/>
- **UAX #29 — Unicode Text Segmentation.** <https://unicode.org/reports/tr29/>
- **UAX #10 — Unicode Collation Algorithm.** <https://unicode.org/reports/tr10/>
- **UCD (Unicode Character Database).** <https://www.unicode.org/ucd/>

## Lexical and linguistic resources

- **WordNet.** Princeton University. Miller, G.A. (1995). <https://wordnet.princeton.edu/>
- **Open Multilingual WordNet (OMW).** Bond, F. and Foster, R. (2013). <http://compling.hss.ntu.edu.sg/omw/>
- **Universal Dependencies.** Nivre, J. et al. <https://universaldependencies.org/>
- **Wiktionary** (via wiktextract / kaikki.org). <https://kaikki.org/dictionary/>
- **Tatoeba.** <https://tatoeba.org/>
- **ISO 639 (language codes).** SIL International maintains the registry. <https://iso639-3.sil.org/>

## Database and infrastructure

- **PostgreSQL 18 documentation.** <https://www.postgresql.org/docs/18/>
- **PostGIS 3.x documentation.** <https://postgis.net/docs/>
- **Tree-sitter parser generator.** <https://tree-sitter.github.io/>
- **Tree-sitter language pack** (305+ languages). <https://github.com/kreuzberg-dev/tree-sitter-language-pack>

## ML and model formats

- **safetensors specification.** HuggingFace. <https://github.com/huggingface/safetensors>, <https://huggingface.co/docs/safetensors>
- **HuggingFace transformers library.** <https://huggingface.co/docs/transformers>
- **Lottery Ticket Hypothesis.** Frankle, J. and Carbin, M. (2019). *The Lottery Ticket Hypothesis*. ICLR 2019.

## Geometry

- **Super-Fibonacci spirals.** Alexa, M. (2022). *Super-Fibonacci Spirals: Fast, Low-Discrepancy Sampling of SO(3)*. CVPR 2022.
- **Borsuk-Ulam theorem.** <https://en.wikipedia.org/wiki/Borsuk%E2%80%93Ulam_theorem>
- **Laplacian eigenmaps.** Belkin, M. and Niyogi, P. (2003). *Laplacian Eigenmaps for Dimensionality Reduction and Data Representation*. Neural Computation, 15: 1373–1396.
- **Spectra eigensolver library.** <https://spectralib.org/>
- **Eigen linear algebra library.** <https://eigen.tuxfamily.org/>

## Philosophy

- **Laplace, P. S. (1814).** *Essai philosophique sur les probabilités*. <https://en.wikipedia.org/wiki/Laplace%27s_demon>

## Foundation models referenced

The substrate ingests and references many foundation models. Authoritative sources for each:

- Llama family: Meta AI. <https://huggingface.co/meta-llama>
- Qwen family: Alibaba. <https://huggingface.co/Qwen>
- DeepSeek family: DeepSeek AI. <https://huggingface.co/deepseek-ai>
- Florence-2: Microsoft. <https://huggingface.co/microsoft/Florence-2-large>
- DETR / Conditional-DETR / RT-DETR: Various authors via HuggingFace.
- Grounding-DINO: IDEA Research. <https://huggingface.co/IDEA-Research/grounding-dino-base>
- YOLO11: Ultralytics.
- SAM-audio: Meta AI.
- Granite-Speech: IBM. <https://huggingface.co/ibm-granite/granite-speech-3.3-8b>
- Canary: NVIDIA.
- Fish-Speech: FishAudio. <https://huggingface.co/fishaudio/fish-speech-1.5>
- Music-Flamingo: NVIDIA.
- FLUX.2-dev: Black Forest Labs. <https://huggingface.co/black-forest-labs>

## Cross-references

- Vision: `00-business/00-vision.md`
- Architecture: `10-architecture/00-overview.md`
- Related work: `90-appendix/01-related-work.md`
