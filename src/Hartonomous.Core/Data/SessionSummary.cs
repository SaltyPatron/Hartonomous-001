using System;

namespace Hartonomous.Core.Data;

/// <summary>
/// Lightweight projection of a <c>monitor.session</c> row for list display.
/// </summary>
public sealed record SessionSummary(
    Guid SessionId,
    string Label,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long ComparisonEventCount);
