namespace Hartonomous.Api.Endpoints;

internal sealed record RecomposeRequest(string EntityTypeCode, string EntityHashHex, int? MaxDepth);
