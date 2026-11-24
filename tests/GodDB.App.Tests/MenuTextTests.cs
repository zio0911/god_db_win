using Avalonia.Controls;
using GodDB.App.Infrastructure;

namespace GodDB.App.Tests;

/// 메뉴 글자의 `_` 가 단축키 표시로 사라지지 않는다 (화면 확인에서 "참조 따라가기 → tbtest_users" 로 보이던 문제)
public class MenuTextTests
{
    [Fact]
    public Task UnderscoreStaysVisibleInMenuHeader() => UiThread.Run(() =>
    {
        var m = new MenuItem { Header = "참조 따라가기 → tb_test_users (user_id)" };
        Assert.Equal("참조 따라가기 → tb__test__users (user__id)", m.Header);
        var plain = new MenuItem { Header = "복사" };
        Assert.Equal("복사", plain.Header);
        Assert.Equal("a__b", MenuText.Escape("a_b"));
    });
}
