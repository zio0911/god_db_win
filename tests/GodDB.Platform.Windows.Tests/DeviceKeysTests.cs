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

    /// 로컬 데이터 암호화 키 (Mac 키체인 `local-data-key-v1` 대응): DPAPI 로 잠가 두고, 처음이면 만들고 다음엔 같은 키.
    /// 감사 로그 기기 키와 엔트로피가 달라 서로의 파일로 열리지 않는다.
    [Fact]
    public void LocalDataKeyIsDpapiProtectedAndStable()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows 전용");
        var dir = Path.Combine(Path.GetTempPath(), "goddb-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = new FileDeviceKeyStore(dir);
            var store = new DpapiDeviceKeyStore(files, DpapiDeviceKeyStore.LocalDataEntropy);
            var key = new DeviceKeyStoreLocalDataKeyProvider(store).Key();
            Assert.Equal(32, key.Length);
            Assert.Equal(key, new DeviceKeyStoreLocalDataKeyProvider(new DpapiDeviceKeyStore(files, DpapiDeviceKeyStore.LocalDataEntropy)).Key());
            Assert.NotEqual(key, files.Load(DeviceKeyStoreLocalDataKeyProvider.Account + ".dpapi"));
            // 다른 엔트로피(감사 로그 기기 키 저장소)로는 풀 수 없다
            Assert.Null(new DpapiDeviceKeyStore(files).Load(DeviceKeyStoreLocalDataKeyProvider.Account));
            // 실제로 파일을 잠그고 연다
            var crypto = new LocalDataCrypto(new DeviceKeyStoreLocalDataKeyProvider(store));
            var file = Path.Combine(dir, "tabs.json");
            Assert.True(crypto.WriteText(file, "SELECT 1"));
            Assert.Equal("SELECT 1", crypto.ReadText(file));
            Assert.True(UserOnlyFile.IsRestricted(file));
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
