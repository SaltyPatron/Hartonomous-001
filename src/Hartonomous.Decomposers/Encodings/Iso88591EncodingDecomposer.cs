using Hartonomous.Core.Decomposition;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Decomposers.Encodings;

/// <summary>
/// ISO 8859-1 (Latin-1) encoding decomposer. 256 mappings; full byte→codepoint
/// identity for the 0x00–0xFF range. Cross-source attestation accumulates on
/// shared codepoints when other encodings (Windows-1252, MacRoman, etc.)
/// attest the same Latin-1 supplement positions.
/// </summary>
public sealed class Iso88591EncodingDecomposer : EncodingDecomposerBase
{
    public override string ProvenanceCode => "iso_8859_1";
    public override string DisplayName => "ISO 8859-1 (Latin-1) Encoding Decomposer";

    public Iso88591EncodingDecomposer(DecomposerConfig config, ILogger<Iso88591EncodingDecomposer> logger)
        : base(config, logger) { }

    protected override string EncodingName => "ISO 8859-1";

    protected override int[] ByteToCodepoint
    {
        get
        {
            int[] table = new int[256];
            for (int i = 0; i < 256; i++) { table[i] = i; }
            return table;
        }
    }
}
