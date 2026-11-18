using System.Security.Cryptography;
using GodDB.Core.Platform;
using GodDB.Platform.Windows;

namespace GodDB.Platform.Windows.Tests;

public class DeviceKeysTests
{
    [Fact]
    public void DpapiStoreRoundTripsAndIsNotPlain()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows 전용");
        var dir = Path.Combine(Path.GetTempPath(), "goddb-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = new FileDeviceKeyStore(dir);
            var store = new DpapiDeviceKeyStore(files);
            var secret = RandomNumberGenerator.GetBytes(48);
            Assert.True(store.Save("device-key-sw", secret));
            Assert.Equal(secret, store.Load("device-key-sw"));
            // 파일에는 잠긴 값만
            Assert.NotEqual(secret, files.Load("device-key-sw.dpapi"));
            Assert.Null(store.Load("missing"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// CI 러너에 TPM 이 없으면 null 이 정상 (그러면 DPAPI 키를 쓴다). 있으면 서명이 공개 키로 검증돼야 한다.
    [Fact]
    public void TpmSignerVerifiesWhenAvailable()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows 전용");
        var signer = TpmDeviceSigner.TryCreate();
        if (signer == null) Assert.Skip("TPM 없음");
        var data = "line"u8.ToArray();
        var sig = signer.Sign(data);
        Assert.Equal(64, sig.Length);
        Assert.Equal(64, signer.PublicKeyRaw.Length);
        using var ec = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = signer.PublicKeyRaw[..32], Y = signer.PublicKeyRaw[32..] },
        });
        Assert.True(ec.VerifyData(data, sig, HashAlgorithmName.SHA256));
    }
}
