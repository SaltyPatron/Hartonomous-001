using System;

namespace Hartonomous.Core.Recomposition;

public sealed class SynthesisDispatchException : Exception
{
    public SynthesisDispatchException(string message) : base(message) { }
}
