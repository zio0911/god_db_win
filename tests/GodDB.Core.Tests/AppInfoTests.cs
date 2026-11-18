using GodDB.Core;

namespace GodDB.Core.Tests;

public class AppInfoTests
{
    [Fact]
    public void VersionComesFromVersionFile()
    {
        var file = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "VERSION")).Trim();
        Assert.Equal(file, AppInfo.Version);
    }
}
