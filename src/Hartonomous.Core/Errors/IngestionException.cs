using System;

namespace Hartonomous.Core.Errors;

public sealed class IngestionException : SubstrateException
{
    public IngestionException(string message) : base(message) { }
    public IngestionException(string message, Exception inner) : base(message, inner) { }
}
