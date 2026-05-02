# Capability Reinvention Catalog — Every Conventional AI Capability Mapped to Substrate-Native Mechanism

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Anyone who needs to understand how the substrate replaces specific conventional AI infrastructure, and why those replacements are structural inversions rather than incremental improvements.

---

## How to read this catalog

Conventional AI is built from a fixed set of components: forward passes, training loops, attention mechanisms, embedding spaces, retrieval systems, inference servers, evaluation harnesses. Each component achieves a specific outcome — produce a token, learn from data, find similar things, ground a response, etc. Each has a known cost structure (compute, latency, memory, opaqueness, retraining requirements).

The substrate accomplishes the SAME outcomes through different mechanisms. Not "incremental improvement" — different in kind. The mechanism difference matters because each substrate replacement inherits properties (transparency, determinism, audit traceability, marginal-cost-zero reproduction) that the conventional component cannot have.

Each entry below has five fields: **conventional form**, **conventional cost/limit/opaqueness**, **substrate mechanism**, **structural reason for the difference**, **query/operation shape** in the substrate.

The catalog is not exhaustive of every AI variation that has ever existed; it covers the load-bearing capabilities of the contemporary AI stack and the substrate's replacement for each. Add entries when new capabilities emerge.

---

## I — Compute primitives

### Forward pass / inference

**Conventional form:** Token-by-token generation through a stack of transformer layers. For each token, the model performs O(N²·d) self-attention (where N = sequence length, d = embedding dim), plus FFN, layer norm, residual connections. Repeated for every token. Inference latency proportional to context length squared.

**Conventional cost/limit/opaqueness:** Quadratic compute in context length. GPU-bound. ~$0.0001–$0.10 per token at production scale. Hidden state at every layer is opaque to outside observers. Why a particular token was generated cannot be audited beyond surface heuristics.

**Substrate mechanism:** Bounded indexed A\* over typed edges. Cost per edge = 1/μ in the requested arena (Glicko-2 rating). Bulk-fetch SPI per popped node — one SQL query returns all candidate successors with their significance, joined to provenance, joined to edge type, filtered by recipe. K nodes visited × log N index depth = O(K log N) per inference. K is bounded by cost budget; log N is ~30 even at billion-edge scale.

**Structural reason:** Inference replaces probability sampling with edge traversal. There is no probability distribution over a vocabulary; there is a graph of typed edges between content-addressed entities, weighted by accumulated cross-source attestation. The traversal is a sequence of indexed lookups, not a sequence of matrix multiplications. CPU-bound. <10ms warm-cache target. Path is the explanation.

**Query shape:**
```sql
SELECT * FROM hartonomous.inference.converse(
    prompt          => $1,
    arena_recipe    => $2,           -- per-hop filter recipe (JSONB)
    max_cost        => 1000.0,
    max_depth       => 10,
    explanation     => true
);
```

---

### Training / pretraining

**Conventional form:** Stochastic gradient descent over hundreds of billions of training tokens. Initialize random weights. Forward pass on a batch. Compute loss. Backpropagate gradients. Update weights. Repeat for billions of iterations across a distributed GPU cluster.

**Conventional cost/limit/opaqueness:** Frontier-model pretraining costs $10M–$100M+ in compute. Weeks to months of cluster time. Produces opaque weight matrices. Convergence is hoped-for, not guaranteed. Hyperparameter search is trial and error. The artifact (a weight file) cannot be inspected at the level of individual training examples.

**Substrate mechanism:** Ingestion. Decomposers stream input bytes through tree-sitter (or Kaitai, for binary) → typed AST → substrate compositions and edges with provenance. Glicko-2 ratings initialize from `provenance.initial_mu`. No gradient. No loss function. No backpropagation. Training data IS the substrate's content; the substrate's "training" is its ingestion phase.

**Structural reason:** Training is replaced by ingestion because the substrate's "knowledge" is content-addressed edges with arena ratings, not weight matrices. Content from any source converges via BLAKE3 identity. Cross-source attestation accumulates evidence on shared edge rows. The substrate doesn't optimize a loss; it accumulates structured observations and lets arena dynamics resolve disagreements.

**Query shape:**
```sql
-- Training is happening continuously; this surfaces what's been ingested
SELECT * FROM hartonomous.substrate.ingestion_status();
```

---

### Backpropagation

**Conventional form:** Compute gradient of loss with respect to every weight via the chain rule. Update weights against gradient with learning-rate-scaled step. Required for gradient descent; the only known way to optimize hundreds of billions of parameters jointly.

**Conventional cost/limit/opaqueness:** Memory-intensive (must store activations from forward pass for the backward pass). GPU-required for production scale. Numerical precision issues at extreme depth. Catastrophic forgetting — new training data can destroy capabilities the model previously had. Backprop produces no audit trail of which training example influenced which weight by how much.

**Substrate mechanism:** Glicko-2 outcome events. When inference produces an outcome (user accepts, downstream task succeeds), comparison events fire between selected and rejected path edges. Glicko-2 update changes (μ, σ, volatility, games) for each edge in each relevant arena. Winner edges' μ rises, σ falls. Loser edges' μ falls, σ rises.

**Structural reason:** The substrate doesn't optimize across all parameters jointly. Each edge's significance updates locally per outcome event, in the arenas the inference query specified. There's no global gradient; there's a local-update rule for each rated edge. Catastrophic forgetting is structurally impossible because edge identities are content-addressed and don't get overwritten — only their significance per arena evolves.

**Query shape:**
```sql
SELECT hartonomous.inference.outcome(
    response_entity_id  => $1,
    outcome             => 'accept',     -- or 'reject', 'partial'
    arenas              => NULL          -- NULL = update all consulted arenas
);
```

---

### Fine-tuning

**Conventional form:** Take pretrained model. Continue training on a smaller, domain-specific dataset with a lower learning rate. Hope model learns new domain without destroying base capabilities. Risk catastrophic forgetting. Risk overfitting. Requires GPU compute proportional to dataset size and model size.

**Conventional cost/limit/opaqueness:** $10K–$1M+ per fine-tune for a frontier-class model. Hours to days of GPU time. Result is a new opaque weight file. Whether the fine-tune improved or degraded specific capabilities can only be evaluated via downstream benchmarks; cannot be inspected directly.

**Substrate mechanism:** Ingest customer's domain corpus into the substrate. Existing edges accumulate new attestations from domain content via Glicko in arena dynamics. New entities are added where the domain corpus has content not previously in substrate. No iterative training; just ingestion. Re-export the customer's model from refreshed substrate state.

**Structural reason:** Fine-tuning works in conventional AI because gradient descent can shift weights toward domain-specific patterns. The substrate's equivalent — accumulating new attestations on existing edges — happens at INSERT time via the same content-addressing that handled the original ingestion. Domain specialization is a function of which provenance sources contribute attestations, not of an optimization procedure.

**Query shape:**
```sql
-- Customer's domain corpus is ingested as a separate provenance:
SELECT hartonomous.ingestion.run_decomposer(
    decomposer       => 'text_corpus',
    source_path      => '/customer-data/domain-X/',
    provenance       => 'customer_X:domain'
);
-- Then re-export their model from updated substrate state:
SELECT hartonomous.recompose.refine_model(
    source_provenance  => 'huggingface_model:customer-X-base',
    output_path        => '/exports/customer-X-refined.safetensors'
);
```

---

### Distillation

**Conventional form:** Train a smaller "student" model to mimic the outputs of a larger "teacher" model. Run teacher inference on training data; student trains to match teacher's softmax distributions. Student typically retains 90–95% of teacher capability at fraction of inference cost.

**Conventional cost/limit/opaqueness:** Requires GPU training of student (multi-day for production-scale models). Architectural compatibility constraints (student tokenizer must match teacher; both must share input/output spaces). Cannot easily distill across architectures (encoder→decoder, dense→MoE). Student is a NEW model with its own opaque weights.

**Substrate mechanism:** SELECT-with-significance-threshold against substrate edges, projected onto target architecture spec via the recomposer. Below-threshold edges become zeros (sparse). Above-threshold edges become weights. Output is a new safetensors file with the customer's specified architecture, populated from substrate state.

**Structural reason:** The substrate's edges are architecture-agnostic. A `transformation` edge between two vocabulary entries doesn't care if it's projected onto a 7B-dense FFN or a 30B-MoE expert. The recomposer's projection function reads target architecture, walks substrate edges, fills tensors. No training. No GPU. No tokenizer compatibility constraint (substrate's content-addressed identity makes tokenizer compositions converge regardless of model). Mitosis economics: parent substrate doesn't deplete; daughter models bud off.

**Query shape:**
```sql
SELECT hartonomous.recompose.distill_to_safetensors(
    target_architecture  => 'decoder_transformer',
    target_shape         => '{"layers":32,"hidden":4096,"heads":32,"mlp":11008,"vocab":152064}'::jsonb,
    arena_filter         => ARRAY['semantic_relevance','syntactic_role_fitness'],
    significance_floor   => 0.6,
    tokenizer_source     => 'qwen-2.5-tokenizer',
    output_path          => '/exports/laplace-linguistics-7b'
);
```

---

### RLHF / preference learning

**Conventional form:** Train reward model from human preference rankings. Use PPO or DPO to fine-tune base model against reward model. Iteratively repeat. Adjusts base model toward "helpful, harmless, honest" or whatever objective the reward model encoded.

**Conventional cost/limit/opaqueness:** Requires preference data collection (expensive human labeling). Reward model training. PPO/DPO training. Each step is full-model GPU training. Risk of reward hacking (model finds adversarial strategies that game the reward without solving the underlying task). Reward model itself is an opaque approximation of human preference.

**Substrate mechanism:** Outcome events on inference paths. When users accept or reject substrate-generated responses, comparison events fire between path edges. Glicko updates per arena. The "preference signal" is the outcome; arena dynamics resolve which paths win in which contexts. No reward model; the arena IS the reward dynamics.

**Structural reason:** RLHF in conventional AI is a way to push opaque weights toward human-aligned behavior. The substrate's equivalent is built into the same Glicko machinery that handles ordinary outcome events. There's no separate "alignment phase" — every inference outcome is a Glicko update, and arena weights track which paths produce desired outcomes for which customers in which contexts.

**Query shape:**
```sql
-- Per-customer preference accumulates in a customer-scoped arena:
SELECT hartonomous.inference.outcome(
    response_entity_id  => $1,
    outcome             => 'accept',
    arenas              => ARRAY['customer_X:preferred_style', 'corroboration_strength']
);
```

---

## II — Mechanism analogues (per-layer / per-component)

### Attention mechanism (Q, K, V, output projection)

**Conventional form:** Per layer, project input embeddings through query (Q), key (K), and value (V) matrices. Compute attention scores as softmax(QK^T / √d). Weight values by attention scores. Project through output matrix. Repeats per attention head, per layer.

**Conventional cost/limit/opaqueness:** O(N²·d) compute per layer. KV-cache memory grows with context. Attention weights are computed at inference; not pre-stored. Why a particular token attended to particular previous tokens is opaque past surface heuristics.

**Substrate mechanism:** `beaten_path` edges between entities. When a model is ingested, its attention patterns at each (layer, role, head) decompose into typed edges with significance. Inference traversal walks these edges; the edges that fire are the substrate's analogue of attention "lighting up."

**Structural reason:** Attention in conventional AI is computed per-query; the substrate's equivalent is pre-computed at ingestion and stored as edges. Attention scores become edge significance per arena. Per-layer / per-head structure is preserved as edge metadata (layer index, role, head index). The substrate's per-hop filtering allows queries to consult specific layers, specific heads, specific source models — granularity conventional inference cannot offer.

**Query shape:**
```sql
SELECT * FROM substrate.edge e
  JOIN ref.edge_type et ON et.id = e.edge_type_id
  JOIN substrate.edge_significance s
    ON (s.edge_type_id, s.edge_hash) = (e.edge_type_id, e.hash)
 WHERE et.code = 'beaten_path'
   AND s.context_type_id = $arena_id
   AND e.metadata->>'layer' = '15'
   AND s.mu > 1700
 ORDER BY s.mu DESC;
```

---

### Feed-forward network (FFN) layers

**Conventional form:** Per layer, after attention, project through MLP: input → gate × up-projection → activation → down-projection → output. Three linear projections per layer. The "knowledge layer" of the transformer; estimated to hold the bulk of factual content.

**Conventional cost/limit/opaqueness:** Largest fraction of model parameters (~2/3 of total). Per-layer compute. Why specific facts are encoded in specific positions is uninspectable.

**Substrate mechanism:** `transformation` edges. Each tensor element at (layer, role, row, col) of an FFN matrix becomes a typed edge with significance, stored as edge state in the substrate. Substrate inference walks these edges instead of multiplying matrices.

**Structural reason:** FFN's purpose is to apply learned per-position transformations. The substrate stores those transformations as content-addressed edges with provenance per source model. Cross-model corroboration through arenas resolves which FFN-derived patterns are consensus vs noise. Recomposition projects substrate edges back onto FFN matrix shape for safetensors export — and below-threshold positions go to zero (sparser than original).

**Query shape:**
```sql
SELECT * FROM substrate.edge e
  JOIN ref.edge_type et ON et.id = e.edge_type_id
 WHERE et.code = 'transformation'
   AND e.metadata->>'role' IN ('ffn_up','ffn_gate','ffn_down')
   AND e.metadata->>'layer' = $L;
```

---

### Embedding / token vectors

**Conventional form:** Input tokens lookup vectors in an embedding matrix. Each vocabulary token has a learned d-dimensional vector. Used to convert discrete tokens into the model's continuous representation space.

**Conventional cost/limit/opaqueness:** Embeddings encode learned similarity structure. Their geometry is model-specific (Llama's "cat" vector is in a different space than Qwen's). Cross-model comparison requires alignment procedures. Embeddings cannot be directly inspected for what makes two tokens similar.

**Substrate mechanism:** Two-track storage:
1. **Track 1 — Firefly:** Per-model 4D point in `physicality(physicality_type=embedding_firefly)` via Laplacian eigenmap + Gram-Schmidt + L2 norm. Each model contributes one firefly per token. Cross-model comparison is Hausdorff over firefly clouds for the same token entity.
2. **Native-dim preservation:** The original n-D embedding row is also stored as physicality (physicality_type=embedding_native_<model_id>) so the recomposer can reconstruct embedding tensors at the source model's dimensionality.

**Structural reason:** Conventional embeddings are model-specific. The substrate's content-addressed token entities (the same `cat` composition is the same row regardless of source) make cross-model embeddings co-locate. The 4D firefly is the substrate's interpretable cross-model representation; the native-dim embedding is preserved for distillation use. Embedding similarity becomes 4D geometric query (Fréchet, Hausdorff) over deterministic positions.

**Query shape:**
```sql
SELECT * FROM hartonomous.compare.cross_model_consensus('cat');
-- Returns centroid, dispersion, n_models, agreement_score over all models'
-- fireflies for the cat composition entity.
```

---

### Layer normalization / RMSNorm

**Conventional form:** Per-layer normalization (subtract mean, divide by stddev for LayerNorm; root-mean-square normalize for RMSNorm). Applied between sub-layers. Stabilizes training; preserves gradient magnitude.

**Conventional cost/limit/opaqueness:** A small parameter set (one scale per hidden dim per layer) but architecturally critical. Why specific scales were learned is opaque.

**Substrate mechanism:** Per-(layer, role) `layer_norm` edges with stored scale parameters. Recomposer reads these and writes them into the output safetensors; if the source model used RMSNorm, the recomposer preserves the choice.

**Structural reason:** Norms are small, deterministic, architecture-fixed parameters. The substrate stores them as substrate edges per layer per source model. Cross-model corroboration generally doesn't apply (norm scales are model-specific). Recomposition reads them back at export time.

**Query shape:** Layer norms are architectural metadata; rarely queried directly except by the recomposer at export time.

---

### Position encoding (RoPE, ALiBi, learned, sinusoidal)

**Conventional form:** Inject position information into input embeddings or attention computation. RoPE rotates Q, K vectors per position. ALiBi adds attention bias. Learned position embeddings have a separate learnable vector per position. Sinusoidal uses fixed sin/cos waves.

**Conventional cost/limit/opaqueness:** Each scheme has different extension behavior (RoPE extends gracefully; learned doesn't). Choice is architectural.

**Substrate mechanism:** Position-modulation is a property of the architecture spec the recomposer reads. Substrate edges don't store position-specific values; the recomposer applies the target architecture's chosen scheme (RoPE base θ, ALiBi slopes, etc.) at recompose time.

**Structural reason:** Position encoding is an architectural choice, not learned content. The substrate represents it as architecture metadata, not as edges. Recomposition applies the chosen scheme at output time.

---

### Mixture of Experts (MoE)

**Conventional form:** Replace single FFN per layer with N expert FFNs plus a router. Router (small NN) decides which top-k experts process each token. Increases capacity without proportional inference cost since only k of N experts fire per token.

**Conventional cost/limit/opaqueness:** Training is harder (router learning is unstable). Upcycling from dense (clone-then-fine-tune) requires additional GPU time. Heterogeneous experts (different sizes) are not supported by standard methods. Why a particular token is routed to particular experts is opaque.

**Substrate mechanism:** SQL clustering of substrate edges by domain → per-cluster expert recomposition. Each cluster's edges become one expert's FFN matrix. The router is either a SQL function (deterministic, indexed lookup) for substrate-runtime deployment, or a small materialized lookup tensor for safetensors deployment to vLLM/llama.cpp.

**Structural reason:** Substrate edges have arena memberships. Clustering edges by their highest-μ arena produces domain-coherent specialists from the start (Python edges to Python expert, math edges to math expert). Heterogeneous experts (different sizes per cluster) fall out for free because the recomposer's projection onto each expert is independent. Conventional MoE upcycling forces uniform expert shape; substrate-derived MoE doesn't.

**Query shape:**
```sql
SELECT hartonomous.recompose.distill_to_safetensors(
    target_architecture  => 'mixture_of_experts',
    target_shape         => '{"num_experts":8,"top_k":2,"experts":[
                              {"size":"4096x16384","arena":"python_idiom"},
                              {"size":"4096x4096","arena":"poetry"},
                              ...
                             ]}'::jsonb,
    output_path          => '/exports/laplace-coder-moe-heterogeneous'
);
```

---

### LoRA / adapters

**Conventional form:** Train a low-rank update (delta) on top of frozen base model weights. Add LoRA delta to base at inference. Allows per-domain specialization without full fine-tuning cost.

**Conventional cost/limit/opaqueness:** Requires GPU training for the LoRA delta. Multiple LoRAs cannot be simultaneously composed without merging. The delta is a new opaque weight tensor.

**Substrate mechanism:** Adapter ingestion. The base model's edges have provenance `huggingface_model:base-model`. The LoRA delta's edges are ingested with sub-provenance `huggingface_model:base-model:adapter:lora-name`. At recompose time, the recomposer reads BOTH base provenance edges AND adapter sub-provenance edges; their combination is the effective weight.

**Structural reason:** Adapters in conventional AI are weight-additive; the substrate's equivalent is provenance-additive. Multiple adapters compose at substrate level (read base + adapter-1 + adapter-2 edges, sum significance). No GPU training needed for new adapters; ingest customer's adapter delta directly.

**Query shape:**
```sql
-- Recompose with a specific adapter applied:
SELECT hartonomous.recompose.refine_model(
    source_provenance    => 'huggingface_model:granite-speech-3.3-8b',
    adapter_provenances  => ARRAY['huggingface_model:granite-speech-3.3-8b:adapter:medical'],
    output_path          => '/exports/granite-medical.safetensors'
);
```

---

### Quantization (AWQ, GGUF, INT8/4)

**Conventional form:** Compress weight matrices from FP16/BF16 to INT8/INT4 representations. Trade precision for size and inference speed. Methods: AWQ (activation-aware), GGUF (k-quants for llama.cpp), GPTQ, etc.

**Conventional cost/limit/opaqueness:** Lossy compression. Quality regressions are model-specific and hard to predict pre-deployment. Once quantized, original precision is lost.

**Substrate mechanism:** Substrate doesn't quantize. It uses lossless decode (BF16 → F32 → F64 as needed). Sparsity comes from significance threshold (honest absence of attestation) rather than precision compression. For producing quantized exports, the recomposer can quantize at export time post-projection — but the substrate state is precision-preserving.

**Structural reason:** Quantization in conventional AI is a deployment optimization for models trained in higher precision. Substrate preserves precision because audit and provenance require it; quantization is a choice the customer can make on their export, not a substrate-internal optimization.

**Query shape:** N/A — substrate doesn't store quantized state. Quantization, if needed, is a flag on the export operation.

---

### Pruning

**Conventional form:** Remove weights below a threshold (magnitude pruning) or based on activation patterns (lottery ticket). Reduces model size; sometimes improves generalization.

**Conventional cost/limit/opaqueness:** Pruned weights cannot be recovered. Iterative prune-fine-tune cycles. Pruning aggressively without retraining causes quality collapse.

**Substrate mechanism:** `DELETE WHERE significance < threshold` or threshold filtering at recompose time (below-threshold becomes zero in output). Reversible because substrate retains the underlying provenance — re-ingesting the source produces the edge with original significance.

**Structural reason:** Pruning conventional models is destructive. Substrate "pruning" is a query: filter low-significance edges out of an export. The substrate's underlying state is unchanged. A future export with a lower threshold recovers what was filtered.

**Query shape:**
```sql
-- Policy-governed pruning:
SELECT hartonomous.substrate.prune_low_significance(
    arena              => 'semantic_relevance',
    threshold          => 0.3,
    min_games          => 100        -- only prune well-attested-as-low edges
);
-- Or just filter at export:
SELECT hartonomous.recompose.refine_model(
    source_provenance   => 'huggingface_model:llama4-maverick',
    significance_floor  => 0.7,        -- aggressive pruning
    output_path         => '/exports/llama4-pruned.safetensors'
);
```

---

## III — Cross-cutting capabilities

### Multi-modality (vision-language, audio-language)

**Conventional form:** Separate encoders per modality (vision encoder, audio encoder, text encoder). Connect via projection layers that map one modality's embeddings into another's space. Cross-modal alignment is learned through paired training data (image-caption pairs, audio-transcript pairs).

**Conventional cost/limit/opaqueness:** Each projection is a trained approximation. Cross-modal understanding is bounded by paired data quality. Adding a new modality requires new training.

**Substrate mechanism:** Cross-modal edges between modality-specific entities. `recording_of` edges link audio_recording entities to text_composition entities (Tatoeba pattern). `depicts` edges link pixel_region entities to concept entities (Visual Genome scene graphs). `has_caption` links image entities to text entities. The substrate stores these as first-class typed edges; cross-modal traversal is graph walk, not projection-layer arithmetic.

**Structural reason:** Modalities are connected via explicit content-addressed edges from corpora that paired them (Tatoeba audio recordings, Visual Genome captions, Florence-2 vision-language patterns). New modalities are added by ingesting datasets that bridge them; no projection-layer training.

**Query shape:**
```sql
-- Audio prompt → text response:
SELECT hartonomous.inference.converse_multimodal(
    audio_input  => $1,
    target_modality => 'text',
    arena_recipe => '{"hops":[
                       {"edge_types":["recording_of"]},
                       {"edge_types":["has_text"]},
                       ...]}'::jsonb
);
```

---

### Cross-lingual transfer

**Conventional form:** Train multilingual models on parallel data (paired source-target sentences). Hope model develops language-agnostic representations. Quality varies dramatically by language pair.

**Conventional cost/limit/opaqueness:** Low-resource languages underperform. Translation quality is statistical, not structural.

**Substrate mechanism:** OMW-aligned synsets give every language a content-addressed concept entity. `aligned_to_synset` edges from per-language lemmas to shared synsets enable graph traversal as translation: lemma in source language → synset → reverse-aligned lemma in target language. Tatoeba's `translation_link` edges add sentence-level pairings.

**Structural reason:** Translation is graph traversal in the `translation_quality` arena, not an end-to-end learned mapping. Adding a new language is ingesting OMW (or equivalent) for that language, not retraining a multilingual model.

**Query shape:**
```sql
SELECT hartonomous.transform.translate(
    text         => 'Hello world',
    target_lang  => 'es',
    arena_recipe => '{"hops":[
                       {"edge_types":["aligned_to_synset"]},
                       {"language_filter":"es"}
                      ]}'::jsonb
);
```

---

### Few-shot / in-context learning

**Conventional form:** Provide examples in prompt; model induces pattern from examples and applies to query. Capability emerges from large-scale pretraining.

**Conventional cost/limit/opaqueness:** Limited by context window. Why model generalizes from examples is opaque. Performance varies with example ordering and phrasing.

**Substrate mechanism:** Examples and query are all decomposed into substrate state with `user_session` provenance. Inference traversal seeds from prompt entities INCLUDING the example entities. Per-hop filtering can up-weight `corroboration_strength` arena (where example-attested patterns reinforce). The "shots" are session-scoped substrate state, not transient context.

**Structural reason:** Few-shot in conventional AI works because attention can attend to in-context examples. The substrate's equivalent is that examples are graph nodes the traversal naturally visits. No context window limit; examples persist for the session.

**Query shape:**
```sql
SELECT hartonomous.inference.converse(
    prompt   => $query,
    context  => $examples,         -- prior session compositions
    arena_recipe => '{...,"corroboration_weight":1.5}'::jsonb
);
```

---

### Chain of thought / reasoning

**Conventional form:** Prompt model to "think step by step." Model generates intermediate reasoning before final answer. Improves accuracy on reasoning tasks. Variants: ToT (tree), GoT (graph), self-consistency, reflection.

**Conventional cost/limit/opaqueness:** Tokens-of-thought consume context budget. Reasoning is generated text; correctness is not enforced. Self-consistency is post-hoc voting.

**Substrate mechanism:** The traversal path IS the chain of thought. Each hop is an explicit reasoning step over typed edges with provenance. Tree-of-thought emerges from running multiple recipes and comparing top-k paths. Self-consistency is querying multiple arenas in parallel and aggregating. Reflexion is using a prior path as context for a next traversal in a `reflexion` arena that weights revision.

**Structural reason:** Chain of thought in conventional AI is generation; the substrate's traversal is structured by construction. Every intermediate step is auditable; every reasoning step has provenance. ToT/GoT/self-consistency are query patterns, not separate algorithms.

**Query shape:**
```sql
-- Tree of thought: top-K paths, multiple recipes:
SELECT * FROM hartonomous.inference.converse(
    prompt    => $1,
    max_paths => 10,
    explanation => true
);
```

---

### Tool use / function calling

**Conventional form:** Train model to emit JSON describing tool calls. Application code parses output, calls tool, feeds result back into context. Repeat.

**Conventional cost/limit/opaqueness:** Tool definition lives outside model. Model decides when to call based on prompt patterns. Multi-step tool chains can drift.

**Substrate mechanism:** Cognitive functions are SQL functions. Inference can call them mid-traversal: a recipe can specify "at hop N, invoke `hartonomous.cognitive.compute_quantity()` and use its output as the next hop's seed." Tool use becomes nested SQL function composition.

**Structural reason:** Tool use in conventional AI is a coordination protocol between an opaque model and external code. The substrate is itself a queryable database with hundreds of functions; the "tool" is just another SQL function. The recipe DSL allows in-traversal function dispatch.

**Query shape:**
```sql
-- Recipe specifies a mid-traversal function call:
SELECT hartonomous.inference.converse(
    prompt   => $1,
    arena_recipe => '{
       "hops":[
         {"action":"traverse"},
         {"action":"invoke","function":"hartonomous.compute.unit_conversion",
          "args":{"from":"node.surface_form","to":"target_unit"}},
         {"action":"traverse"}
       ]}'::jsonb
);
```

---

### RAG (retrieval-augmented generation)

**Conventional form:** Embed query and corpus chunks. Retrieve top-k similar chunks via vector DB. Concatenate retrieved chunks into model context. Model generates response from augmented context.

**Conventional cost/limit/opaqueness:** Retrieval quality is bounded by embedding similarity. Chunking artifacts. Limited by context window. Two-stage architecture (retrieve + generate) with separate failure modes.

**Substrate mechanism:** Inference IS the retrieval. Traversal walks edges from prompt entities; the path traversed both retrieves and generates simultaneously. There is no separate retrieval step.

**Structural reason:** RAG exists because conventional models can't directly query their training data. The substrate's training data IS its queryable state; "retrieval" is just edge traversal, and "generation" is recomposition from the traversed entities.

**Query shape:**
```sql
-- Same converse call. There's no retrieve-then-generate split.
SELECT * FROM hartonomous.inference.converse($prompt);
```

---

### Vector databases / similarity search

**Conventional form:** Store embedding vectors. Query via approximate nearest-neighbor (HNSW, IVF, LSH). Return top-k similar vectors.

**Conventional cost/limit/opaqueness:** Approximate (recall < 100%). Index build is expensive. Updating requires rebuild. Distance is in embedding space, not in real semantic space.

**Substrate mechanism:** No vector database. Two replacements:
1. **Content-addressed identity** for exact equality (BLAKE3 hash). Most "search for the same content" queries are O(1) hash lookup.
2. **Exact 4D Fréchet/Hausdorff** for genuine similarity (`hartonomous.geometric.frechet`). 4D operators on stored trajectories. GiST-indexed.

**Structural reason:** Vector DBs exist because embeddings are model-specific and ANN is the cheapest approximation of similarity in those spaces. The substrate's geometry is deterministic (codepoints on S³ via UCA Super-Fibonacci) and exact (4D operators on stored linestrings). Approximation is unnecessary.

**Query shape:**
```sql
-- Geometric similarity over composition trajectories:
SELECT entity_hash, hartonomous.geometric.frechet(
    p.linestring4d,
    (SELECT linestring4d FROM substrate.physicality WHERE entity_hash = $target)
) AS distance
FROM substrate.physicality p
WHERE p.physicality_type_id = (SELECT id FROM ref.physicality_type WHERE code='composition_trajectory')
ORDER BY distance ASC LIMIT 20;
```

---

### Knowledge graphs + LLM

**Conventional form:** Store triples in a KG (Neo4j, etc.). Use LLM as natural-language interface. LLM translates queries to SPARQL or graph traversals.

**Conventional cost/limit/opaqueness:** Two systems with separate failure modes. KG manually curated. LLM translation can hallucinate.

**Substrate mechanism:** The substrate IS the knowledge graph AND the inference engine. Triples are typed edges; traversal is A\* with arena-rated significance; natural language is decomposed to substrate state and inference traverses from the prompt's entities.

**Structural reason:** KG+LLM is a two-system architecture because conventional LLMs cannot directly query structured knowledge. The substrate unifies both into one schema with one query language.

**Query shape:** Same as inference; there's no separate KG query path.

---

### Continuous learning / online fine-tuning

**Conventional form:** Periodically re-fine-tune model on new data. Risk catastrophic forgetting; risk drift; requires re-validation per cycle.

**Conventional cost/limit/opaqueness:** Each cycle is a training run. Models can degrade unpredictably.

**Substrate mechanism:** Continuous ingestion. New data is ingested as new attestations; existing edges accumulate; new entities are added where novel content arrives. Glicko updates on outcome events refine arena weights. No separate cycle; substrate state monotonically improves.

**Structural reason:** Continuous learning in conventional AI is hard because gradient methods are destructive (each update can damage prior knowledge). The substrate's content-addressed edges don't get destroyed; they accumulate evidence and update significance.

**Query shape:** Continuous ingestion; surfaceable via:
```sql
SELECT * FROM monitor.ingestion_progress WHERE last_progress > now() - interval '1 day';
```

---

### Model merging (SOUP, TIES, DARE)

**Conventional form:** Combine multiple fine-tuned models' weights via interpolation, sparse averaging, or task-arithmetic operations. Produces a single combined model.

**Conventional cost/limit/opaqueness:** Architectural compatibility required. Choice of weights / sparsification heuristics is ad hoc.

**Substrate mechanism:** Cross-source attestation in arenas. Multiple source models' edges converge on the same entity rows; Glicko in arenas resolves the contest. The "merged model" is what the recomposer produces from this joint state.

**Structural reason:** Model merging in conventional AI is constrained because each model has its own opaque weight matrix. The substrate's content-addressed edges from different models naturally co-locate; merging is the default substrate state, not a separate procedure.

**Query shape:** Recompose with multi-source provenance:
```sql
SELECT hartonomous.recompose.distill_to_safetensors(
    target_architecture  => 'decoder_transformer',
    arena_filter         => ARRAY['model_consensus'],
    significance_floor   => 0.7
    -- Implicitly aggregates across all model provenances
);
```

---

### Ensemble methods

**Conventional form:** Run multiple models in parallel; combine outputs via voting / averaging / stacking. Higher quality than any single model; multiplied inference cost.

**Conventional cost/limit/opaqueness:** N× the inference compute for an N-model ensemble.

**Substrate mechanism:** Cross-source consensus in arenas. The substrate's `corroboration_strength` arena rises naturally for edges multiple models agreed on. Inference in arenas weighted toward corroboration is "ensemble-distilled" by construction.

**Structural reason:** Ensembles work in conventional AI because diverse models make diverse errors. The substrate captures this directly: multiple models' attestations converge on shared edges, and arena dynamics resolve disagreement. There's no per-query N× cost; the consensus state is pre-computed via accumulated Glicko updates.

**Query shape:**
```sql
SELECT * FROM hartonomous.inference.converse(
    prompt       => $1,
    arena_recipe => '{"hops":[{"arenas":["corroboration_strength"]}]}'::jsonb
);
```

---

### Neural architecture search (NAS)

**Conventional form:** Search over architecture variants (depth, width, attention configs, etc.) to find one that performs best on a task. Expensive; usually requires train-and-eval per candidate.

**Conventional cost/limit/opaqueness:** Each candidate requires training; total cost = N × single-model cost.

**Substrate mechanism:** Recompose to multiple architectures from the same substrate state. Each recomposition is I/O cost; benchmark each output; pick the winning shape.

**Structural reason:** NAS in conventional AI is expensive because each architectural variant requires its own training run. The substrate's mitosis economics make per-architecture cost approach zero; the search becomes "spawn 10 daughters with different shapes, benchmark them, pick the best."

**Query shape:**
```sql
-- Loop in client code:
FOR shape IN candidate_shapes:
    safetensors = hartonomous.recompose.distill_to_safetensors(target_shape => shape, ...);
    score = run_benchmark(safetensors);
```

---

## IV — Inference operational concerns

### Token sampling / generation

**Conventional form:** From softmax distribution over vocabulary, sample next token. Temperature, top-k, top-p, beam search are sampling strategies.

**Conventional cost/limit/opaqueness:** Stochastic. Same prompt produces different outputs (sometimes desirable, sometimes problematic).

**Substrate mechanism:** Walk substrate state along the selected path; recomposer assembles output bytes. Path selection is deterministic given substrate state + recipe + significance threshold. Variation, if desired, comes from selecting top-K paths or running with explicit diversity bonus.

**Structural reason:** Sampling in conventional AI exists because models output distributions, not answers. The substrate outputs paths with definite vertices; randomness is opt-in, not default.

**Query shape:** Standard inference; for diversity request top-K:
```sql
SELECT * FROM hartonomous.inference.converse(prompt => $1, max_paths => 5);
```

---

### Beam search / diverse sampling

**Conventional form:** Track top-K candidate beams during generation. Each beam grows token-by-token. At each step, expand all beams, score, and keep top-K.

**Conventional cost/limit/opaqueness:** K× the per-token compute.

**Substrate mechanism:** A\* with `max_paths > 1` keeps top-K paths per traversal. Path selection scores by path-significance.

**Structural reason:** Beam search in conventional AI is needed because greedy generation is brittle. The substrate's A\* explicitly returns multiple paths and lets the recipe choose how to aggregate.

**Query shape:** `max_paths => 5` parameter to inference.

---

### Temperature / nucleus (top-p) sampling

**Conventional form:** Modulate softmax to control output diversity. Temperature 0 = greedy; high temperature = more random.

**Conventional cost/limit/opaqueness:** A scalar knob; effects are model-dependent.

**Substrate mechanism:** Significance-floor variation per hop. Lower floor admits more candidate paths (higher diversity); higher floor admits only highest-μ paths (more deterministic).

**Structural reason:** Temperature in conventional AI shapes a probability distribution. The substrate's analogue shapes the candidate path set.

**Query shape:** Recipe parameter:
```sql
-- "creative" recipe with low floor:
'{"hops":[{"significance_floor":0.3}]}'
-- "deterministic" recipe with high floor:
'{"hops":[{"significance_floor":0.85}]}'
```

---

### Hallucination mitigation

**Conventional form:** RLHF, CAI, retrieval augmentation, guardrail models. Layer post-hoc mechanisms to suppress confident-but-wrong outputs.

**Conventional cost/limit/opaqueness:** Each mitigation is a patch on a fundamental property: transformers sample from probability distributions and have no mechanism to distinguish probable from true. Mitigations reduce frequency but cannot eliminate.

**Substrate mechanism:** Honest abstention. If no edge exists or no edge above significance threshold reaches the target, the substrate returns `{paths: [], frayed_edges: [...]}` rather than inventing. There is no probability distribution to sample from.

**Structural reason:** Hallucination requires a generative mechanism that can produce content with no grounding. The substrate has no such mechanism. Output comes from edges; if no edge, no output.

**Query shape:** Same inference call; abstention is a possible response shape.

---

### Catastrophic forgetting

**Conventional form:** When fine-tuning or continual learning, new training can destroy capabilities the model previously had. Mitigations include EWC, replay buffers, parameter isolation.

**Conventional cost/limit/opaqueness:** Mitigations are complex; complete prevention is structurally impossible because gradient methods overwrite weights.

**Substrate mechanism:** Impossible by construction. Edges are content-addressed and append-only at the identity level. Significance evolves but identities don't get overwritten. Re-export from substrate state at any time recovers all previously-attested capabilities.

**Structural reason:** Conventional models have shared parameters that must encode all knowledge. New training shifts those parameters, possibly destroying old encoding. The substrate has separate edges per relationship; new evidence accumulates without destroying old.

---

### Context window

**Conventional form:** Fixed-length attention budget. Models have 4K / 8K / 32K / 128K / 1M token limits. Beyond limit, content is truncated or summarized.

**Conventional cost/limit/opaqueness:** Quadratic compute in context length; engineering hacks (sliding window, sparse attention, linear attention) trade fidelity for length.

**Substrate mechanism:** Infinite by construction. Prompts are decomposed to substrate state with `user_session` provenance. Conversation history is graph state, not transient context. Relevant context is selected by traversal, not by attention.

**Structural reason:** Context window exists because attention is O(N²). The substrate has no attention; traversal scales with substrate size, not with conversation length.

**Query shape:** No special handling; conversation history is just more substrate state.

---

### Long-context retrieval

**Conventional form:** Within long context, find specific information. "Needle in a haystack" benchmarks measure this.

**Conventional cost/limit/opaqueness:** Position-encoding extensions degrade. Models miss content in the middle of long contexts.

**Substrate mechanism:** Graph traversal is uniform; there's no "middle of context" position bias. Querying for content N hops back in conversation history is the same operation as querying for current-prompt content.

**Structural reason:** Long-context attention has known position-attention biases. Substrate traversal is position-agnostic.

**Query shape:** Standard inference traversal.

---

### Inference serving infrastructure

**Conventional form:** GPU clusters, model serving frameworks (vLLM, TGI, TensorRT-LLM), KV-cache management, batched scheduling.

**Conventional cost/limit/opaqueness:** Substantial infrastructure. Per-token costs at scale.

**Substrate mechanism:** PostgreSQL. The substrate's inference engine is SQL functions. Scaling is PostgreSQL scaling — replication, partitioning, connection pooling, horizontal sharding (eventual decentralized mode).

**Structural reason:** Conventional inference servers exist because models need GPU infrastructure. The substrate's CPU-only inference runs on a database server.

**Query shape:** Just connect to Postgres and call cognitive functions.

---

### Model evaluation / benchmarks

**Conventional form:** Run model on benchmark suites (MMLU, HumanEval, GSM8K, etc.). Compute scores.

**Conventional cost/limit/opaqueness:** Per-benchmark inference cost. Black-box score reflects nothing about WHY model performs the way it does.

**Substrate mechanism:** Benchmarks are sets of (input, expected-output) pairs. Substrate evaluation is a SQL query against substrate state's responses. Plus per-question provenance trace explains every answer.

**Structural reason:** Evaluating conventional models is opaque (you see scores, not reasons). The substrate's audit chain makes failure analysis structural.

---

### Provenance / explainability

**Conventional form:** Post-hoc explanation methods (attention visualization, gradient attribution, SHAP, LIME). Approximate why-decisions; not ground truth.

**Conventional cost/limit/opaqueness:** Approximations of opaque computations. No guarantees of accuracy.

**Substrate mechanism:** The traversal path IS the explanation. Every edge has provenance. Every answer has a path. Auditability is structural, not approximate.

**Query shape:**
```sql
SELECT hartonomous.provenance.audit_chain(
    response_entity_id => $1,
    depth              => 5
);
```

---

### KV cache

**Conventional form:** Cache key/value tensors from previous tokens to avoid recomputing during autoregressive generation. Memory grows linearly with context length.

**Conventional cost/limit/opaqueness:** Substantial memory; OOM at long contexts.

**Substrate mechanism:** Not applicable. Substrate inference is graph traversal, not autoregressive token-by-token generation. Each query is fresh; no per-token state to cache. PostgreSQL's buffer cache covers query-pattern caching at the database layer.

---

### Speculative decoding

**Conventional form:** Use small "draft" model to propose tokens; large "target" model verifies in parallel. Accepts proposed tokens that match target's predictions.

**Conventional cost/limit/opaqueness:** Engineering complexity; depends on draft-target alignment.

**Substrate mechanism:** Not applicable. Substrate inference is single-pass graph traversal; there's no token-by-token autoregression to speculate against.

---

### Test-time compute (TTC) / inference-time scaling

**Conventional form:** Trade more inference compute for higher quality (longer chain of thought, more samples for self-consistency, search-based reasoning).

**Conventional cost/limit/opaqueness:** Linear-ish scaling of cost with quality; no free lunch.

**Substrate mechanism:** Increase `max_cost` and `max_paths` parameters. Run multiple recipes and aggregate. Per-arena consensus across more thorough traversal.

**Query shape:**
```sql
SELECT * FROM hartonomous.inference.converse(
    prompt    => $1,
    max_cost  => 10000,    -- "thinking harder"
    max_paths => 20,       -- more candidate paths
    -- and combine multiple recipes via union
);
```

---

### Self-consistency / majority voting

**Conventional form:** Sample N independent responses; majority-vote for final answer. Improves accuracy on reasoning tasks.

**Conventional cost/limit/opaqueness:** N× cost.

**Substrate mechanism:** Top-K paths from a single A\* are simultaneously available. No additional cost beyond bumping `max_paths`. Voting / aggregation is over the returned path set.

**Query shape:** `max_paths => 10` then aggregate in client code or via aggregation function.

---

### Reflection / Reflexion / iteratively-refined output

**Conventional form:** Model generates output; same model (or another) critiques output; original model revises. Iteratively until convergence.

**Conventional cost/limit/opaqueness:** N× cost per iteration. Convergence not guaranteed.

**Substrate mechanism:** Multi-pass traversal where each pass uses prior output as input via `user_session`-scoped substrate content. A `reflexion` arena weights revision-of-prior-output edges.

**Query shape:**
```sql
-- Round 1:
SELECT * FROM hartonomous.inference.converse(prompt => $1);
-- Round 2 (uses round 1's output as additional context):
SELECT * FROM hartonomous.inference.converse(
    prompt       => 'Revise:',
    context      => $previous_response_entity,
    arena_recipe => '{"reflexion_weight":1.5}'::jsonb
);
```

---

## V — System-level inversions

### Cost structure

**Conventional:** Pay-per-inference. Per-token cost. GPU compute amortized across queries.

**Substrate:** Pay-per-substrate-build. Inference is indexed lookup; marginal cost per query approaches zero. Mitosis economics make daughter (export) production approach I/O cost.

### Data ownership

**Conventional:** Customer data goes through API; provider sees it. Privacy via contract.

**Substrate:** On-premise substrate is feasible (CPU-only, runs anywhere PostgreSQL runs). Customer data never leaves their infrastructure.

### Determinism

**Conventional:** Stochastic generation. Same prompt → different responses.

**Substrate:** Deterministic by Substrate Law 6. Same substrate state + same recipe + same prompt → byte-identical response. Variation is opt-in.

### Audit

**Conventional:** Black box. Approximations via post-hoc methods.

**Substrate:** Every byte of output traces to substrate edges to provenance to source content. Audit is structural.

### Updates / new data

**Conventional:** Periodic retraining cycles. New knowledge enters via expensive process.

**Substrate:** Continuous ingestion. New data is INSERT; existing edges accumulate.

### Architecture choice

**Conventional:** Architecture is fixed at training time. Customer takes what's released.

**Substrate:** Architecture is a recompose-time parameter. Customer specifies; substrate fills.

### Multi-tenant isolation

**Conventional:** Per-tenant model copies (expensive); or shared model with per-tenant prompts (privacy concern).

**Substrate:** Per-tenant provenance. Customer-confidential corpus is tagged with `tenant_id` provenance and excluded from cross-tenant queries by recipe filter. Single substrate instance serves arbitrary tenants without weight duplication.

---

## VI — What the substrate accomplishes that conventional AI cannot

These are not "substrate replacements" — they're capabilities that don't have conventional AI equivalents:

### Refinement-as-service

Customer's existing model is improved without retraining. Substrate ingests the model + the customer's corpus + the substrate's accumulated curated knowledge. Cross-source corroboration happens at ingestion time via Glicko in arenas. Re-export to the SAME architecture produces refined weights. Drop-in replacement.

There is no conventional AI mechanism for this. Distillation produces a different model. Fine-tuning destabilizes capabilities. Model merging requires architectural compatibility. The substrate does it via content-addressed convergence, which has no conventional analogue.

### Cross-architecture distillation

Substrate edges are architecture-agnostic. Distill from a 480B-param MoE teacher to a 7B-param dense student to a 30B-param heterogeneous-expert MoE — all from the same substrate state.

Conventional distillation cannot cross architecture families.

### Heterogeneous-expert MoE

Each expert can have a different size, fitted to the edge density of its domain cluster. Python expert wide; poetry expert narrow. Recomposition handles each independently.

Conventional MoE training requires uniform expert shape.

### Frayed-edge research

Pairs of entities whose 4D positions match an edge type's archetype trajectory but no edge exists. Mendeleev for knowledge. The substrate can identify pairs that "should" have a specific relation but don't, as candidates for further research.

Conventional AI has no equivalent. This is geometric structure, not statistical pattern.

### Cross-model consensus / divergence

For any token entity, query the substrate for all per-model fireflies. Tight cluster = high agreement; dispersed = disagreement. This is structural cross-model analysis, queryable as SQL.

Conventional AI requires bespoke comparison procedures per model pair.

### Per-customer recipe marketplace

Customers compose inference recipes (per-hop filters, arena weights, provenance restrictions) and share them. Recipes are content-addressed substrate objects. Network effects on recipe library.

Conventional AI has no equivalent because conventional inference is monolithic forward-pass.

### Per-hop multi-model walks

At hop 1, consult Llama. At hop 2, consult Qwen. At hop 3, consult curated WordNet. At hop 4, consult Wiktionary. The "model" perceived by the user is per-hop-customized substrate state.

Conventional inference is monolithic. Once a forward pass starts, you're committed to one model's complete attention/FFN structure. Per-hop model switching is structurally impossible in transformers.

### Determinism across substrate snapshots

Replay any past inference against any past substrate snapshot byte-identically. Forensic reconstruction of why an answer was given two months ago is structural, not approximate.

Conventional AI cannot do this; weights drift, and even with frozen weights, GPU non-determinism and sampling randomness make exact replay infeasible.

### Honest abstention

If no path above significance threshold reaches the target, the substrate returns nothing rather than inventing. Customer recipes can tighten or loosen this floor.

Conventional AI's hallucination mitigations are statistical bounds; the substrate's abstention is structural.

---

## How to use this catalog

When discussing substrate capability with a technical audience: cite the conventional version first ("conventional fine-tuning costs $10K–$1M+ per cycle"), then the substrate's mechanism ("substrate ingests customer corpus; refinement happens via Glicko in arenas at ingestion time"), then the structural reason ("not iterative; not gradient-based; happens at INSERT time"), then the query shape (the SQL).

When evaluating whether a new conventional AI capability has a substrate analogue: walk this catalog. If the new capability is a variation on an existing entry (e.g., a new sampling strategy, a new attention variant), the substrate's existing mechanism likely covers it. If it's structurally novel, this catalog needs a new entry — write it.

When customers ask "can the substrate do X" where X is a conventional AI capability: this catalog is the answer key.

## Cross-references

- Vision (one-paragraph claim): `00-business/00-vision.md`
- Three pillars (the underlying mechanism): `10-architecture/00-overview.md`, `02-identity-and-convergence.md`, `03-geometry-4d.md`, `04-significance-glicko.md`
- Inference engine (per-hop filtering): `10-architecture/07-inference-engine.md`
- Recomposer (export mechanism): `10-architecture/06-recomposer-contract.md`
- Cognitive surface (SQL function catalog): `10-architecture/08-cognitive-surface.md`
- Market positioning (competitive frame): `00-business/02-market-positioning.md`
- Product line (what we ship): `00-business/01-product-line.md`
