using System.Text;
using GodDB.Core.Drivers;
using GodDB.Core.Platform;

namespace GodDB.Core.Tests;

/// Mac MSSQLTLSTests.swift — SQL Server 인증서 확인 (F9-3): PRELOGIN 패킷 · 다시 확인하는 설정.
/// Mac 의 두 번째 단계(FreeTDS ca file · freetds.conf 섹션)는 Windows 에서 SqlClient 설정(HostNameInCertificate ·
/// ServerCertificate)으로 바뀌었으므로 같은 판정 표를 SqlClient 설정으로 확인한다 (MssqlTls.cs 머리 주석).
public class MSSQLTLSTests
{
    [Fact]
    public void PreloginPacket()
    {
        var payload = MssqlTls.PreloginPayload();
        // 옵션 5개(5바이트씩) + 끝(0xFF) 뒤에 값, ENCRYPTION = ON
        Assert.Equal(0xFF, payload[25]);
        Assert.Equal(MssqlTls.Encryption.On, MssqlTls.EncryptionIn(payload));
        var packets = MssqlTls.Packets(payload);
        Assert.Equal(new byte[] { 0x12, 0x01, 0x00, (byte)(payload.Length + 8), 0, 0, 1, 0 }, packets.Take(8).ToArray());
        // 큰 페이로드는 나눠 담고 마지막에만 EOM
        var big = MssqlTls.Packets(Enumerable.Repeat((byte)7, 5000).ToArray(), maxSize: 4096);
        Assert.Equal(5000 + 16, big.Length);
        Assert.True(big[1] == 0x00 && (big[2] << 8 | big[3]) == 4096);
        Assert.True(big[4096 + 1] == 0x01 && big[4096 + 6] == 2);
    }

    [Fact]
    public void PreloginResponseEncryption()
    {
        // 테스트 서버의 실제 응답: VERSION · ENCRYPTION=ON · INSTOPT · THREADID · MARS
        byte[] response = [0x00, 0x00, 0x1a, 0x00, 0x06, 0x01, 0x00, 0x20, 0x00, 0x01, 0x02, 0x00, 0x21, 0x00, 0x01,
                           0x03, 0x00, 0x22, 0x00, 0x00, 0x04, 0x00, 0x22, 0x00, 0x01, 0xff,
                           0x10, 0x00, 0x10, 0xdb, 0x00, 0x00, 0x01, 0x00, 0x00];
        Assert.Equal(MssqlTls.Encryption.On, MssqlTls.EncryptionIn(response));
        var notSup = (byte[])response.Clone();
        notSup[32] = 0x02;
        Assert.Equal(MssqlTls.Encryption.NotSupported, MssqlTls.EncryptionIn(notSup));
        Assert.Null(MssqlTls.EncryptionIn([0x01, 0x00]));
    }

    /// Mac freeTDSCheckFromVerdict → SqlClient 에 넘길 확인 설정
    [Fact]
    public void SqlClientCheckFromVerdict()
    {
        var der = TLSVerifyTests.SelfSignedDER();
        var direct = new TlsPolicy(true, "db.example.com", null);
        // 시스템 신뢰 저장소로 통과(고정 없음) → SqlClient 가 체인 + 이름 다시 확인 (HostNameInCertificate)
        Assert.Equal(new MssqlTls.SqlClientCheck(false, "db.example.com", null), MssqlTls.CheckFor(TlsVerdict.Trusted, [der], direct));
        // 고정 → 그 인증서와 정확히 같아야 (ServerCertificate), 이름 확인 안 함
        var pinned = MssqlTls.CheckFor(TlsVerdict.Pinned, [der], direct with { Pin = TLSVerifyTests.SelfSignedSHA });
        Assert.False(pinned.TrustServerCertificate);
        Assert.Null(pinned.HostName);
        Assert.Equal(der, pinned.ServerCertificateDer);
        // 통과했지만 고정 지문도 있음 → 지문까지 지키게 ServerCertificate
        Assert.Equal(der, MssqlTls.CheckFor(TlsVerdict.Trusted, [der], direct with { Pin = TLSVerifyTests.SelfSignedSHA }).ServerCertificateDer);
        // 확인 안 함 · 거부는 암호화만 (거부는 연결 전에 예외로 끝난다)
        Assert.True(MssqlTls.CheckFor(TlsVerdict.Unchecked, [der], new TlsPolicy(true, null, null)).TrustServerCertificate);
        Assert.True(MssqlTls.CheckFor(TlsVerdict.PinMismatch, [der], direct).TrustServerCertificate);
        Assert.Equal(der, TlsVerify.DerFromPem(TlsVerify.Pem(der)));
    }

    /// Mac configSection (0600 파일로 쓰고, 끝나면 지운다) → ServerCertificate 임시 파일
    [Fact]
    public void ServerCertificateFileIsPrivate()
    {
        var der = TLSVerifyTests.SelfSignedDER();
        var path = MssqlTls.WriteServerCertificate(der);
        try
        {
            Assert.Equal(der, File.ReadAllBytes(path));
            Assert.True(UserOnlyFile.IsRestricted(path));
            Assert.StartsWith("goddb-", Path.GetFileName(path));
        }
        finally { File.Delete(path); }
    }

    /// `host\인스턴스`: SQL Browser 응답에서 TCP 포트
    [Fact]
    public void BrowserResponse()
    {
        var text = "ServerName;DB01;InstanceName;SQLEXPRESS;IsClustered;No;Version;16.0.1000.6;tcp;49172;;";
        var bytes = new byte[] { 0x05, (byte)text.Length, 0 }.Concat(Encoding.ASCII.GetBytes(text)).ToArray();
        Assert.Equal(49172, MssqlTls.ParseBrowserResponse(bytes));
        Assert.Null(MssqlTls.ParseBrowserResponse([0x05, 0, 0]));
        Assert.Null(MssqlTls.ParseBrowserResponse([0x01]));
    }
}
