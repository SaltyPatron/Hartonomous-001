using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

internal readonly record struct EntityModelSourceEntry(EntityHandle Entity, long ModelSourceId);
