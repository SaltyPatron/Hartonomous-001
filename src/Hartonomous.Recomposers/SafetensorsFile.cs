using System.Collections.Generic;

namespace Hartonomous.Recomposers;

/// <summary>
/// In-memory representation of a recomposed safetensors model package:
/// per-tensor data keyed by tensor name, plus the model name (used as the
/// package identifier in the safetensors __metadata__ block).
///
/// Per docs/specs/csharp/recomposers.md § "SafetensorsRecomposer" output type.
/// </summary>
public sealed record SafetensorsFile(
    IReadOnlyDictionary<string, TensorData> Tensors,
    string ModelName);
