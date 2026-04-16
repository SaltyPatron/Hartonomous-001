using System.Collections.Generic;

namespace Hartonomous.Decomposers.Ucd;

internal sealed class CollationWeightComparer : IComparer<CollationWeight>
{
    public static readonly CollationWeightComparer Instance = new();

    public int Compare(CollationWeight x, CollationWeight y)
    {
        int c = x.Primary.CompareTo(y.Primary);
        if (c != 0)
        {
            return c;
        }

        c = x.Secondary.CompareTo(y.Secondary);
        if (c != 0)
        {
            return c;
        }

        return x.Tertiary.CompareTo(y.Tertiary);
    }
}
