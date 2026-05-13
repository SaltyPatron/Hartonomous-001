using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Text;

public interface ITextEmissionCache
{
    bool TryRegisterEntity(string entityTypeCode, Hash32 hash, string provenanceCode);

    bool TryRegisterPhysicality(string physicalityTypeCode, Hash32 entityHash);

    bool TryRegisterCompositionChild(Hash32 parentHash, int ordinal);

    bool TryRegisterSignificance(string contextTypeCode, string attestationTypeCode, Hash32 entityHash);
}
