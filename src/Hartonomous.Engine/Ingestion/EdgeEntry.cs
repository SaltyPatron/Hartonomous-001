using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

internal readonly record struct EdgeEntry(string EdgeTypeCode, string ProvenanceCode, EdgeMemberSpec[] Members);
