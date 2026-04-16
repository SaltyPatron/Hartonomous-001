using Hartonomous.Decomposers.Ucd;

namespace Hartonomous.Decomposers.Tests.Ucd;

public sealed class ReferenceTableCollectorTests
{
    [Fact]
    public void AddGeneralCategory_CollectsUnique()
    {
        ReferenceTableCollector collector = new();

        collector.AddGeneralCategory("Lu");
        collector.AddGeneralCategory("Ll");
        collector.AddGeneralCategory("Lu"); // duplicate

        Assert.Equal(2, collector.GeneralCategories.Count);
        Assert.Contains("Lu", collector.GeneralCategories.Keys);
        Assert.Contains("Ll", collector.GeneralCategories.Keys);
    }

    [Fact]
    public void AddScript_CollectsUnique()
    {
        ReferenceTableCollector collector = new();

        collector.AddScript("Latn");
        collector.AddScript("Grek");
        collector.AddScript("Latn"); // duplicate

        Assert.Equal(2, collector.Scripts.Count);
    }

    [Fact]
    public void AddBlock_CollectsWithRange()
    {
        ReferenceTableCollector collector = new();

        collector.AddBlock("ASCII", 0x0000, 0x007F);
        collector.AddBlock("Latin Extended-A", 0x0100, 0x017F);

        Assert.Equal(2, collector.Blocks.Count);
        Assert.Equal((0x0000, 0x007F), collector.Blocks["ASCII"]);
    }

    [Fact]
    public void AddBreakProperty_CollectsWithCategory()
    {
        ReferenceTableCollector collector = new();

        collector.AddBreakProperty("CR", "GCB");
        collector.AddBreakProperty("LF", "GCB");
        collector.AddBreakProperty("CR", "WB"); // same code, different category

        Assert.Equal(3, collector.BreakProperties.Count);
        Assert.Contains(("CR", "GCB"), collector.BreakProperties.Keys);
        Assert.Contains(("CR", "WB"), collector.BreakProperties.Keys);
    }

    [Fact]
    public void EmptyCollector_HasEmptyCollections()
    {
        ReferenceTableCollector collector = new();

        Assert.Empty(collector.GeneralCategories);
        Assert.Empty(collector.Scripts);
        Assert.Empty(collector.Blocks);
        Assert.Empty(collector.BreakProperties);
    }
}
