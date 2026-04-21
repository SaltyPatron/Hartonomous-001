using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

internal readonly record struct SequenceEntry(EntityHandle Parent, EntityHandle Child, int Position, int Count);
