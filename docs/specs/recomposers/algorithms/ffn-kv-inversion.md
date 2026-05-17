# FFN-as-KV-Memory Inversion — Algorithm Spec

**Status:** Canonical for `FfnLayerSynthesizer` (and by extension `MoeExpertLayerSynthesizer`).

**Authority:** Slice of [`docs/00-substrate-spec.md`](../../00-substrate-spec.md) §VI. Algorithm derivation from research dispatched 2026-05-09. References Geva, Dai foundational papers on FFN-as-key-value memory.

**Reciprocal of:** `FfnLayerDecomposer` (Phase A.1; refactored from `TokenFfnEdgePass`) which emits `model_ffn_factor` edges between word_form entities with sign-aware `positive_evidence`/`negative_evidence` events (P1d 2026-05-14 collapse) and `EdgeRatingEvent` attribution `(Linear, SwiGluFfn|BertFfn, {gate|up|down|intermediate|output})` encoding (input_token, output_token, strength) constraints from forward FFN analysis. (The old `model_ffn_full_path` attestation_type label moved to the EdgeRatingEvent attribution metadata.)

---

## Forward direction (foundational — already in literature)

Per Geva et al. 2021 (arXiv:2012.14913) and Dai et al. 2022 (arXiv:2104.08696), an FFN layer with weights `(W_up, W_gate, W_down)` and gated activation `σ` (SiLU for SwiGLU, GELU for GeGLU) acts as a key-value memory:

```
Input:  x ∈ R^H              (residual hidden state)
Up:     u = W_up · x          (W_up ∈ R^{I×H})
Gate:   g = σ(W_gate · x)     (W_gate ∈ R^{I×H})
Activate: a = u ⊙ g            (element-wise; ∈ R^I)
Down:   y = W_down · a        (W_down ∈ R^{H×I})
```

Each row `i` of `(W_up, W_gate)` is a key-pattern that activates intermediate dim `i` for specific input residuals. Each column `i` of `W_down` is a value-pattern that contributes to the output residual when intermediate `i` fires. The mechanism is a learned associative memory: *if* input matches pattern k_i, *then* add pattern v_i to the residual.

At decomposition time (`FfnLayerDecomposer`), for each significant (input_token a, output_token b) pair, emit an attestation edge with strength `s_{a,b}` reflecting the magnitude of the a → b contribution through the FFN.

---

## Inverse direction (synthesis problem; novel for substrate-consensus)

**Given:**
- Target FFN architecture: intermediate dim `I`, hidden dim `H`, activation function `σ`
- Sparse attestation matrix `S ∈ R^{V × V}` where `S[a][b] = consensus_mu` for token pair (a, b) at this layer
- Token embeddings `embed(a) ∈ R^H` (from prior `EmbeddingLayerSynthesizer` output OR from the architecture's tied embeddings)
- Unembedding vectors `e_b ∈ R^H` (from prior `LmHeadLayerSynthesizer` output OR tied)

**Find:** `(W_up, W_gate, W_down)` that satisfies the constraints:
```
e_b^T · W_down · σ(W_gate · embed(a)) ⊙ (W_up · embed(a)) ≈ s_{a,b}   for each attested (a, b)
```

---

## Well-posedness

The system is **severely underdetermined** in the typical regime:
- Variables: `2·I·H + H·I = 3·I·H` parameters
- Constraints: at most `V²` equations (top-K attestations per token; typically `~V·K` where `K << V`)
- Typical: `H = 4096`, `I = 4·H = 16384`, `V = 128k`, `K = 50` → variables ≈ 200M, constraints ≈ 6M
- Underdetermined → infinite solutions exist

**Honest abstention** is therefore the correct disposition for under-supported intermediate dimensions: stay at exact zero rather than fabricating from incomplete evidence.

---

## Two synthesis approaches

### Approach 1 — Direct KV-memory construction + exact SVD compression (RECOMMENDED canonical)

**Step 1: Direct construction.** For each significant attestation `(a, b, s_{a,b})`, allocate one intermediate dimension `i` and set:
```
W_up[i, :]   = embed(a)
W_gate[i, :] = embed(a)         (same, so σ(W_gate·x) reproduces the gate's contribution at activation time)
W_down[:, i] = e_b · s_{a,b}
```

This produces an over-sized FFN with `I_construct = |S|` intermediate dimensions (one per attestation). Each attestation gets its own memory slot. The forward pass exactly reproduces the constraint pattern (modulo the gating activation's nonlinearity — see Step 3 below).

**Step 2: SVD compression to target I.** Compute thin SVD of the constructed matrices and truncate to target intermediate dimension:
```
[W_up_construct | W_gate_construct] = U·Σ·V^T
W_up    ← U[:, :I] · sqrt(Σ[:I])
W_gate  ← U[:, :I] · sqrt(Σ[:I])    (or via independent SVD if gate ≠ up)
W_down  ← compress W_down_construct via the same SVD basis
```

SVD is exact closed-form via `Eigen::BDCSVD` or `oneMKL dgesdd`. The truncation IS an information loss, but it's an HONEST loss (compression of redundant memory slots) rather than approximation of an exact value.

**Step 3: Activation-function correction.** The gating activation `σ` is transcendental (SwiGLU/GeGLU), so `σ(W_gate·embed(a))` ≠ `W_gate·embed(a)` in general. The direct construction in Step 1 implicitly assumes σ is identity, which is wrong for SwiGLU/GeGLU. To compensate:
- For SwiGLU/GeGLU: scale `W_down[:, i]` by `1 / σ(W_gate[i,:] · embed(a))` so the forward pass output matches `s_{a,b}` exactly when input is `embed(a)`.
- For plain ReLU/GELU FFN (no gate): no correction needed; activation is element-wise on `u`.

**Complexity:**
- Construction: O(|S| · H) — single pass over attestations
- SVD compression: O(min(|S|, 3H)² · max(|S|, 3H)) for thin SVD
- Total: O(|S|·H + |S|²·H) where typical |S| ≈ V·K ≈ 6M and H ≈ 4096

**Determinism:** Fully deterministic — direct construction is exact; SVD is closed-form (Jacobi or BDCSVD with deterministic ordering).

**Honest abstention:** Attestations below significance threshold are excluded from `S`; intermediate dimensions corresponding to excluded attestations don't exist (the constructed matrix is smaller). Compression target I may exceed the post-threshold rank, in which case the "extra" intermediate dimensions stay at zero (honest abstention).

### Approach 2 — Per-intermediate-dimension Levenberg-Marquardt (alternative; iterative)

For each intermediate dimension `i` with at least `θ ≥ 3` supporting attestations, solve the joint nonlinear least-squares problem for `(W_up[i,:], W_gate[i,:], W_down[:,i])`:

```
min_{w_up, w_gate, w_down} Σ_{(a,b,s)} (e_b^T · w_down · σ(w_gate · embed(a)) · (w_up · embed(a)) - s)²
```

via `Eigen::LevenbergMarquardt` (Levenberg-Marquardt with QR-based Jacobian).

**Why iterative:** the gating activation σ is transcendental → no algebraic closed-form for the joint optimization. LM is the canonical numerical method (quadratic convergence near solution; deterministic given fixed seed).

**Determinism:** seeded random initialization (seed derived from `ModelPassContext.CheckpointKey` per Substrate Law #6 pattern); deterministic numerical iteration.

**Complexity:** O(I · |S| · H) per LM iteration; ~5-20 LM iterations to convergence; total O(I · LM_iters · |S| · H).

**Honest abstention:** dimensions with < θ attestations stay at zero; dimensions where LM fails to converge after max iterations OR condition number exceeds 10⁸ are zeroed and reported in coverage statistics.

### Recommendation: Approach 1

Direct construction + exact SVD compression is preferred because:
- Step 1 (construction) and Step 2 (SVD) are both EXACT closed-form operations
- Step 3 (activation correction) is a per-dim scalar adjustment, also exact
- No iterative numerical methods required
- Cleaner determinism story — no PRNG seeded LM convergence
- Cleaner honest abstention — over-budget intermediate dimensions stay at zero structurally rather than via convergence-failure detection

Approach 2 is documented as the alternative for cases where direct construction's over-allocated `I_construct` exceeds memory budget (very large vocabularies × very high attestation density). For typical workloads, Approach 1 is the canonical path.

---

## Implementation surface

```csharp
public sealed class FfnLayerSynthesizer : ILayerTypeSynthesizer
{
    public bool Handles(TensorRole role) => role is
        TensorRole.FfnGate or TensorRole.FfnUp or TensorRole.FfnDown
        or TensorRole.MoeSharedExpert;

    public async Task<byte[]> SynthesizeAsync(
        TargetTensorSpec target,
        SubstrateAttestationQuery query,
        RecompositionOptions options,
        CancellationToken ct)
    {
        // 1. Query model_ffn_factor edges with attestation_type=model_ffn_full_path
        //    filtered by arena/threshold/layer/expert metadata.
        // 2. Direct construction: one intermediate dim per attestation.
        // 3. Exact thin SVD via Compute.Svd; truncate to target I.
        // 4. Activation-function correction per Step 3 (scale W_down rows).
        // 5. Pack to target dtype.
        // Honest abstention: cells beyond post-threshold rank stay at zero.
    }
}
```

Native compute primitives used:
- `Compute.Svd.Thin` — already exists in `src/Hartonomous.Core/Compute/Ingestion/Svd.cs`
- Embedding lookup: from prior synthesizer output or architecture's tied embeddings
- Activation function: in-place element-wise (SiLU, GELU, ReLU); trivial

For MoE experts: `MoeExpertLayerSynthesizer` reuses `FfnLayerSynthesizer` scoped per expert via `expert_index` filter on the attestation query.

---

## Honest abstention semantics

- Attestations below `RecompositionOptions.SignificanceThreshold` excluded from `S` → corresponding intermediate dimensions don't exist post-construction → naturally absent in synthesized weights.
- Tokens with no attestation participation → their corresponding rows/columns in `(W_up, W_gate, W_down)` stay at zero.
- After SVD compression: if rank(S) < target I, the extra intermediate dimensions stay at zero (the SVD's lower singular values are exact zeros within numerical tolerance).
- Per-tensor coverage statistics emitted to safetensors header:
  - % of intermediate dims with non-zero rows
  - % of attestations that survived the significance threshold
  - Singular value spectrum summary (cumulative variance retained at the truncation point)

---

## Cross-references

- [`docs/00-substrate-spec.md`](../../00-substrate-spec.md) §VI (recomposer architecture)
- [`docs/specs/decomposers/layer-type-library.md`](../../decomposers/layer-type-library.md) (`FfnLayerDecomposer` row)
- [`docs/specs/recomposers/synthesis-library.md`](../synthesis-library.md) (`FfnLayerSynthesizer` row)
- [`lottery-ticket-foundations.md`](lottery-ticket-foundations.md) (sparsity discipline; under-supported intermediate dimensions are the un-winning lottery tickets)
- [`synthesis-hardware-integration.md`](../../native/synthesis-hardware-integration.md) (Eigen/oneMKL primitives for SVD)
- `src/Hartonomous.Core/Compute/Ingestion/Svd.cs` (existing thin SVD primitive)
- `src/Hartonomous.Decomposers/Safetensors/Passes/TokenFfnEdgePass.cs` (the reciprocal forward decomposer; vision-aligned working code)

## References

- Geva, M., Schuster, R., Berant, J., & Levy, O. (2020). *Transformer Feed-Forward Layers Are Key-Value Memories*. arXiv:2012.14913. https://doi.org/10.48550/arXiv.2012.14913
- Dai, H., et al. (2021). *Knowledge Neurons in Pretrained Transformers*. arXiv:2104.08696. https://doi.org/10.48550/arXiv.2104.08696
- Nocedal, J., & Wright, S. J. (2006). *Numerical Optimization* (2nd ed.). Springer. ISBN 978-0-387-30303-0. (Levenberg-Marquardt reference for Approach 2.)
- Demmel, J. W. (1997). *Applied Numerical Linear Algebra*. SIAM. (Thin SVD reference for Approach 1.)

## Empirical questions to measure once Phase C ships

- Approach 1 vs Approach 2 fidelity comparison: does direct-construct + SVD compression produce equivalent forward-FFN behavior to LM-fit per-dim?
- Activation-correction error: how well does Step 3's scalar adjustment compensate for the σ nonlinearity in practice?
- Coverage statistics across the model farm: what % of FFN intermediate dimensions stay at zero post-synthesis on average?
