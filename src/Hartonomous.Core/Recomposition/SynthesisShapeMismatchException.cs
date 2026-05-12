using System;

namespace Hartonomous.Core.Recomposition;

public sealed class SynthesisShapeMismatchException : Exception
{
    public SynthesisShapeMismatchException(string message) : base(message) { }
}
