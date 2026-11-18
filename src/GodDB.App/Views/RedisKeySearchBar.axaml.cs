using Avalonia.Controls;
using Avalonia.Input;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 사이드바 아래 Redis 키 검색 (패턴 + 타입). DataContext = RedisWorkspace (없으면 null) — Mac RedisKeySearchBar
public partial class RedisKeySearchBar : UserControl
{
    private bool _syncing;

    public RedisKeySearchBar()
    {
        InitializeComponent();
        // 첫 줄 = 모든 타입 ("")
        TypeBox.ItemsSource = new[] { Tr("모든 타입") }.Concat(RedisWorkspace.KeyTypes).ToList();
        TypeBox.SelectedIndex = 0;
        PatternBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Ws is not { } ws) return;
            e.Handled = true;
            ws.Fire(ws.ReloadKeysAsync);
        };
        TypeBox.SelectionChanged += (_, _) =>
        {
            if (_syncing || Ws is not { } ws) return;
            var type = TypeBox.SelectedIndex <= 0 ? "" : RedisWorkspace.KeyTypes[TypeBox.SelectedIndex - 1];
            if (type == ws.TypeFilter) return;
            ws.TypeFilter = type;
            ws.Fire(ws.ReloadKeysAsync);
        };
        DataContextChanged += (_, _) => SyncType();
    }

    public RedisWorkspace? Ws => DataContext as RedisWorkspace;

    private void SyncType()
    {
        _syncing = true;
        var i = Ws is { } ws ? RedisWorkspace.KeyTypes.ToList().IndexOf(ws.TypeFilter) : -1;
        TypeBox.SelectedIndex = i + 1;
        _syncing = false;
    }
}
