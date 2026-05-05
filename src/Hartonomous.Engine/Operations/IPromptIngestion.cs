namespace Hartonomous.Engine.Operations;

public interface IPromptIngestion
{
    Task<byte[]> IngestAsync(string promptText, string provenanceCode, double trustMu, CancellationToken ct);
}
