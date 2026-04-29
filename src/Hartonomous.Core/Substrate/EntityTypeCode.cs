namespace Hartonomous.Core.Substrate;

/// <summary>
/// Strongly-typed entity-type identifier. Wraps the SERIAL id from
/// substrate.entity_type and carries the canonical code string for human
/// inspection / SQL parameter passing. The static instances (Codepoint,
/// WordForm, etc.) are the SINGLE SOURCE OF TRUTH on the C# side; their
/// integer values must match sql/schema/seed/entity_type.sql exactly.
///
/// At process startup, <see cref="ValidateAgainstSubstrate"/> runs against
/// substrate.entity_type and throws if any code/id pair drifts. This makes
/// schema/code drift a fail-fast condition rather than a runtime "Unknown
/// entity_type code" deep in a drain task.
///
/// Why this exists: passing string codes everywhere meant typos in
/// decomposer code (e.g. "inflected_form" after the type was removed from
/// the seed) became runtime errors deep in COPY pipelines instead of
/// compile-time errors. With strongly-typed codes, the compiler refuses
/// to accept a removed type's name.
/// </summary>
public readonly record struct EntityTypeCode(int Id, string Code)
{
    public static readonly EntityTypeCode Codepoint          = new( 1, "codepoint");
    public static readonly EntityTypeCode GraphemeCluster    = new( 2, "grapheme_cluster");
    public static readonly EntityTypeCode WordForm           = new( 3, "word_form");
    public static readonly EntityTypeCode Morpheme           = new( 4, "morpheme");
    public static readonly EntityTypeCode Lemma              = new( 5, "lemma");
    public static readonly EntityTypeCode TextComposition    = new( 6, "text_composition");
    public static readonly EntityTypeCode Paragraph          = new( 7, "paragraph");
    public static readonly EntityTypeCode Document           = new( 8, "document");
    public static readonly EntityTypeCode Synset             = new( 9, "synset");
    public static readonly EntityTypeCode CollationElement   = new(10, "collation_element");
    public static readonly EntityTypeCode LanguageName       = new(11, "language_name");
    public static readonly EntityTypeCode PixelRegion        = new(12, "pixel_region");
    public static readonly EntityTypeCode AudioRecording     = new(13, "audio_recording");
    public static readonly EntityTypeCode AudioChunk         = new(14, "audio_chunk");
    public static readonly EntityTypeCode VideoFrame         = new(15, "video_frame");
    public static readonly EntityTypeCode Tensor             = new(16, "tensor");
    public static readonly EntityTypeCode ModelArchitecture  = new(17, "model_architecture");
    public static readonly EntityTypeCode AttentionPattern   = new(18, "attention_pattern");

    /// <summary>
    /// All canonical types in id order. Used for schema-vs-code drift checks.
    /// </summary>
    public static readonly System.Collections.Generic.IReadOnlyList<EntityTypeCode> All = new[]
    {
        Codepoint, GraphemeCluster, WordForm, Morpheme, Lemma,
        TextComposition, Paragraph, Document, Synset,
        CollationElement, LanguageName,
        PixelRegion,
        AudioRecording, AudioChunk,
        VideoFrame,
        Tensor, ModelArchitecture, AttentionPattern,
    };

    public override string ToString() => Code;

    public static implicit operator string(EntityTypeCode c) => c.Code;
    public static implicit operator int(EntityTypeCode c) => c.Id;
}
