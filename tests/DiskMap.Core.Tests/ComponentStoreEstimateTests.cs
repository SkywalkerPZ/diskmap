using DiskMap.Core.Removal;

namespace DiskMap.Core.Tests;

public class ComponentStoreEstimateTests
{
    private const double GB = 1024.0 * 1024 * 1024;

    [Fact]
    public void ComponentStoreEstimate_ParsesEnglishOutput()
    {
        const string sample = """
            Component Store (WinSxS) information:

            Windows Explorer Reported Size of Component Store : 12.30 GB
            Actual Size of Component Store : 10.20 GB
                Shared with Windows : 2.00 GB
                Backups and Disabled Features : 6.10 GB
                Cache and Temporary Data : 2.05 GB
            Date of Last Cleanup : 2026-10-01 10:00:00
            Component Store Cleanup Recommended : Yes
            """;
        Assert.Equal(((long)Math.Round(2.05 * GB), (long)Math.Round(8.15 * GB)), ComponentStoreEstimate.Parse(sample));
    }

    [Fact]
    public void ComponentStoreEstimate_UnknownLanguage_ReturnsNull()
    {
        Assert.Null(ComponentStoreEstimate.Parse("Sicherungen und deaktivierte Features : 6,10 GB\nCache und temporaere Daten : 2,05 GB"));
        Assert.Null(ComponentStoreEstimate.Parse(""));
    }

    [Theory]
    [InlineData("1,23 GB", 1.23)]
    [InlineData("1.23 GB", 1.23)]
    [InlineData("1,234.50 GB", 1234.5)]
    [InlineData("1.234,50 GB", 1234.5)]
    [InlineData("1\u00A0234,5 GB", 1234.5)]
    [InlineData("1,234 GB", 1234)]
    [InlineData("12 GB", 12)]
    public void ComponentStoreEstimate_ParsesNumbersInAnyLocale(string value, double gb)
    {
        var text = $"Backups and Disabled Features : 0 GB\nCache and Temporary Data : {value}";
        var (low, high) = ComponentStoreEstimate.Parse(text)!.Value;
        Assert.Equal((long)Math.Round(gb * GB), low);
        Assert.Equal(low, high);
    }
}
