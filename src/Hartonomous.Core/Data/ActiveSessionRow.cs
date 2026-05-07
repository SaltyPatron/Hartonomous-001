using System;

namespace Hartonomous.Core.Data;

/// <summary>
/// Projection of a row from <c>monitor.active_sessions</c>.
/// </summary>
public sealed record ActiveSessionRow(
    Guid SessionId,
    string Label,
    DateTimeOffset StartedAt,
    long ComparisonEventCount);