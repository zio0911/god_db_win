using GodDB.Core.Dialects;

namespace GodDB.Core.Tests;

/// Mac PostgresQuoteTests.swift — PostgreSQL 문자열 상수: 백슬래시가 있으면 E'…'
/// (standard_conforming_strings 설정과 무관하게 같은 값, #197). 실제 서버 검사는 Integration PostgresQuoteIntegrationTests.
public class PostgresQuoteTests
{
    [Fact]
    public void BackslashUsesEscapeString()
    {
        var pg = new PostgresDialect();
        Assert.Equal("'it''s'", pg.QuoteString("it's"));
        Assert.Equal(@"E'C:\\temp\\a''b'", pg.QuoteString(@"C:\temp\a'b"));
        // Redshift 는 E'' 를 쓰지 않는다
        Assert.Equal(@"'a\b'", new PostgresDialect(redshift: true).QuoteString(@"a\b"));
    }
}
