using System;

namespace Hartonomous.Core.Errors;

public sealed class TraversalException : SubstrateException
{
    public TraversalException(string message) : base(message) { }
    public TraversalException(string message, Exception inner) : base(message, inner) { }
}
