using GodDB.Core.Results;
using static GodDB.Core.Results.InlineCellEdit;

namespace GodDB.Core.Tests;

/// Mac: InlineCellEditTests.swift
public class InlineCellEditTests
{
    [Fact]
    public void InitialTextIsValueOrEmpty()
    {
        Assert.Equal("kim", InitialText("kim"));
        Assert.Equal("", InitialText(null));
        Assert.Equal("", InitialText(""));
    }

    [Fact]
    public void UnchangedTextRecordsNothing()
    {
        Assert.Null(CommittedValue("kim", "kim", Finish.Commit));
        Assert.Null(CommittedValue("kim", "kim", Finish.Next));
        // NULL 칸을 열고 아무것도 안 쓰면 NULL 그대로 (빈 문자열로 바꾸지 않는다)
        Assert.Null(CommittedValue(null, "", Finish.Commit));
    }

    [Fact]
    public void ChangedTextIsCommitted()
    {
        Assert.Equal("lee", CommittedValue("kim", "lee", Finish.Commit));
        Assert.Equal("lee", CommittedValue("kim", "lee", Finish.Previous));
        Assert.Equal("x", CommittedValue(null, "x", Finish.Next));
        // 지우면 빈 문자열 (NULL 아님)
        Assert.Equal("", CommittedValue("kim", "", Finish.Commit));
        // 바이너리 · JSON 도 글자 그대로
        Assert.Equal("0xFF", CommittedValue("0x00", "0xFF", Finish.Commit));
        Assert.Equal("{\"a\":1}", CommittedValue("{}", "{\"a\":1}", Finish.Commit));
    }

    [Fact]
    public void CancelRecordsNothing() => Assert.Null(CommittedValue("kim", "lee", Finish.Cancel));

    [Fact]
    public void MultilineValuesGoToPanel()
    {
        Assert.True(CanEditInline("one line"));
        Assert.True(CanEditInline(null));
        Assert.False(CanEditInline("a\nb"));
        Assert.False(CanEditInline("a\r\nb"));
    }

    [Fact]
    public void TabMovesWithinEditableColumns()
    {
        int[] editable = [3, 0, 1];   // 2 는 테이블 컬럼이 아님 (계산 컬럼 등)
        Assert.Equal(1, Neighbor(0, editable, Finish.Next));
        Assert.Equal(3, Neighbor(1, editable, Finish.Next));
        Assert.Null(Neighbor(3, editable, Finish.Next));
        Assert.Equal(1, Neighbor(3, editable, Finish.Previous));
        Assert.Null(Neighbor(0, editable, Finish.Previous));
        Assert.Null(Neighbor(1, editable, Finish.Commit));
    }
}
