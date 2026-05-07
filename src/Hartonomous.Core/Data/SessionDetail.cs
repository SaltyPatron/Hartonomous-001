using System;

namespace Hartonomous.Core.Data;

/// <summary>
/// Full detail of a single <c>monitor.session</c> row, including aggregate counts
/// from <c>monitor.comparison_event</c>.
/// </summary>
public sealed record SessionDetail(
    Guid SessionId,
    string Label,
    string? Notes,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long ComparisonEventCount);
