using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

internal readonly record struct JunctionEntry(string JunctionTable, EntityHandle Entity, int ReferenceId, double? Mu);
