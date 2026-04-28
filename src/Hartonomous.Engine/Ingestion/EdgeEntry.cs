using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// One substrate.edge row plus its members. The pipeline computes the edge
/// hash from (edge_type_id, role-ordered participant hashes) at flush, then
/// writes the edge row + edge_member rows in one transaction. Members carry
/// EntityHandle directly — no surrogate-id resolve step.
/// </summary>
internal readonly record struct EdgeEntry(
    string EdgeTypeCode,
    string ProvenanceCode,
    EdgeMemberSpec[] Members);
