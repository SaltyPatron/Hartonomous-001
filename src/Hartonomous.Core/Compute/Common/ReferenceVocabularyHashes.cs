using System.Text;

namespace Hartonomous.Core.Compute.Common;

/// <summary>
/// Deterministic content-addressed identities for reference vocabulary
/// classifications (POS / lexname / language / morph feature / deprel /
/// sense pointer). Per the AP-8 correction (2026-05-14): reference vocabulary
/// rows become substrate entities reachable as edge targets — corpus decomposers
/// emit typed edges like <c>has_pos(word_form, pos_entity)</c> instead of
/// (or alongside) the legacy junction rows. Same code from any decomposer
/// produces the same hash → one substrate.entity row → cross-source attestation
/// accumulates on the same content-addressed identity.
///
/// Hash inputs are <c>"{kind}:{code}"</c> UTF-8 bytes. The kind prefix
/// guarantees that POS "NOUN" and a hypothetical lexname "NOUN" never collide
/// at the substrate.entity hash. All methods are pure / deterministic /
/// allocation-aware (stackalloc'd buffers for short prefix+code combinations).
///
/// Per AP-9, these hashes are content-only — no provenance, no batch state,
/// no decomposer version. Two decomposers emitting POS "NOUN" produce the
/// same entity hash regardless of session.
/// </summary>
public static class ReferenceVocabularyHashes
{
    private const string PosPrefix = "pos:";
    private const string LexnamePrefix = "lexname:";
    private const string LanguagePrefix = "language:";
    private const string MorphFeaturePrefix = "morph:";
    private const string DeprelPrefix = "deprel:";
    private const string SensePrefix = "sense:";
    private const string GeneralCategoryPrefix = "general_category:";
    private const string ScriptPrefix = "script:";
    private const string BlockPrefix = "block:";
    private const string BidiClassPrefix = "bidi_class:";
    private const string EastAsianWidthPrefix = "east_asian_width:";
    private const string BreakPropertyPrefix = "break_property:";

    /// <summary>
    /// Content-addressed hash for a Universal POS tag code (NOUN, VERB, ADJ, etc.).
    /// Identity = BLAKE3("pos:{code}"). Used as the target of has_pos edges.
    /// </summary>
    public static Hash32 PosEntityHash(string posCode) => HashWithPrefix(PosPrefix, posCode);

    /// <summary>
    /// Content-addressed hash for a WordNet lexicographer category
    /// (noun.animal, verb.creation, adj.pert, etc.). Identity =
    /// BLAKE3("lexname:{code}"). Target of has_lexname edges.
    /// </summary>
    public static Hash32 LexnameEntityHash(string lexnameCode) => HashWithPrefix(LexnamePrefix, lexnameCode);

    /// <summary>
    /// Content-addressed hash for an ISO 639-3 / BCP47 language code
    /// (eng, spa, jpn, etc.). Identity = BLAKE3("language:{code}"). Target
    /// of has_language edges.
    ///
    /// NOTE: <see cref="CrossLinkAttestation.EmitLanguageAttestation"/> uses
    /// BLAKE3 over the bare ISO code (no prefix) for backward compatibility
    /// with existing has_language emissions. This method is the prefix-bearing
    /// version reserved for future has_language emit sites that want
    /// kind-collision protection. New emit sites should converge on this form.
    /// </summary>
    public static Hash32 LanguageEntityHash(string iso639Code) => HashWithPrefix(LanguagePrefix, iso639Code);

    /// <summary>
    /// Content-addressed hash for a Universal Dependencies morph feature
    /// (Number=Sing, Gender=Fem, Tense=Past, etc.). Identity =
    /// BLAKE3("morph:{code}"). Target of has_morph_feature edges.
    /// </summary>
    public static Hash32 MorphFeatureEntityHash(string morphFeatureCode) => HashWithPrefix(MorphFeaturePrefix, morphFeatureCode);

    /// <summary>
    /// Content-addressed hash for a Universal Dependencies dependency relation
    /// (nsubj, obj, root, etc.). Identity = BLAKE3("deprel:{code}"). Target
    /// of has_deprel_pattern edges.
    /// </summary>
    public static Hash32 DeprelEntityHash(string deprelCode) => HashWithPrefix(DeprelPrefix, deprelCode);

    /// <summary>
    /// Content-addressed hash for a WordNet sense-pointer code or other
    /// sense-discrimination key. Identity = BLAKE3("sense:{code}"). Target
    /// of sense-discrimination edges where applicable.
    /// </summary>
    public static Hash32 SenseEntityHash(string senseCode) => HashWithPrefix(SensePrefix, senseCode);

    /// <summary>
    /// Content-addressed hash for a Unicode General_Category code (UAX #44):
    /// Lu / Ll / Lt / Mn / Mc / Me / Nd / Nl / No / Pc / Pd / Ps / Pe / Pi / Pf / Po /
    /// Sm / Sc / Sk / So / Zs / Zl / Zp / Cc / Cf / Cs / Co / Cn. Identity =
    /// BLAKE3("general_category:{code}"). Target of has_cp_general_category edges.
    /// </summary>
    public static Hash32 GeneralCategoryEntityHash(string code) => HashWithPrefix(GeneralCategoryPrefix, code);

    /// <summary>
    /// Content-addressed hash for a Unicode Script code (UAX #24 / ISO 15924):
    /// Latn, Grek, Cyrl, Hani, Arab, Deva, etc. Identity = BLAKE3("script:{code}").
    /// Target of has_cp_script edges. Distinct from the existing has_script edge
    /// type (which carries language_name → ISO 15924 code text composition).
    /// </summary>
    public static Hash32 ScriptEntityHash(string code) => HashWithPrefix(ScriptPrefix, code);

    /// <summary>
    /// Content-addressed hash for a Unicode Block name (UAX #44):
    /// "Basic Latin", "Greek and Coptic", "Devanagari", etc. Identity =
    /// BLAKE3("block:{name}"). Target of has_cp_block edges.
    /// </summary>
    public static Hash32 BlockEntityHash(string code) => HashWithPrefix(BlockPrefix, code);

    /// <summary>
    /// Content-addressed hash for a Unicode Bidi_Class code (UAX #9):
    /// L / R / AL / EN / ES / ET / AN / CS / NSM / BN / B / S / WS / ON / LRE / LRO /
    /// RLE / RLO / PDF / LRI / RLI / FSI / PDI. Identity =
    /// BLAKE3("bidi_class:{code}"). Target of has_cp_bidi_class edges.
    /// </summary>
    public static Hash32 BidiClassEntityHash(string code) => HashWithPrefix(BidiClassPrefix, code);

    /// <summary>
    /// Content-addressed hash for a Unicode East_Asian_Width code (UAX #11):
    /// F / H / W / Na / A / N. Identity = BLAKE3("east_asian_width:{code}").
    /// Target of has_cp_east_asian_width edges.
    /// </summary>
    public static Hash32 EastAsianWidthEntityHash(string code) => HashWithPrefix(EastAsianWidthPrefix, code);

    /// <summary>
    /// Content-addressed hash for a Unicode segmentation break-property code
    /// (UAX #29 GCB / WB / SB and UAX #14 LB). Identity =
    /// BLAKE3("break_property:{category}:{code}"). The category prefix
    /// disambiguates same-coded breaks across different break categories
    /// (e.g. GCB:CR vs LB:CR — distinct semantics). Target of
    /// has_cp_grapheme_break / has_cp_word_break / has_cp_sentence_break /
    /// has_cp_line_break edges.
    /// </summary>
    public static Hash32 BreakPropertyEntityHash(string category, string code)
        => HashWithPrefix(BreakPropertyPrefix, category + ":" + code);

    private static Hash32 HashWithPrefix(string prefix, string code)
    {
        int prefixBytes = Encoding.UTF8.GetByteCount(prefix);
        int codeBytes = Encoding.UTF8.GetByteCount(code);
        int total = prefixBytes + codeBytes;

        // Most reference codes are short (<= 32 bytes including prefix);
        // stackalloc keeps the hot path allocation-free. Fall through to
        // heap allocation only for unusually long inputs.
        if (total <= 128)
        {
            System.Span<byte> buffer = stackalloc byte[total];
            Encoding.UTF8.GetBytes(prefix, buffer[..prefixBytes]);
            Encoding.UTF8.GetBytes(code, buffer[prefixBytes..]);
            return Blake3.Hash32(buffer);
        }
        else
        {
            byte[] buffer = new byte[total];
            Encoding.UTF8.GetBytes(prefix, buffer.AsSpan(0, prefixBytes));
            Encoding.UTF8.GetBytes(code, buffer.AsSpan(prefixBytes));
            return Blake3.Hash32(buffer);
        }
    }
}
