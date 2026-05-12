namespace Hartonomous.Core.Text;

/// <summary>
/// Caller-supplied parameters to <see cref="SubstrateTextDecomposer.Emit"/>.
/// All fields are required; there are no implicit defaults that would let
/// two different callers feed the same content and get different substrate
/// state. Determinism is in the contract: same UTF-8 + same options =
/// byte-identical substrate emission.
/// </summary>
/// <param name="ProvenanceCode">
/// The <c>substrate.provenance.code</c> attributing this decomposition.
/// Examples: <c>tatoeba</c>, <c>wiktextract</c>, <c>princeton_wordnet</c>,
/// <c>universaldependencies</c>, <c>sil_international</c>,
/// <c>huggingface_model</c>, <c>user_session</c>. Used for arena routing and
/// (under the planned classification-junction schema) for per-classification
/// attribution.
/// </param>
/// <param name="TopEntityType">
/// The substrate <c>entity_type.code</c> assigned to the top-level
/// composition. Common values: <c>text_composition</c> (Tatoeba sentences,
/// prompts, free text), <c>lemma</c> (WordNet/Wiktionary/UD lemma forms),
/// <c>language_name</c> (ISO 639 names), <c>paragraph</c>, <c>document</c>.
/// Note: under the planned hash-only PK + classification junction schema,
/// the type is recorded as a classification on the single content-pure
/// entity, NOT as part of identity. Same content with different
/// <c>TopEntityType</c> still produces the same hash and the same entity row.
/// </param>
/// <param name="TrustMu">
/// Initial μ for the <c>source_authority</c> arena prior. Per
/// <c>substrate.provenance.initial_mu</c>: <c>unicode_consortium</c>=2000,
/// <c>princeton_wordnet</c>=1800, <c>universaldependencies</c>=1600,
/// <c>wiktextract</c>=1400, <c>tatoeba</c>=1200, <c>huggingface_model</c>=1500,
/// <c>user_session</c>=1000.
/// </param>
public readonly record struct TextDecomposeOptions(
    string ProvenanceCode,
    string TopEntityType,
    double TrustMu,
    ITextEmissionCache? EmissionCache = null);
