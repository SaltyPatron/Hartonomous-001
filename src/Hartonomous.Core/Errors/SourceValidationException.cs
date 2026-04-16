using System;

namespace Hartonomous.Core.Errors;

public sealed class SourceValidationException : SubstrateException
{
    public SourceValidationException(string message) : base(message) { }
    public SourceValidationException(string message, Exception inner) : base(message, inner) { }
}
