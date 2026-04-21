using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

internal readonly record struct SignificanceEntry(EntityHandle Entity, string ContextTypeCode, double InitialMu);
