using System.Security.Cryptography;
using System.Text;
using GodDB.Core.Profiles;

namespace GodDB.Core.Tests;

/// DBeaver 접속 정보 가져오기 (Mac DBeaverImportTests)
public class DBeaverImportTests
{
    /// DBeaver 26.2 에서 파일 > 내보내기 > DBeaver > 프로젝트 로 만든 .dbp (Docker 테스트 DB 접속).
    /// 폴더 Docker Test / Docker Test/Bastion, MySQL(읽기전용) · PostgreSQL · SQL Server(SSH 공개키),
    /// Redshift(가짜 호스트, 연결 안 됨 — 형식 확인용), 샘플 SQLite.
    /// credentials-config.json 에는 사용자 이름만 있다 (비밀번호 없음). meta.xml 의 주소 · 호스트 이름은 바꿔 둠.
    internal static readonly byte[] Dbp = Convert.FromBase64String("""
    UEsDBBQAAgAIADd2d11CSqiaCgEAAEcCAAAIAAAAbWV0YS54bWyFkt1OwzAMhV8lyn3TtdoYk9pOQggeAHiA4HhdpjQpcZh4fNI2
    ZWjlR7mx7C/Hx06q/Udn2Bk9aWdrXogVZ2jBKW3bmr88P2S3fN9U0sNRn/EbyJuK3LsHZEF3GBPb3WZdlLubdVlsOJNKeSSK+XIr
    VvEUnB0dhZoHpJB1EoRxIA3Pm6r37oQQ6CtiVg6Sj2jRR4QpJPC6D2PnlGX3dyijHZbuRD+x4eRoui7U60gsK+BRoQ1aGsrA2YNu
    xYmcHbxckUoGmU0Z+o1JBrIOgxz4/zjCEOJ2L3r5DC5HmNnlDM63AsHonlCA8yjmOone44H+Fn4a9/mD7FQQ9GauBPJk/xLF98rT
    v2g+AVBLAwQKAAAAAAA3dnddAAAAAAAAAAAAAAAACQAAAHByb2plY3RzL1BLAwQKAAAAAAA3dnddAAAAAAAAAAAAAAAAEQAAAHBy
    b2plY3RzL0dlbmVyYWwvUEsDBAoAAAAAADd2d10AAAAAAAAAAAAAAAAaAAAAcHJvamVjdHMvR2VuZXJhbC8uZGJlYXZlci9QSwME
    FAACAAgAN3Z3XQZbO4BkAAAAfAAAAC8AAABwcm9qZWN0cy9HZW5lcmFsLy5kYmVhdmVyL3Byb2plY3QtbWV0YWRhdGEuanNvbj3M
    SwqEMBCE4bv02ihuvYDM2hO0SQkZMpPY3c5GcndfMKuC+uDfSaB5Ew+lYafJSyym3bOtrul6Axbekrki+Q1vr0ADjfhCOFHz18DG
    T+rkMIN/EKf8KQm3zaxwZzEaXE+11gNQSwMECgACAAAAN3Z3XeG1WqJwAQAAcAEAADEAAABwcm9qZWN0cy9HZW5lcmFsLy5kYmVh
    dmVyL2NyZWRlbnRpYWxzLWNvbmZpZy5qc29umIKQspFyAOE21Jfq5ikUcfpuHcaW1b43LVroYbHfrvfri/J/XIvcN46rQ8gH+O3B
    x3PEso6Dw+dYx4I/tyYpGSxHcvAZmxSr0PBrymI8M3DkD2ywanpCRM8uJtRiazqITJZhpWvH+T3ZWG9PunfQk+5lYtReG8qBwBLj
    uxhXT2luzEd/Der5q5Vc3mLF3YSnD+rCbc+X923y3gLohoHU7IUkksQaBJzVItx95YdvEDdMHp56zpngbBqWMSdMKSk3j/ShJAr9
    LjBDcY7x1ExA8jQ25eNSz7GmNidwUupLaAM9aXnpajVMzske3bAaUJ1U154odGBsXRIowXDZbmHLo1mUd/aTC/8uva5pbCtThqbq
    l2fn4d0J3psaCKCZLnHn2i9NXa+1a0TUK6Bcata49REcmf8TtAkGzVL/oP70Dy9zkCVXBf523gzN+f1qHt15YjuOWYaacmZGdSIc
    9pD+vqWoNFcsJr4JACsBEfBUSLZQSwMECgACAAAAN3Z3XTwinKstAAAALQAAAC8AAABwcm9qZWN0cy9HZW5lcmFsLy5kYmVhdmVy
    L3Byb2plY3Qtc2V0dGluZ3MuanNvbnsiaWQiOiIyODQ2Y2YzMS0xMmE1LTQyYjQtYWU4Yi04ZWM4ZWE1MDE0ZjQifVBLAwQUAAIA
    CAA3dnddeWwCE2cEAADIDgAAKwAAAHByb2plY3RzL0dlbmVyYWwvLmRiZWF2ZXIvZGF0YS1zb3VyY2VzLmpzb27VV21P20gQ/hx+
    ReRPdxJrx3ZiSPjSAtX1VIpoAx9Od6do7Z1gg+317a5DcxX32zvrdyemLa1a6UAoaGZ23p5nZrMfD0bGmscMhDQW448Ho5FxzoN7
    EONrkEqLHg93hNYplSriaaE8GGm9EfA0hUBLGzfMB7oBQSRNshgIo4r6VAKR/8SRAmJXdiMjE3wTYQIoMEqlcVgomIg2XfHqjvlB
    pUtpAlpzflpEGS+LKOPzKsr4l+W7Czzya2Uu0YhkVMoHLhieUyKHShPyByK3UkFCuH+HRcieHitbR7e5oHXJWoq5VYF0DtaNxPZZ
    CnsDwrqIfEHF1qoy0xlZGPVeZjQAzzITUFSftp5qjHUWRinn9ybzy+xHRi5iHUjXvyjNFj8+aK/y621W1Pr25eXNy4vaRFVSBhtD
    S5AOo5IvyRY9HxObOhPvaObN3WMyC+YTx55S6viOy8AbJEBxbg//0tsO9L9xdn46frtFpL+IsgDKCE/jbU9aEr+gUYfzn8U95MVU
    GLZzZE7w165bkXFRKFzXnXi1sMsSJFo2hGdR28KyGo+LwoXVtX8mFHVKVV8J/pOBUBHIppCR8aIZUD0BNI4J8+UL7UI3qICzxBJt
    aa5CknAGRdopprGBHcAzbMytAEl0URXuHnPmU3LkzAOYeOyI2Wss0hnEvT4+AH7P8yAHrkqLlgg/BNjZ1HWeA2xbUh9d7ecno5vy
    lDBY0zxWiHIB8prG8vkoJ1EguORrVSE8D9zjGcEPtp6B7XvOMXj20VOrHTeWxnRvumunwwO+RFzJcvn6i0M+iHpzW30n+vbUdYfQ
    T6hewE8s6rJghP+k/O8SK/srn0xc1gQ60RFKWRHjpHbe2vZD/JxdIIMQMG6HKoeVrZTxtcilWhYVnWlf6yigCr6KVU1TVk0TK7uQ
    pixuv4aUocKVyvGbRdwK2+quby4vX9U1oxxS6sfQo8Rn+VK1Za8bKL7Lk6yszwx4nmoGIFiHjb6mi19yy4QPxbVqqmbNdMnj4E/3
    tO5IDdzVzenF72erN6/+6Jy7h+0VVaHW/2eZ2AXrljPmr7T7FTBnNrPnHfNIx04gVTWpdePuOgYypAKui0bKnQ6MjJgHNH5dFdQ5
    JCDhChpFJX886Hw8PnEFCGAyjHBJtHfB2mG4K8jUtz1vMoP52p7pLwPffBf0QgzujfeVxbetja9bFxXuJIhzPaIm9QPbcU2a4cYV
    KgSkB3HMOleTJvRfntIHiaxKdjcMXgvzoQXTGeLudqmd4nL5niwWOqzVifH/uIz2nh1EJ9W+PTCzmlnNSwE2EPNMz0kDbswL3HGg
    Dqu/ruqcivthNQMZiCirx+093OYxFWPWhhj315suhxPsdxKp/eeFSAh8gCAvdmjRi75S+yIBLsjbHQOZUKFat09ocFgCXg5QG7hM
    KOb4BlCCppI2r7dOcntqguBGXM+PfTyZ7HnqPwN3HXW0HT/Tae1IYgtEpLbtnKFVEkmp8cWhVyJqfP/5d5cLB4+fAFBLAwQKAAAA
    AAA3dnddAAAAAAAAAAAAAAAAGwAAAHByb2plY3RzL0dlbmVyYWwvLnNldHRpbmdzL1BLAwQKAAIAAAA3dnddDejeTTcAAAA3AAAA
    OwAAAHByb2plY3RzL0dlbmVyYWwvLnNldHRpbmdzL29yZy5lY2xpcHNlLmNvcmUucmVzb3VyY2VzLnByZWZzZWNsaXBzZS5wcmVm
    ZXJlbmNlcy52ZXJzaW9uPTEKZW5jb2RpbmcvPHByb2plY3Q+PVVURi04ClBLAwQKAAAAAAA3dnddAAAAAAAAAAAAAAAAGQAAAHBy
    b2plY3RzL0dlbmVyYWwvU2NyaXB0cy9QSwMECgAAAAAAN3Z3XQAAAAAAAAAAAAAAACMAAABwcm9qZWN0cy9HZW5lcmFsL1Njcmlw
    dHMvU2NyaXB0LnNxbFBLAQIeAxQAAgAIADd2d11CSqiaCgEAAEcCAAAIAAAAAAAAAAEAAACkgQAAAABtZXRhLnhtbFBLAQIeAwoA
    AAAAADd2d10AAAAAAAAAAAAAAAAJAAAAAAAAAAAAEADtQTABAABwcm9qZWN0cy9QSwECHgMKAAAAAAA3dnddAAAAAAAAAAAAAAAA
    EQAAAAAAAAAAABAA7UFXAQAAcHJvamVjdHMvR2VuZXJhbC9QSwECHgMKAAAAAAA3dnddAAAAAAAAAAAAAAAAGgAAAAAAAAAAABAA
    7UGGAQAAcHJvamVjdHMvR2VuZXJhbC8uZGJlYXZlci9QSwECHgMUAAIACAA3dnddBls7gGQAAAB8AAAALwAAAAAAAAABAAAApIG+
    AQAAcHJvamVjdHMvR2VuZXJhbC8uZGJlYXZlci9wcm9qZWN0LW1ldGFkYXRhLmpzb25QSwECHgMKAAIAAAA3dndd4bVaonABAABw
    AQAAMQAAAAAAAAAAAAAApIFvAgAAcHJvamVjdHMvR2VuZXJhbC8uZGJlYXZlci9jcmVkZW50aWFscy1jb25maWcuanNvblBLAQIe
    AwoAAgAAADd2d108IpyrLQAAAC0AAAAvAAAAAAAAAAEAAACkgS4EAABwcm9qZWN0cy9HZW5lcmFsLy5kYmVhdmVyL3Byb2plY3Qt
    c2V0dGluZ3MuanNvblBLAQIeAxQAAgAIADd2d115bAITZwQAAMgOAAArAAAAAAAAAAEAAACkgagEAABwcm9qZWN0cy9HZW5lcmFs
    Ly5kYmVhdmVyL2RhdGEtc291cmNlcy5qc29uUEsBAh4DCgAAAAAAN3Z3XQAAAAAAAAAAAAAAABsAAAAAAAAAAAAQAO1BWAkAAHBy
    b2plY3RzL0dlbmVyYWwvLnNldHRpbmdzL1BLAQIeAwoAAgAAADd2d10N6N5NNwAAADcAAAA7AAAAAAAAAAEAAACkgZEJAABwcm9q
    ZWN0cy9HZW5lcmFsLy5zZXR0aW5ncy9vcmcuZWNsaXBzZS5jb3JlLnJlc291cmNlcy5wcmVmc1BLAQIeAwoAAAAAADd2d10AAAAA
    AAAAAAAAAAAZAAAAAAAAAAAAEADtQSEKAABwcm9qZWN0cy9HZW5lcmFsL1NjcmlwdHMvUEsBAh4DCgAAAAAAN3Z3XQAAAAAAAAAA
    AAAAACMAAAAAAAAAAAAAAKSBWAoAAHByb2plY3RzL0dlbmVyYWwvU2NyaXB0cy9TY3JpcHQuc3FsUEsFBgAAAAAMAAwAsAMAAJkK
    AAAAAA==
    """);

    private static ConnectionProfile? Profile(DBeaverImport.Result r, string name) =>
        r.Sources.FirstOrDefault(s => s.Profile.Name == name)?.Profile;

    [Fact]
    public void ReadsExportedProject()
    {
        var r = DBeaverImport.Parse(Dbp);
        Assert.Equal(4, r.Sources.Count);
        Assert.Equal(["DBeaver Sample Database (SQLite)"], r.Skipped);

        // DBeaver 26.2 Redshift: provider "postgresql" + driver "postgres-redshift-jdbc" → PostgreSQL 접속, TLS 켬
        var rs = Assert.IsType<ConnectionProfile>(Profile(r, "GodDB Redshift"));
        Assert.Equal(DatabaseKind.Postgres, rs.Kind);
        Assert.True(rs.Host == "example-cluster.abc123.ap-northeast-2.redshift.amazonaws.com" && rs.Port == 5439);
        Assert.Equal("dev", rs.Database);
        Assert.Equal("awsuser", rs.Username);
        Assert.True(rs.UseTLS);
        Assert.Equal("Docker Test", rs.Group);

        var my = Assert.IsType<ConnectionProfile>(Profile(r, "GodDB MySQL"));
        Assert.Equal(DatabaseKind.Mysql, my.Kind);
        Assert.True(my.Host == "127.0.0.1" && my.Port == 33306);
        Assert.Equal("shop", my.Database);
        Assert.Equal("root", my.Username);
        Assert.True(my.ReadOnly);
        Assert.Equal("Docker Test", my.Group);
        Assert.Null(my.Ssh);
        Assert.False(my.UseTLS);

        var pg = Assert.IsType<ConnectionProfile>(Profile(r, "GodDB PostgreSQL"));
        Assert.Equal(DatabaseKind.Postgres, pg.Kind);
        Assert.True(pg.Host == "127.0.0.1" && pg.Port == 35432);
        Assert.Equal("shop", pg.Database);
        Assert.Equal("postgres", pg.Username);
        Assert.False(pg.ReadOnly);
        Assert.Equal("Docker Test", pg.Group);

        var ms = Assert.IsType<ConnectionProfile>(Profile(r, "GodDB MSSQL-SSH"));
        Assert.Equal(DatabaseKind.Mssql, ms.Kind);
        Assert.True(ms.Host == "127.0.0.1" && ms.Port == 31433);
        Assert.Equal("master", ms.Database);
        Assert.Equal("sa", ms.Username);
        Assert.Equal("Docker Test/Bastion", ms.Group);
        var ssh = Assert.IsType<SshConfig>(ms.Ssh);
        Assert.True(ssh.Host == "bastion.example.test" && ssh.Port == 2222);
        Assert.Equal("tunnel", ssh.Username);
        Assert.Equal("~/.ssh/goddb_test_ed25519", ssh.KeyPath);
    }

    [Fact]
    public void NotesAskForPasswordOnly()
    {
        var r = DBeaverImport.Parse(Dbp);
        foreach (var s in r.Sources)
            Assert.True(s.Notes.SequenceEqual([L10n.Tr("비밀번호는 처음 연결할 때 입력해요.")]), $"{s.Profile.Name}: {string.Join(" / ", s.Notes)}");
    }

    [Fact]
    public void SameDBeaverConnectionKeepsID()
    {
        var a = DBeaverImport.Parse(Dbp);
        var b = DBeaverImport.Parse(Dbp);
        Assert.Equal(a.Sources.Select(s => s.Profile.Id), b.Sources.Select(s => s.Profile.Id));
        Assert.Equal(4, a.Sources.Select(s => s.Profile.Id).Distinct().Count());
        // 다시 가져오면 덮어쓰기로 인식
        var existing = a.Sources.Select(s => s.Profile).ToList();
        Assert.IsNotType<ImportAction.Add>(ConnectionTransfer.Action(b.Sources[0].Profile, existing));
    }

    [Fact]
    public void ReadsDataSourcesJSONWithSiblingCredentials()
    {
        var files = ZipReader.Read(Dbp, p => p.Contains("/.dbeaver/"));
        var dir = Path.Combine(Path.GetTempPath(), "dbeaver-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var name in new[] { "data-sources.json", "credentials-config.json" })
                File.WriteAllBytes(Path.Combine(dir, name), files["projects/General/.dbeaver/" + name]);
            var r = DBeaverImport.ParseFile(Path.Combine(dir, "data-sources.json"));
            Assert.Equal(4, r.Sources.Count);
            Assert.Equal("tunnel", Profile(r, "GodDB MSSQL-SSH")?.Ssh?.Username);

            // 비밀번호 파일이 없으면 사용자 이름 없이 가져오고 알려준다
            File.Delete(Path.Combine(dir, "credentials-config.json"));
            var r2 = DBeaverImport.ParseFile(Path.Combine(dir, "data-sources.json"));
            var my = Assert.Single(r2.Sources, s => s.Profile.Name == "GodDB MySQL");
            Assert.Empty(my.Profile.Username);
            Assert.Contains(L10n.Tr("사용자 이름이 없어요. 접속 편집에서 넣어 주세요."), my.Notes);
            Assert.Contains(L10n.Tr("SSH 사용자 이름이 없어요. 접속 편집에서 넣어 주세요."),
                r2.Sources.First(s => s.Profile.Name == "GodDB MSSQL-SSH").Notes);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CredentialsGiveUserNames()
    {
        var files = ZipReader.Read(Dbp, p => p.EndsWith("credentials-config.json", StringComparison.Ordinal));
        var users = DBeaverImport.UsersInCredentials(files.Values.FirstOrDefault() ?? []);
        Assert.Equal(new DBeaverImport.Users("sa", "tunnel"), users["microsoft-1a20679c385-79cdf5e1b628e617"]);
        Assert.Equal(4, users.Count);
        // 깨진 파일은 빈 결과
        Assert.Empty(DBeaverImport.UsersInCredentials(Enumerable.Repeat((byte)7, 48).ToArray()));
    }

    // 손으로 만든 data-sources.json (옛 폴더 형식, URL 방식, SSH 비밀번호 방식, SSL)
    private const string HandMade = """
    {
      "folders": { "운영": {}, "주문": { "parent": "운영" } },
      "connections": {
        "pg-url-1": { "provider": "postgresql", "driver": "postgres-jdbc", "name": "주문 DB", "folder": "주문",
          "configuration": { "url": "jdbc:postgresql://db.example.test:6543/orders", "configurationType": "URL", "user": "app",
            "handlers": {
              "postgre_ssl": { "type": "CONFIG", "enabled": true, "properties": { "sslMode": "require" } },
              "ssh_tunnel": { "type": "TUNNEL", "enabled": true,
                "properties": { "host": "jump.example.test", "port": "22", "authType": "PASSWORD", "jumpServer.count": 1 } } } } },
        "maria-1": { "provider": "mysql", "driver": "mariaDB", "name": "마리아",
          "configuration": { "host": "maria.example.test", "port": 3307, "database": "app",
            "handlers": { "ssh_tunnel": { "type": "TUNNEL", "enabled": false, "properties": { "host": "x" } } } } },
        "oracle-1": { "provider": "oracle", "driver": "oracle_thin", "name": "오라클", "configuration": { "host": "o.example.test" } }
      }
    }
    """;

    [Fact]
    public void HandMadeDataSources()
    {
        var r = DBeaverImport.Parse(Encoding.UTF8.GetBytes(HandMade));
        Assert.Equal(["오라클"], r.Skipped);
        var pg = Assert.Single(r.Sources, s => s.Profile.Name == "주문 DB");
        Assert.Equal("운영/주문", pg.Profile.Group);
        Assert.True(pg.Profile.Host == "db.example.test" && pg.Profile.Port == 6543);
        Assert.Equal("orders", pg.Profile.Database);
        Assert.Equal("app", pg.Profile.Username);
        Assert.True(pg.Profile.UseTLS && !pg.Profile.TlsVerify);
        Assert.Equal("jump.example.test", pg.Profile.Ssh?.Host);
        Assert.Equal("", pg.Profile.Ssh?.KeyPath);
        Assert.Contains(L10n.Tr("SSH 비밀번호 방식이에요. GodDB 는 키 파일이나 ssh-agent 로 연결하니 접속 편집에서 키를 지정해 주세요."), pg.Notes);
        Assert.Contains(L10n.Tr("SSH 점프 서버 설정은 가져오지 않았어요."), pg.Notes);

        var maria = Assert.Single(r.Sources, s => s.Profile.Name == "마리아");
        Assert.True(maria.Profile.Kind == DatabaseKind.Mysql && maria.Profile.Port == 3307);
        Assert.Null(maria.Profile.Ssh);
        Assert.Empty(maria.Profile.Group);
    }

    // Redshift: 새 DBeaver(postgresql + postgres-redshift-jdbc), 옛 DBeaver(generic + aws_redshift), URL 방식
    [Fact]
    public void RedshiftBecomesPostgresWithTLS()
    {
        const string json = """
        {"connections": {
          "rs-new": { "provider": "postgresql", "driver": "postgres-redshift-jdbc", "name": "분석",
            "configuration": { "host": "example-cluster.abc123.ap-northeast-2.redshift.amazonaws.com", "port": "5439",
                               "database": "dev", "user": "analyst" } },
          "rs-old": { "provider": "generic", "driver": "aws_redshift_42", "name": "옛 분석",
            "configuration": { "host": "old.example.test", "database": "dev", "provider-properties": { "ssl": "false" } } },
          "rs-url": { "provider": "postgresql", "driver": "postgres-redshift-jdbc", "name": "주소 분석",
            "configuration": { "url": "jdbc:redshift://url.example.test:5440/sales", "configurationType": "URL" } }
        }}
        """;
        var r = DBeaverImport.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Empty(r.Skipped);
        var @new = r.Sources.Single(s => s.Profile.Name == "분석").Profile;
        Assert.True(@new.Kind == DatabaseKind.Postgres && @new.Port == 5439 && @new.Database == "dev" && @new.Username == "analyst");
        Assert.True(@new.UseTLS && @new.TlsVerify);
        Assert.True(@new.Redshift && @new.Type == ConnectionType.Redshift);
        var old = r.Sources.Single(s => s.Profile.Name == "옛 분석").Profile;
        Assert.True(old.Kind == DatabaseKind.Postgres && old.Port == 5439);
        Assert.False(old.UseTLS);
        var url = r.Sources.Single(s => s.Profile.Name == "주소 분석").Profile;
        Assert.True(url.Host == "url.example.test" && url.Port == 5440 && url.Database == "sales" && url.UseTLS);

        var jdbc = Assert.IsType<ConnectionProfile>(DataGripImport.ProfileFromJdbc("jdbc:redshift://c.example.test/dev"));
        Assert.True(jdbc.Kind == DatabaseKind.Postgres && jdbc.Port == 5439 && jdbc.UseTLS && jdbc.Redshift);
    }

    /// DBeaver 에 저장된 DB 비밀번호도 가져온다 (Mac #195). 비밀번호가 없는 접속은 지금처럼 처음 연결할 때 입력
    [Fact]
    public void SavedPasswordsAreImported()
    {
        const string creds = """{"pg-1":{"#connection":{"user":"app","password":"s3cret"}},"my-1":{"#connection":{"user":"root"}}}""";
        var iv = Enumerable.Repeat((byte)7, 16).ToArray();
        using var aes = Aes.Create();
        aes.Key = DBeaverImport.CredentialsKey;
        byte[] file = [.. iv, .. aes.EncryptCbc(Encoding.UTF8.GetBytes(creds), iv, PaddingMode.PKCS7)];
        var users = DBeaverImport.UsersInCredentials(file);
        Assert.Equal(new DBeaverImport.Users("app", null, "s3cret"), users["pg-1"]);
        Assert.Null(users["my-1"].Password);

        const string ds = """{"connections":{"pg-1":{"provider":"postgresql","driver":"postgres-jdbc","name":"pg","configuration":{"host":"h.example.test","port":"5432","database":"d"}},"my-1":{"provider":"mysql","driver":"mysql8","name":"my","configuration":{"host":"m.example.test","port":"3306"}}}}""";
        var r = DBeaverImport.Parse(Encoding.UTF8.GetBytes(ds), credentials: file);
        var pg = r.Sources.Single(s => s.Profile.Name == "pg");
        Assert.True(pg.Password == "s3cret" && pg.Profile.Username == "app");
        Assert.Contains(L10n.Tr("DBeaver 에 저장된 비밀번호도 가져와 자격 증명 관리자에 넣어요."), pg.Notes);
        var my = r.Sources.Single(s => s.Profile.Name == "my");
        Assert.True(my.Password == null && my.Notes.Contains(L10n.Tr("비밀번호는 처음 연결할 때 입력해요.")));
    }

    [Fact]
    public void Errors()
    {
        Assert.Equal(DBeaverParseErrorKind.NotDBeaver,
            Assert.Throws<DBeaverImportException>(() => DBeaverImport.Parse(Encoding.UTF8.GetBytes("{\"format\":\"goddb-connections\"}"))).Kind);
        Assert.Equal(DBeaverParseErrorKind.NoConnections,
            Assert.Throws<DBeaverImportException>(() => DBeaverImport.Parse(Encoding.UTF8.GetBytes("{\"connections\":{}}"))).Kind);
        const string onlySQLite = """{"connections":{"s":{"provider":"sqlite","name":"로컬","configuration":{"database":"/tmp/a.db"}}}}""";
        var e = Assert.Throws<DBeaverImportException>(() => DBeaverImport.Parse(Encoding.UTF8.GetBytes(onlySQLite)));
        Assert.Equal(DBeaverParseErrorKind.NothingSupported, e.Kind);
        Assert.Equal(["로컬"], e.Names);
        Assert.False(DBeaverImport.LooksLikeDBeaver(Encoding.UTF8.GetBytes("{\"format\":\"goddb-connections\",\"profiles\":[]}")));
        Assert.True(DBeaverImport.LooksLikeDBeaver(Dbp));
    }

    /// Windows: DBeaver 기본 작업 폴더는 %APPDATA%\DBeaverData\workspace6 (Mac 은 ~/Library/DBeaverData/workspace6)
    [Fact]
    public void DefaultWorkspaceIsUnderAppData()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Equal(Path.Combine(appData, "DBeaverData", "workspace6", "General", ".dbeaver"), DBeaverImport.DefaultWorkspaceFolder);
    }

    /// 접속 id 는 Mac 과 같은 UUID (두 판에서 가져온 접속이 같은 접속으로 인식되게)
    [Fact]
    public void StableIdMatchesMacByteOrder()
    {
        var id = DBeaverImport.StableId("x");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("dbeaver:x"));
        var hex = Convert.ToHexString(hash[..16]).ToLowerInvariant();
        var s = id.ToString("N");
        Assert.Equal(hex[..12], s[..12]);           // 앞 6바이트는 그대로 (big-endian)
        Assert.Equal('5', s[12]);                     // 버전 5 모양
        Assert.Equal(hex[18..], s[18..]);           // 9번째 바이트 다음부터 그대로
    }
}
