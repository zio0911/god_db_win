using System.Runtime.Versioning;
using System.Security.Cryptography;
using GodDB.Core.Platform;

namespace GodDB.Platform.Windows;

/// 감사 로그 서명용 기기 키를 TPM 에 만든다 (Mac: Secure Enclave). 키는 TPM 밖으로 꺼낼 수 없어
/// 다른 PC 에서는 같은 서명을 만들 수 없다. TPM 이 없거나 쓸 수 없으면 null → DPAPI 로 잠근 소프트웨어 키를 쓴다.
[SupportedOSPlatform("windows")]
public sealed class TpmDeviceSigner : IDeviceSigner
{
    private const string Provider = "Microsoft Platform Crypto Provider";
    private const string KeyName = "GodDB-audit-device";
    private readonly ECDsaCng _ecdsa;

    private TpmDeviceSigner(CngKey key)
    {
        _ecdsa = new ECDsaCng(key);
        var p = _ecdsa.ExportParameters(includePrivateParameters: false);
        PublicKeyRaw = [.. p.Q.X!, .. p.Q.Y!];
    }

    public byte[] PublicKeyRaw { get; }
    public string Kind => "tpm";

    /// SHA-256 해시에 서명, r‖s 64바이트 (ECDsaCng 기본 형식이 IEEE P1363)
    public byte[] Sign(byte[] data) => _ecdsa.SignData(data, HashAlgorithmName.SHA256);

    public static TpmDeviceSigner? TryCreate()
    {
        try
        {
            var provider = new CngProvider(Provider);
            CngKey key;
            if (CngKey.Exists(KeyName, provider))
                key = CngKey.Open(KeyName, provider);
            else
                key = CngKey.Create(CngAlgorithm.ECDsaP256, KeyName, new CngKeyCreationParameters
                {
                    Provider = provider,
                    KeyCreationOptions = CngKeyCreationOptions.None, // 현재 사용자 키
                    ExportPolicy = CngExportPolicies.None,           // 꺼낼 수 없음
                });
            return new TpmDeviceSigner(key);
        }
        catch (CryptographicException) { return null; }
        catch (PlatformNotSupportedException) { return null; }
    }
}

/// 기기 키 파일을 DPAPI(현재 사용자)로 잠가 보관한다. 다른 계정이나 다른 PC 로 파일을 복사하면 풀 수 없다.
/// (Mac: 이 기기 전용 키체인 항목). 잠그지 않은 예전 파일과 섞이지 않게 계정 이름에 `.dpapi` 를 붙인다.
/// 로컬 데이터 암호화 키(<see cref="LocalDataCrypto"/>)도 같은 방식으로, 다른 엔트로피(<see cref="LocalDataEntropy"/>)로 둔다.
[SupportedOSPlatform("windows")]
public sealed class DpapiDeviceKeyStore(IDeviceKeyStore? inner = null, byte[]? entropy = null) : IDeviceKeyStore
{
    private static readonly byte[] Entropy = "GodDB audit device key"u8.ToArray();
    /// 로컬 데이터(히스토리 · 탭 · 저장 쿼리 · AI 대화) 키용
    public static readonly byte[] LocalDataEntropy = "GodDB local data key"u8.ToArray();
    private readonly IDeviceKeyStore _inner = inner ?? new FileDeviceKeyStore();
    private readonly byte[] _entropy = entropy ?? Entropy;

    public byte[]? Load(string account)
    {
        if (_inner.Load(account + ".dpapi") is not { } sealedData) return null;
        try { return ProtectedData.Unprotect(sealedData, _entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { return null; }
    }

    public bool Save(string account, byte[] data)
    {
        try { return _inner.Save(account + ".dpapi", ProtectedData.Protect(data, _entropy, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return false; }
    }
}
