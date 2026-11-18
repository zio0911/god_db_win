using System.Security.Cryptography;

namespace GodDB.Core.Platform;

/// 소프트웨어 ECDSA P-256 키 (Mac 판 `AuditChain.Signer.software` 와 같은 역할, kind "sw").
public sealed class SoftwareDeviceSigner : IDeviceSigner, IDisposable
{
    private readonly ECDsa _key;
    private readonly Lock _lock = new();

    public string Kind => "sw";
    public byte[] PublicKeyRaw { get; }

    /// 새 키를 만든다
    public SoftwareDeviceSigner() : this(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { }

    private SoftwareDeviceSigner(ECDsa key)
    {
        _key = key;
        PublicKeyRaw = RawPublicKey(key.ExportParameters(false));
    }

    /// 저장해 둔 키 (PKCS#8) 를 읽는다. P-256 이 아니거나 손상됐으면 null.
    public static SoftwareDeviceSigner? FromPkcs8(byte[] pkcs8)
    {
        ECDsa? key = null;
        try
        {
            key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            if (key.KeySize != 256) { key.Dispose(); return null; }
            return new SoftwareDeviceSigner(key);
        }
        catch (CryptographicException)
        {
            key?.Dispose();
            return null;
        }
    }

    /// 저장용 (PKCS#8)
    public byte[] ExportPkcs8()
    {
        lock (_lock) return _key.ExportPkcs8PrivateKey();
    }

    public byte[] Sign(byte[] data)
    {
        lock (_lock) return _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public void Dispose() => _key.Dispose();

    /// X‖Y (각 32바이트, 앞을 0 으로 채움)
    internal static byte[] RawPublicKey(ECParameters p)
    {
        var raw = new byte[64];
        p.Q.X!.CopyTo(raw, 32 - p.Q.X!.Length);
        p.Q.Y!.CopyTo(raw, 64 - p.Q.Y!.Length);
        return raw;
    }

    /// 원시 공개 키(X‖Y 64바이트)로 r‖s 서명을 검사한다. 키·서명 형식이 틀리면 false.
    public static bool Verify(byte[] publicKeyRaw, byte[] data, byte[] signature)
    {
        using var key = ImportPublicKey(publicKeyRaw);
        if (key == null || signature.Length != 64) return false;
        try { return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation); }
        catch (CryptographicException) { return false; }
    }

    /// 원시 공개 키(X‖Y 64바이트) → ECDsa. 곡선 위의 점이 아니면 null.
    public static ECDsa? ImportPublicKey(byte[] raw)
    {
        if (raw.Length != 64) return null;
        try
        {
            var key = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = raw[..32], Y = raw[32..] },
            });
            return key;
        }
        catch (CryptographicException) { return null; }
    }
}
