using System;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text.Segmentation;

namespace Hartonomous.Core.Text;

/// <summary>
/// Compatibility facade for older callers that still pass an
/// <see cref="ICodepointProperties"/> cache. Text decomposition is owned by
/// <see cref="SubstrateTextDecomposer"/>, which marshals to the shared native
/// <c>hartonomous_text_decompose</c> implementation used by both C# and SQL.
/// </summary>
public static class CanonicalTextDecomposer
{
    public static TextDecomposeResult Emit(
        IIngestionBatch batch,
        ReadOnlySpan<byte> utf8,
        ICodepointProperties codepointProperties,
        TextDecomposeOptions options)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(codepointProperties);

        return SubstrateTextDecomposer.EmitStatic(batch, utf8, options);
    }
}
