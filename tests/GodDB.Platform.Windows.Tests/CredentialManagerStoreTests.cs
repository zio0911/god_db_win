using GodDB.Platform.Windows;

namespace GodDB.Platform.Windows.Tests;

/// Windows 에서만 돈다 (CI windows-latest). 다른 OS 에서는 건너뛴다.
public class CredentialManagerStoreTests
{
    [Fact]
    public void SaveReadDelete()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows 전용");
        var store = new CredentialManagerStore();
        var id = Guid.NewGuid();
        try
        {
            Assert.Null(store.GetPassword(id));
            Assert.True(store.SetPassword(id, "p@ss 한글 ✓"));
            Assert.Equal("p@ss 한글 ✓", store.GetPassword(id));
            Assert.True(store.SetPassword(id, "changed"));
            Assert.Equal("changed", store.GetPassword(id));
            Assert.True(store.SetPassword(id, ""));
            Assert.Equal("", store.GetPassword(id));
        }
        finally { store.DeletePassword(id); }
        Assert.Null(store.GetPassword(id));
    }

    [Fact]
    public void TargetNameMatchesMacAccountFormat()
    {
        var id = Guid.Parse("0b8f6c1e-1234-4abc-9def-001122334455");
        Assert.Equal("GodDB:0B8F6C1E-1234-4ABC-9DEF-001122334455", CredentialManagerStore.TargetName(id));
    }
}
