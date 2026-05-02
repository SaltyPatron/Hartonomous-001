namespace Hartonomous.Engine.Godel;

/// <summary>
/// One unit of inference. The Gödel Engine's Observe phase decomposes the
/// raw prompt into sub-questions via UAX #29 sentence boundaries plus clause
/// splits on conjunctions. Each sub-question is its own forward pass — its
/// own seed activation, its own A* fan-out, its own top-K — and is later
/// synthesized into the final response by the Act phase.
///
/// Index is the sub-question's order in the original prompt; preserving it
/// lets the synthesizer reassemble multi-clause answers in the order the
/// user asked (Observe → Orient phases of the engine spec).
/// </summary>
public sealed record SubQuestion(int Index, string Text);
