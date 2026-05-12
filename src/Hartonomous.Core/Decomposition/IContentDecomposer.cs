using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Decomposition;

/// <summary>
/// Per-modality content decomposer. Produces the content entities (per spec §II.1)
/// that the layer-type decomposers attest BETWEEN. One implementation per modality:
///
/// <list type="bullet">
/// <item><see cref="Hartonomous.Core.Text.SubstrateTextDecomposer"/> (text;
/// produces <c>codepoint</c> → <c>grapheme_cluster</c> → <c>word_form</c> →
/// <c>text_composition</c> entities) — already exists; the canonical reference.</item>
/// <item><c>AudioContentDecomposer</c> (audio; produces <c>audio_recording</c> +
/// <c>audio_chunk</c> LINESTRINGZM entities; CTC/forced-alignment to <c>word_form</c>
/// when transcript available) — Phase A.5.</item>
/// <item><c>ImageContentDecomposer</c> (image; produces <c>pixel_region</c> +
/// optional <c>visual_concept</c> entities for CLIP-style binding) — Phase A.4.</item>
/// <item><c>VideoContentDecomposer</c> (video; produces <c>video_frame</c> + per-frame
/// <c>pixel_region</c> entities via composition over <c>ImageContentDecomposer</c>) —
/// Phase A.6.</item>
/// </list>
///
/// Content decomposers are DIFFERENT from layer-type decomposers: layer-type
/// decomposers consume model tensors and emit attestation edges between content
/// entities; content decomposers consume raw modality bytes (UTF-8, WAV, PNG, MP4)
/// and emit the content entities themselves. Both produce substrate state, but
/// they're at different layers of the decomposition stack.
///
/// Per spec §V.7. Per [`docs/specs/decomposers/layer-type-library.md`]. Per AP-26
/// (modality factoring is wrong for layer-type decomposers; modality factoring is
/// CORRECT for content decomposers because the content extraction logic is genuinely
/// modality-specific — text decoder ≠ audio decoder ≠ image decoder).
/// </summary>
public interface IContentDecomposer
{
    /// <summary>
    /// The modality this decomposer handles, for orchestrator dispatch.
    /// </summary>
    string ModalityCode { get; }

    /// <summary>
    /// File extensions this decomposer accepts (lowercase, with leading dot).
    /// Used by polymorphic content readers to route files to the right decomposer.
    /// </summary>
    /// <example>
    /// SubstrateTextDecomposer: [".txt", ".md", ".json", ".html", ".xml"].
    /// AudioContentDecomposer: [".wav", ".flac", ".mp3", ".ogg"].
    /// ImageContentDecomposer: [".png", ".jpg", ".jpeg", ".webp", ".bmp"].
    /// VideoContentDecomposer: [".mp4", ".mkv", ".webm", ".mov"].
    /// </example>
    IReadOnlyList<string> AcceptedExtensions { get; }

    /// <summary>
    /// Decompose a content stream into substrate content entities. The decomposer
    /// streams chunks (per AP-20) — no load-and-pray of the full input. The
    /// returned <see cref="ContentDecomposeResult"/> carries the root entity hash
    /// (the top-level composition entity for the content) plus any per-call
    /// metadata the caller needs (e.g. <c>RootHandle</c>) for binding edges to.
    /// </summary>
    Task<ContentDecomposeResult> DecomposeAsync(
        Stream content,
        ContentDecomposeOptions options,
        IIngestionBatch batch,
        CancellationToken ct);
}
