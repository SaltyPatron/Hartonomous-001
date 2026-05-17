using Hartonomous.Core.Decomposition;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Encodings;

/// <summary>
/// ASCII (7-bit) encoding decomposer. 128 mappings (0x00–0x7F → U+0000–U+007F),
/// identity in the lower 128 codepoints. Fires has_encoding_position events under
/// ascii provenance into the encoding_position_consensus arena.
/// </summary>
public sealed class AsciiEncodingDecomposer : EncodingDecomposerBase
{
    public override string ProvenanceCode => "ascii";
    public override string DisplayName => "ASCII (7-bit) Encoding Decomposer";

    public AsciiEncodingDecomposer(DecomposerConfig config, ILogger<AsciiEncodingDecomposer> logger)
        : base(config, logger) { }

    protected override string EncodingName => "ASCII";

    protected override int[] ByteToCodepoint
    {
        get
        {
            int[] table = new int[256];
            for (int i = 0; i < 128; i++) { table[i] = i; }
            // 0x80-0xFF undefined
            return table;
        }
    }
}
