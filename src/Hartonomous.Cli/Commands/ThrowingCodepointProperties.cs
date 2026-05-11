using Hartonomous.Core.Text.Segmentation;

namespace Hartonomous.Cli.Commands;

internal sealed class ThrowingCodepointProperties : ICodepointProperties
{
    public static ThrowingCodepointProperties Instance { get; } = new();

    private ThrowingCodepointProperties()
    {
    }

    public GraphemeBreak GetGraphemeBreak(int codepoint) => throw CreateException();

    public bool IsExtendedPictographic(int codepoint) => throw CreateException();

    public WordBreak GetWordBreak(int codepoint) => throw CreateException();

    public SentenceBreak GetSentenceBreak(int codepoint) => throw CreateException();

    public LineBreak GetLineBreak(int codepoint) => throw CreateException();

    private static InvalidOperationException CreateException()
        => new(
            "Legacy ICodepointProperties lookup was invoked from the CLI phase runner. "
            + "Text-bearing decomposers must route through SubstrateTextDecomposer, "
            + "which consumes the native UCD/UCA client catalog directly.");
}
