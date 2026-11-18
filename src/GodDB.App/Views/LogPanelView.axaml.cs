using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

public partial class LogPanelView : UserControl
{
    private AppState? _app;
    /// 화면에 보이는 항목 (최신이 위)
    public ObservableCollection<LogEntry> Visible { get; } = [];
    private bool _syncing;

    public LogPanelView()
    {
        InitializeComponent();
        List.ItemsSource = Visible;
        ErrorsOnly.IsCheckedChanged += (_, _) => Rebuild();
        CopyAll.Click += (_, _) => _app?.Fire(() => _app.Dialogs.SetClipboardTextAsync(string.Join("\n\n", Visible.Reverse().Select(v => v.FullText))));
        LogFolder.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(LogFile.Shared.Directory); } catch { }
            _app?.Dialogs.OpenExternal(LogFile.Shared.Directory);
        };
        ToolTip.SetTip(LogFolder, Tr(@"날짜별 감사 로그(goddb-audit-날짜.tsv) 폴더를 열어요 (%LOCALAPPDATA%\GodDB\Logs)"));
        Report.Click += (_, _) => { if (_app != null) _app.ShowProblemReport = true; };
        Clear.Click += (_, _) => _app?.ClearLogs();
        CloseButton.Click += (_, _) => { if (_app != null) _app.ShowLog = false; };
        List.SelectionChanged += (_, _) =>
        {
            if (_syncing || _app == null) return;
            _app.SelectedLogId = (List.SelectedItem as LogEntry)?.Id;
        };
        L10n.LanguageChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => { UpdateEmpty(); ShowDetail(); });
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not AppState app || ReferenceEquals(app, _app)) return;
        _app = app;
        app.Logs.CollectionChanged += OnLogsChanged;
        app.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(AppState.SelectedLogId)) SyncSelection(); };
        Rebuild();
    }

    private bool Passes(LogEntry e) => ErrorsOnly.IsChecked != true || e.Level is LogLevel.Error or LogLevel.Warning;

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            foreach (LogEntry n in e.NewItems) if (Passes(n)) Visible.Insert(0, n);
            UpdateEmpty();
            SyncSelection();
        }
        else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
        {
            foreach (LogEntry o in e.OldItems) Visible.Remove(o);
            UpdateEmpty();
        }
        else Rebuild();
    }

    private void Rebuild()
    {
        Visible.Clear();
        if (_app == null) return;
        foreach (var e in _app.Logs.Reverse()) if (Passes(e)) Visible.Add(e);
        UpdateEmpty();
        SyncSelection();
    }

    private void UpdateEmpty()
    {
        var empty = Visible.Count == 0;
        EmptyText.Text = ErrorsOnly.IsChecked == true ? Tr("오류가 없어요.") : Tr("아직 기록이 없어요.");
        EmptyText.IsVisible = empty;
        Body.IsVisible = !empty;
        CopyAll.IsEnabled = !empty;
        Clear.IsEnabled = _app?.Logs.Count > 0;
        LogFolder.IsVisible = LogFile.Enabled;
    }

    private void SyncSelection()
    {
        if (_app == null) return;
        _syncing = true;
        try
        {
            var entry = Visible.FirstOrDefault(v => v.Id == _app.SelectedLogId);
            List.SelectedItem = entry;
            if (entry != null) List.ScrollIntoView(entry);
        }
        finally { _syncing = false; }
        ShowDetail();
    }

    private void ShowDetail()
    {
        Detail.Children.Clear();
        var entry = _app?.Logs.FirstOrDefault(l => l.Id == _app.SelectedLogId);
        if (entry == null)
        {
            Detail.Children.Add(new TextBlock { Text = Tr("항목을 선택하면 자세한 내용이 나와요."), Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap });
            return;
        }
        Detail.Children.Add(Ui.HStack(6,
            new TextBlock { Text = (string)Conv.LevelIcon.Convert(entry.Level, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture)!,
                            Foreground = (IBrush)Conv.LevelBrush.Convert(entry.Level, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture)! },
            new SelectableTextBlock { Text = entry.Message, FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 }));
        if (entry.Hint is { } hint)
            Detail.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1AFFCC00")),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8),
                Child = Ui.HStack(6, new TextBlock { Text = "💡" }, new SelectableTextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 }),
            });
        if (entry.Detail is { } detail)
            Detail.Children.Add(new SelectableTextBlock { Text = detail, Classes = { "mono", "secondary" }, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        var copy = new Button { Content = Tr("이 항목 복사"), Classes = { "hover" } };
        copy.Click += (_, _) => _app?.Fire(() => _app.Dialogs.SetClipboardTextAsync(entry.FullText));
        Detail.Children.Add(copy);
    }
}
