namespace DiskMap.Core.Tests;

public class FixtureTests
{
    [Fact]
    public void Fixture_CreatesFileWithSize()
    {
        using var f = new Fixture();
        Assert.Equal(5000, new FileInfo(f.File("a/b.bin", 5000)).Length);
    }
}
