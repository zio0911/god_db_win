using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// Redis 화면: 키 · Workbench · Pub/Sub · Streams · 서버 정보. DataContext = RedisWorkspace — Mac RedisWorkspaceView
/// 탭 내용은 처음 열 때 만들고 접속이 끊길 때까지 그대로 둔다 (입력 중인 글 유지).
public partial class RedisWorkspaceView : UserControl
{
    private RedisWorkspace? _bound;
    private readonly Dictionary<RedisTab, Control> _views = [];
    private Window? _newKey;

    public RedisWorkspaceView()
    {
        InitializeComponent();
        foreach (var (button, tab) in TabButtons())
            button.Click += (_, _) => { if (Ws is { } ws) ws.Tab = tab; UpdateTabs(); };
        DataContextChanged += (_, _) => Bind();
    }

    public RedisWorkspace? Ws => DataContext as RedisWorkspace;

    private IEnumerable<(ToggleButton Button, RedisTab Tab)> TabButtons() =>
    [
        (TabKeys, RedisTab.Keys), (TabConsole, RedisTab.Console), (TabPubSub, RedisTab.PubSub),
        (TabStreams, RedisTab.Streams), (TabInfo, RedisTab.Info),
    ];

    private void Bind()
    {
        if (_bound != null) _bound.PropertyChanged -= OnWsChanged;
        _views.Clear();
        _bound = Ws;
        if (_bound != null) _bound.PropertyChanged += OnWsChanged;
        UpdateTabs();
    }

    private void OnWsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RedisWorkspace.Tab)) UpdateTabs();
        if (e.PropertyName == nameof(RedisWorkspace.ShowNewKey) && Ws is { ShowNewKey: true } ws) OpenNewKey(ws);
    }

    /// 지금 탭의 내용 (테스트용)
    public Control? CurrentView => Host.Content as Control;

    private void UpdateTabs()
    {
        if (Ws is not { } ws) { Host.Content = null; return; }
        foreach (var (button, tab) in TabButtons()) button.IsChecked = tab == ws.Tab;
        if (!_views.TryGetValue(ws.Tab, out var view))
        {
            view = ws.Tab switch
            {
                RedisTab.Console => new RedisConsoleView(ws),
                RedisTab.PubSub => new RedisPubSubView(ws),
                RedisTab.Streams => new RedisStreamsView(ws),
                RedisTab.Info => new RedisInfoView(ws),
                _ => new RedisKeyView(ws),
            };
            _views[ws.Tab] = view;
        }
        Host.Content = view;
        // 탭을 열 때 읽기 (Mac .task)
        switch (ws.Tab)
        {
            case RedisTab.PubSub: ws.Fire(ws.LoadActiveChannelsAsync); break;
            case RedisTab.Streams when ws.StreamKeys.Count == 0: ws.Fire(ws.LoadStreamKeysAsync); break;
            case RedisTab.Info when ws.Info.Count == 0: ws.Fire(ws.LoadInfoAsync); break;
        }
    }

    /// 새 키 창은 여기 한 곳에서만 연다 (Mac: 목록 섹션에 붙이면 창이 반복해서 열려서)
    private void OpenNewKey(RedisWorkspace ws)
    {
        if (_newKey != null) return;
        var w = new RedisNewKeyWindow(ws);
        _newKey = w;
        w.Closed += (_, _) => { _newKey = null; ws.ShowNewKey = false; };
        if (TopLevel.GetTopLevel(this) is Window owner && owner.IsVisible) _ = w.ShowDialog(owner);
        else w.Show();
    }

    /// 열린 새 키 창 (테스트용)
    public Window? NewKeyWindow => _newKey;
}
