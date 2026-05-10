using Hartonomous.Core.Data;

namespace Hartonomous.Decomposers.Omw;

internal sealed class OmwReferenceTableWriter : BaseReferenceTableWriter
{
    public OmwReferenceTableWriter(IReferenceDataReader reader, IJunctionWriter junctionWriter, IReferenceDataWriter referenceDataWriter)
        : base(reader, junctionWriter, referenceDataWriter)
    {
    }
}
