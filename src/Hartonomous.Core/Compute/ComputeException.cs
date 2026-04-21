using System;

namespace Hartonomous.Core.Compute;

public class ComputeException : Exception
{
    public ComputeException(string message) : base(message) { }
    public ComputeException(string message, Exception inner) : base(message, inner) { }
}
