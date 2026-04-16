using System;

namespace Hartonomous.Core.Errors;

public abstract class SubstrateException : Exception
{
    protected SubstrateException(string message) : base(message) { }
    protected SubstrateException(string message, Exception inner) : base(message, inner) { }
}
