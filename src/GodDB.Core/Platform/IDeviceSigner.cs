namespace GodDB.Core.Platform;

/// 감사 로그 서명에 쓰는 이 PC 의 기기 키 (ECDSA P-256).
/// Mac 판은 Secure Enclave 키를 우선 쓰고, Windows 판은 TPM 키를 `GodDB.Platform.Windows` 에서 넘길 수 있다
/// (<see cref="GodDB.Core.Audit.AuditChain.PlatformSigner"/>). Core 에는 소프트웨어 키(<see cref="SoftwareDeviceSigner"/>)만 있다.
public interface IDeviceSigner
{
    /// 공개 키 원시 값: X‖Y 64바이트 (CryptoKit P256 PublicKey.rawRepresentation 과 같음)
    byte[] PublicKeyRaw { get; }

    /// 키 종류: "sw" (소프트웨어), "se" (Mac Secure Enclave), "tpm" (Windows TPM) …
    string Kind { get; }

    /// data 를 SHA-256 으로 해시해 서명한다. 서명은 r‖s 64바이트 (IEEE P1363 = CryptoKit ECDSASignature.rawRepresentation)
    byte[] Sign(byte[] data);
}

/// 기기 키를 보관하는 곳. 기본은 <see cref="FileDeviceKeyStore"/> (본인만 읽을 수 있는 파일).
/// Windows 판은 DPAPI 로 잠근 저장소를 넘길 수 있다 (<see cref="GodDB.Core.Audit.AuditChain.KeyStore"/>).
public interface IDeviceKeyStore
{
    byte[]? Load(string account);

    /// 처음 만든 키를 저장한다. 실패하면 false (그러면 무결성 칸 없이 쓴다).
    bool Save(string account, byte[] data);
}
