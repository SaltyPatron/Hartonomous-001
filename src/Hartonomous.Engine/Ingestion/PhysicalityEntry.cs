using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

internal readonly record struct PhysicalityEntry(EntityHandle Entity, string PhysicalityTypeCode, byte[] GeomWkb);
