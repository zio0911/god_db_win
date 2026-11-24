using GodDB.Core.Ai;

namespace GodDB.Core.Tests;

// Mac Tests/GodDBCoreTests/TableNotesTests.swift 중 데이터 찾기가 쓰는 부분 (TableNote · 찾는 말 합치기).
// 옮기지 않은 것: storeRoundTripAndJSONKeys · readsSpecExampleAndMissingFields · autoSaveMergesTermsForSameLocation 의 저장 부분 ·
// filtersByConnectionDatabaseAndTable · updateAndDelete · contextTextForAssistant — 테이블 메모 저장소(table-notes.json)와 화면은
// Mac 1.4.3 에서 없어지고 테이블 설명으로 합쳐졌으며, Windows 판은 1.3.7 → 1.4.3 이라 그 파일이 생긴 적이 없어 옮기지 않았다.
public class TableNotesTests
{
    private static readonly Guid Profile = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void UserNotesAreConfirmedByDefault()
    {
        Assert.True(new TableNote(Profile, "d", "t", source: TableNote.NoteSource.User).Confirmed);
        Assert.False(new TableNote(Profile, "d", "t", source: TableNote.NoteSource.Ai).Confirmed);
    }

    [Fact]
    public void MergesTermsIgnoringSpacesAndCase()
    {
        Assert.Equal(["학년", "Grade"], TableNote.MergeTerms(["학년", "Grade"], ["grade", " 학 년 ", ""]));
        Assert.Equal(["학년", "학년 코드"], TableNote.MergeTerms(["학년"], ["학년 코드", "학년"]));
    }

    [Fact]
    public void SameLocationIgnoresCaseAndOrder()
    {
        var n = new TableNote(Profile, "school", "tb_student", ["grade_cd", "class_no"], ["학년"]);
        Assert.True(n.SameLocation("TB_STUDENT", ["CLASS_NO", "grade_cd"]));
        Assert.False(n.SameLocation("tb_student", ["grade_cd"]));
        Assert.Equal("tb_student.grade_cd, class_no", n.Location);
        Assert.Equal("tb_student", new TableNote(Profile, "school", "tb_student").Location);
    }

    [Fact]
    public void SplitsCommaText() => Assert.Equal(["학년", "학년코드"], TableNote.Split(" 학년, 학년코드 ,, 학년 "));
}
