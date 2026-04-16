using System;

namespace Hartonomous.Core.Orchestration;

public sealed record PhaseResult(
    Phase Phase,
    PhaseStatus Status,
    TimeSpan Elapsed,
    string? ErrorMessage);
