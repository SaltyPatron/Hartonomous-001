using System;
using System.Collections.Generic;

namespace Hartonomous.Core.Operations;

public abstract record OperationResponse
{
    public required byte[] OutputCompositionHash { get; init; }

    public string? OutputModalityCode { get; init; }

    public string? AnswerText { get; init; }

    public int NodesVisited { get; init; }

    public TimeSpan Elapsed { get; init; }

    public required IReadOnlyList<ProvenanceTrace> Trace { get; init; }

    public IReadOnlyDictionary<string, string>? ExtraDiagnostics { get; init; }
}
