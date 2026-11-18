using GodDB.Core.Profiles;

namespace GodDB.Core.Tests;

public class DataGripImportTests
{
    // DataGrip Ctrl+C 형식 (값은 예시)
    private const string Mssql = """
    #DataSourceSettings#
    #LocalDataSource: SALES[PRD]
    #BEGIN#
    <data-source source="LOCAL" name="SALES[PRD]" uuid="b7dca191-568c-407d-a9ca-7bcd3a995ea2"><database-info product="Microsoft SQL Server" version="15.00.4430" dbms="MSSQL"><extra-name-characters>$#@</extra-name-characters><identifier-quote-string>&quot;</identifier-quote-string></database-info><driver-ref>sqlserver.ms</driver-ref><jdbc-driver>com.microsoft.sqlserver.jdbc.SQLServerDriver</jdbc-driver><jdbc-url>jdbc:sqlserver://10.0.0.5:50585;database=SALES</jdbc-url><secret-storage>master_key</secret-storage><user-name>app_user</user-name><schema-mapping><introspection-scope><node kind="database" qname="SALES"/></introspection-scope></schema-mapping><working-dir>$ProjectFileDir$</working-dir></data-source>
    #END#
    """;

    private const string MysqlSsh = """
    #DataSourceSettings#
    #LocalDataSource: shop[DEV]
    #BEGIN#
    <data-source source="LOCAL" name="shop[DEV]" uuid="558f2f4d-ff6d-41c9-8528-e11865a982b6"><database-info product="MySQL" version="8.0.43" dbms="MYSQL_AURORA"><identifier-quote-string>`</identifier-quote-string></database-info><driver-ref>mysql.8</driver-ref><jdbc-url>jdbc:mysql://shop-dev.cluster-xyz.ap-northeast-2.rds.amazonaws.com:3306/shop_db</jdbc-url><user-name>shop_user</user-name><ssh-properties><enabled>true</enabled><ssh-config-id>ba02a190-966f-493f-98b6-fc94843a231a</ssh-config-id></ssh-properties></data-source>
    #END#
    """;

    [Fact]
    public void ParsesSQLServer()
    {
        var s = DataGripImport.Parse(Mssql);
        Assert.Single(s);
        var p = s[0].Profile;
        Assert.Equal(DatabaseKind.Mssql, p.Kind);
        Assert.Equal("SALES[PRD]", p.Name);
        Assert.True(p.Host == "10.0.0.5" && p.Port == 50585);
        Assert.Equal("SALES", p.Database);
        Assert.Equal("app_user", p.Username);
        Assert.Equal(Guid.Parse("b7dca191-568c-407d-a9ca-7bcd3a995ea2"), p.Id);
        Assert.Null(p.Ssh);
    }

    [Fact]
    public void ParsesMySQLWithSSHNote()
    {
        var s = DataGripImport.Parse(MysqlSsh);
        var p = s[0].Profile;
        Assert.Equal(DatabaseKind.Mysql, p.Kind);
        Assert.True(p.Host == "shop-dev.cluster-xyz.ap-northeast-2.rds.amazonaws.com" && p.Port == 3306);
        Assert.True(p.Database == "shop_db" && p.Username == "shop_user");
        Assert.True(p.Ssh != null && p.Ssh.Host == "");
        Assert.Contains(s[0].Notes, n => n.Contains("SSH"));
    }

    [Fact]
    public void ParsesSeveralAtOnce()
    {
        Assert.Equal([DatabaseKind.Mssql, DatabaseKind.Mysql], DataGripImport.Parse(Mssql + "\n" + MysqlSsh).Select(x => x.Profile.Kind));
    }

    [Fact]
    public void JdbcURLs()
    {
        var pg = DataGripImport.ProfileFromJdbc("jdbc:postgresql://pg.local:5433/app?sslmode=disable");
        Assert.True(pg?.Kind == DatabaseKind.Postgres && pg.Port == 5433 && pg.Database == "app" && pg.UseTLS == false);
        var my = DataGripImport.ProfileFromJdbc("jdbc:mysql://db:3307/shop?useSSL=false&serverTimezone=UTC");
        Assert.True(my?.Port == 3307 && my.UseTLS == false);
        var ms = DataGripImport.ProfileFromJdbc(@"jdbc:sqlserver://host\SQLEXPRESS;databaseName=HR;encrypt=false");
        Assert.True(ms?.Host == "host" && ms.Port == 1433 && ms.Database == "HR" && ms.UseTLS == false);
        var rd = DataGripImport.ProfileFromJdbc("jdbc:redis://cache.local:6380/2");
        Assert.True(rd?.Kind == DatabaseKind.Redis && rd.Port == 6380 && rd.Database == "2");
        Assert.Null(DataGripImport.ProfileFromJdbc("jdbc:oracle:thin:@x:1521:ORCL"));
        // 드라이버 래퍼 / 하위 프로토콜 / 여러 호스트
        var aws = DataGripImport.ProfileFromJdbc("jdbc:aws-wrapper:mysql://shop-prd.cluster-xyz.ap-northeast-2.rds.amazonaws.com:3306/shop_db");
        Assert.True(aws?.Kind == DatabaseKind.Mysql && aws.Host == "shop-prd.cluster-xyz.ap-northeast-2.rds.amazonaws.com" && aws.Port == 3306 && aws.Database == "shop_db");
        var awsPG = DataGripImport.ProfileFromJdbc("jdbc:aws-wrapper:postgresql://pg.local:5432/app");
        Assert.True(awsPG?.Kind == DatabaseKind.Postgres && awsPG.Database == "app");
        var spy = DataGripImport.ProfileFromJdbc("jdbc:p6spy:mysql://db:3306/a");
        Assert.True(spy?.Kind == DatabaseKind.Mysql && spy.Host == "db");
        var repl = DataGripImport.ProfileFromJdbc("jdbc:mysql:replication://primary:3306,replica:3307/shop?useSSL=false");
        Assert.True(repl?.Host == "primary" && repl.Port == 3306 && repl.Database == "shop" && repl.UseTLS == false);
        var jtds = DataGripImport.ProfileFromJdbc("jdbc:jtds:sqlserver://ms:1433;databaseName=HR");
        Assert.True(jtds?.Kind == DatabaseKind.Mssql && jtds.Database == "HR");
    }

    [Fact]
    public void RejectsOtherText()
    {
        var e = Assert.Throws<DataGripImportException>(() => DataGripImport.Parse("hello"));
        Assert.Equal(DataGripParseErrorKind.NotDataGrip, e.Kind);
        var oracle = Mssql.Replace("jdbc:sqlserver://10.0.0.5:50585;database=SALES", "jdbc:oracle:thin:@x:1521:ORCL");
        var e2 = Assert.Throws<DataGripImportException>(() => DataGripImport.Parse(oracle));
        Assert.Equal(DataGripParseErrorKind.NothingSupported, e2.Kind);
        Assert.Equal(["SALES[PRD]"], e2.Names);
    }
}
