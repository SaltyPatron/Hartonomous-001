namespace Hartonomous.Decomposers.Ucd;

internal sealed record UcdMaterializationCounts(
    long CodepointClassifications,
    long CodepointProperties,
    long SimpleCaseEdges,
    long SimpleCaseEdgesWithoutGeometry,
    long SignificanceContexts,
    long SimpleCaseEdgeSignificance);
