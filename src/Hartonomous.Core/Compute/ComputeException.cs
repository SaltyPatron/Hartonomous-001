using System;

namespace Hartonomous.Core.Compute;

public class ComputeException : Exception
{
    public ComputeException(string message) : base(message) { }
    public ComputeException(string message, Exception inner) : base(message, inner) { }
}

public sealed class ComputeArgumentException : ComputeException
{
    public ComputeArgumentException(string message) : base(message) { }
}

public sealed class ComputeAllocationException : ComputeException
{
    public ComputeAllocationException(string message) : base(message) { }
}

public sealed class ComputeConvergenceException : ComputeException
{
    public ComputeConvergenceException(string message) : base(message) { }
}

public sealed class UnsupportedDtypeException : ComputeException
{
    public UnsupportedDtypeException(string message) : base(message) { }
}
