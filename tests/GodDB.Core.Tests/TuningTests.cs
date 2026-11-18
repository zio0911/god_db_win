using GodDB.Core.Ai;

namespace GodDB.Core.Tests;

// "Tuning live" (로컬 Ollama + MySQL 테스트 DB 필요) 는 tests/GodDB.Integration.Tests/TuningTests.cs 에 있다 (GODDB_OLLAMA_URL 이 있을 때만).

public class TuningParseTests
{
    [Fact]
    public void ParsesSections()
    {
        var text = """
            <think>internal</think>
            ## 요약
            status 조건에 인덱스가 없어 풀 스캔해요.

            ## 개선 SQL
            ```sql
            SELECT member_id FROM tb_member WHERE status = 'A'
            ```

            ## 추천 인덱스
            ```sql
            CREATE INDEX idx_member_status ON tb_member (status);
            ```

            ## 설명
            - 인덱스로 스캔 범위를 줄여요.
            """;
        var r = TuningResult.Parse(text);
        Assert.StartsWith("status 조건", r.Summary);
        Assert.Equal("SELECT member_id FROM tb_member WHERE status = 'A'", r.ImprovedSql);
        Assert.True(r.IndexSql?.StartsWith("CREATE INDEX idx_member_status"));
        Assert.True(r.HasIndexes);
        Assert.Contains("스캔 범위", r.Explanation);
        Assert.DoesNotContain("internal", r.Raw);
    }

    [Fact]
    public void NoIndexesAndEnglishHeadings()
    {
        var r = TuningResult.Parse("""
            ## Summary
            Fine already.
            ## Improved SQL
            ```sql
            SELECT 1
            ```
            ## Recommended indexes
            ```sql
            -- none needed
            ```
            ## Explanation
            - nothing
            """);
        Assert.Equal("SELECT 1", r.ImprovedSql);
        Assert.False(r.HasIndexes);
        Assert.Equal("Fine already.", r.Summary);
    }
}
