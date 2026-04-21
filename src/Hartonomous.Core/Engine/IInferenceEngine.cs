using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Core.Engine;

/// <summary>
/// The substrate inference engine. Accepts a query (text or pre-resolved seeds),
/// decomposes it into seed entities, traverses the substrate graph guided by
/// significance, selects top-k paths, and returns the result with entity metadata.
/// </summary>
public interface IInferenceEngine
{
    /// <summary>
    /// Run inference: decompose input → activate seeds → traverse → select paths → return.
    /// </summary>
    Task<InferenceResult> InferAsync(InferenceQuery query, CancellationToken ct);
}
