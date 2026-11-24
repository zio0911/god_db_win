using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.Controls;
using GodDB.App.Infrastructure;
using GodDB.App.Services;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 도움말 > 이전 버전 설치… (Mac 1.4.0 PreviousVersionsView. Mac 은 앱 메뉴 GodDB 아래, Windows 는 도움말 메뉴의 업데이트 확인 옆)
[SheetFor(nameof(AppState.ShowPreviousVersions))]
public sealed class PreviousVersionsWindow : Window
{
    private readonly AppState _app;
    public PreviousVersionInstaller Installer { get; }
    /// 버전을 고른 뒤 확인 (설치할지, 이 버전에 머물지). 테스트는 바꿔 끼운다.
    public Func<PreviousVersions.Entry, Task<(bool Ok, bool Stay)>> Confirm { get; set; }

    private readonly ContentControl _content = new();
    private readonly ContentControl _status = new();
    private readonly Button _close;

    public PreviousVersionsWindow(AppState app, object value) : this(app, value, null) { }

    public PreviousVersionsWindow(AppState app, object value, PreviousVersionInstaller? installer)
    {
        _app = app;
        Installer = installer ?? new PreviousVersionInstaller();
        Confirm = ConfirmWithDialogAsync;
        Title = Tr("이전 버전 설치");
        Width = 620;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _close = Ui.Button(Tr("닫기"), Close, isCancel: true);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(Ui.Text("🕘 " + Tr("이전 버전 설치"), "title"));
        var current = Ui.Text(Tr("지금 버전 %@", PreviousVersionInstaller.CurrentVersion), "secondary");
        current.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(current, 1);
        header.Children.Add(current);

        var github = new Button { Content = Tr("GitHub 에서 직접 받기"), Classes = { "link" } };
        github.Click += (_, _) => app.Dialogs.OpenExternal(PreviousVersions.ReleasesPage.ToString());

        var body = new DockPanel { LastChildFill = true };
        var bottom = Ui.Stack(10,
            _status,
            Ui.Caption(Tr("이전 버전에는 이후에 추가된 기능이 없어요. 문제가 해결되면 메뉴에서 업데이트 확인으로 최신으로 돌아오세요.")),
            Ui.ButtonRow([_close], [github]));
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        header.Margin = new Thickness(0, 0, 0, 12);
        bottom.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(header);
        body.Children.Add(bottom);
        body.Children.Add(_content);
        Content = new Border { Padding = new Thickness(20), Child = body };

        Installer.PropertyChanged += OnInstallerChanged;
        Refresh();
        Opened += (_, _) => app.Fire(Installer.LoadAsync);
        Closing += (_, e) => { if (Installer.Busy) e.Cancel = true; };
    }

    private void OnInstallerChanged(object? sender, PropertyChangedEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        _content.Content = BuildContent();
        _status.Content = BuildStatus();
        _close.IsEnabled = !Installer.Busy;
    }

    private Control BuildContent()
    {
        switch (Installer.List)
        {
            case PreviousVersionInstaller.ListStatus.Loading:
                return Center(Ui.HStack(8, new ActivitySpinner { Width = 16, Height = 16 }, Ui.Text(Tr("릴리스 목록을 가져오는 중…"))));
            case PreviousVersionInstaller.ListStatus.Failed:
                var retry = Ui.Button(Tr("다시 시도"), () => _app.Fire(Installer.LoadAsync));
                var github = new Button { Content = Tr("GitHub 에서 직접 받기"), Classes = { "link" } };
                github.Click += (_, _) => _app.Dialogs.OpenExternal(PreviousVersions.ReleasesPage.ToString());
                var failed = Ui.Stack(8, Ui.Text("📡", "title"), Ui.Headline(Tr("릴리스 목록을 가져오지 못했어요")),
                                      Ui.Caption(Installer.ListError), Ui.HStack(8, retry, github));
                foreach (var c in failed.Children) c.HorizontalAlignment = HorizontalAlignment.Center;
                return Center(failed);
        }
        if (Installer.Entries.Count == 0) return Center(Ui.Text(Tr("설치할 수 있는 다른 버전이 없어요"), "secondary"));
        var list = new StackPanel { Spacing = 0 };
        var odd = false;
        foreach (var e in Installer.Entries)
        {
            list.Children.Add(Row(e, odd));
            odd = !odd;
        }
        return new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            CornerRadius = new CornerRadius(4),
            Child = new ScrollViewer { Content = list },
        };
    }

    private static Control Center(Control c)
    {
        c.HorizontalAlignment = HorizontalAlignment.Center;
        c.VerticalAlignment = VerticalAlignment.Center;
        return c;
    }

    /// Mac 의 Markdown 표시 대신 굵게(**) · 코드(`) 표시만 뗀다
    internal static string Plain(string markdown) => markdown.Replace("**", "").Replace("`", "");

    private Control Row(PreviousVersions.Entry e, bool odd)
    {
        var title = Ui.HStack(8, new TextBlock { Text = e.Version, FontWeight = FontWeight.SemiBold });
        if (e.Date is { } d) title.Children.Add(Ui.Caption(d.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        if (PreviousVersions.Compare(e.Version, PreviousVersionInstaller.CurrentVersion) > 0)
            title.Children.Add(new TextBlock { Text = Tr("더 새 버전"), FontWeight = FontWeight.Bold, FontSize = 11, Foreground = Brushes.Orange, VerticalAlignment = VerticalAlignment.Center });
        var left = Ui.Stack(4, title);
        foreach (var line in e.Notes)
        {
            var t = Ui.Caption("• " + Plain(line));
            t.MaxLines = 2;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            left.Children.Add(t);
        }
        var install = Ui.Button(Tr("설치…"), () => _app.Fire(() => ChooseAsync(e)));
        install.IsEnabled = !Installer.Busy;
        install.VerticalAlignment = VerticalAlignment.Top;
        install.Tag = e.Version;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(left);
        Grid.SetColumn(install, 1);
        grid.Children.Add(install);
        return new Border
        {
            Padding = new Thickness(10, 6),
            Background = odd ? new SolidColorBrush(Color.FromArgb(18, 128, 128, 128)) : Brushes.Transparent,
            Child = grid,
        };
    }

    /// 설치 버튼 → 확인 → 설치
    public async Task ChooseAsync(PreviousVersions.Entry entry)
    {
        if (Installer.Busy) return;
        var (ok, stay) = await Confirm(entry);
        if (!ok) return;
        await Installer.InstallAsync(entry, stay, _app);
    }

    private async Task<(bool, bool)> ConfirmWithDialogAsync(PreviousVersions.Entry entry)
    {
        var stay = new CheckBox { Content = Tr("이 버전에 머물기 (자동 업데이트를 '수동'으로 바꿈)"), IsChecked = true };
        var result = false;
        Window? dialog = null;
        var install = Ui.Button(Tr("설치"), () => { result = true; dialog?.Close(); }, isDefault: true);
        var cancel = Ui.Button(Tr("취소"), () => dialog?.Close(), isCancel: true);
        dialog = Ui.Dialog(Tr("%@ 으로 되돌릴까요?", entry.Version), 460, Ui.Stack(12,
            Ui.Headline(Tr("%@ 으로 되돌릴까요?", entry.Version)),
            Ui.Text(Tr("앱을 끄고 이전 버전을 설치한 뒤 다시 켭니다. 접속 · 탭 · 설정은 그대로 남고, 설치 전에 백업합니다.")),
            stay,
            Ui.ButtonRow([install, cancel])));
        await dialog.ShowDialog(this);
        return (result, stay.IsChecked == true);
    }

    private Control? BuildStatus()
    {
        var version = Installer.Target ?? "";
        return Installer.Phase switch
        {
            PreviousVersionInstaller.InstallPhase.BackingUp => Progress(Tr("데이터 폴더 백업 중…"), null, cancellable: true),
            PreviousVersionInstaller.InstallPhase.Verifying => Progress(Tr("설치 파일 확인 중…"), null, cancellable: true),
            PreviousVersionInstaller.InstallPhase.Downloading =>
                Progress(Tr("%@ 내려받는 중… %@%%", version, (int)(Installer.Progress * 100)), Installer.Progress, cancellable: true),
            PreviousVersionInstaller.InstallPhase.Launching => Progress(Tr("앱을 끄고 %@ 를 설치해요…", version), null, cancellable: false),
            PreviousVersionInstaller.InstallPhase.Failed => new SelectableTextBlock
            {
                Text = "⚠️ " + Tr("설치하지 않았어요: %@", Installer.Failure),
                TextWrapping = TextWrapping.Wrap,
            },
            _ => null,
        };
    }

    private Control Progress(string title, double? value, bool cancellable)
    {
        Control indicator = value is { } v
            ? new ProgressBar { Minimum = 0, Maximum = 1, Value = v, Width = 220, VerticalAlignment = VerticalAlignment.Center }
            : new ActivitySpinner { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        row.Children.Add(indicator);
        var text = Ui.Text(title);
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        if (cancellable)
        {
            var cancel = Ui.Button(Tr("취소"), Installer.Cancel);
            Grid.SetColumn(cancel, 2);
            row.Children.Add(cancel);
        }
        return row;
    }
}
