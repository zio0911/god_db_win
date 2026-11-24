using Avalonia;
using Avalonia.Controls;

namespace GodDB.App.Infrastructure;

/// 메뉴 글자의 `_` 를 그대로 보이게 한다.
/// Avalonia 는 메뉴 머리글의 첫 `_` 를 단축키(Alt) 표시로 읽어 지워 버린다 — `tb_test_users` 가 "tbtest_users" 로 보임.
/// 이 앱은 밑줄 단축키를 쓰지 않으므로(Mac 메뉴에도 없음) 모든 MenuItem 의 글자 머리글을 `__` 로 바꿔 넣는다.
public static class MenuText
{
    private static bool _installed, _busy;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        MenuItem.HeaderProperty.Changed.AddClassHandler<MenuItem>((m, e) =>
        {
            if (_busy || e.NewValue is not string s || !s.Contains('_')) return;
            _busy = true;
            try { m.Header = Escape(s); }
            finally { _busy = false; }
        });
    }

    public static string Escape(string s) => s.Replace("_", "__");
}
