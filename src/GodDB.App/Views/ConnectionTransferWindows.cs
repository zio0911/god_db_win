using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.Controls;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 내보낼 접속과 비밀번호 포함 여부를 고르는 창 (Mac ExportConnectionsSheet)
[SheetFor(nameof(AppState.ShowExportConnections))]
public sealed class ExportConnectionsWindow : Window
{
    private readonly AppState _app;
    public HashSet<Guid> Selected { get; }
    public CheckBox IncludePasswords { get; } = new() { Content = Tr("비밀번호 포함 (암호로 잠금)") };
    public PasswordField Passphrase { get; } = new() { Watermark = Tr("암호 (8자 이상)") };
    public PasswordField Confirm { get; } = new() { Watermark = Tr("암호 확인") };
    private readonly TextBlock _count = Ui.Text("", "secondary");
    private readonly TextBlock _problem = Ui.Text("", "error");
    private readonly Button _export;
    private readonly List<CheckBox> _boxes = [];

    public string? PassphraseProblem =>
        ConnectionTransfer.PassphraseProblem(IncludePasswords.IsChecked == true, Passphrase.Text, Confirm.Text);

    public ExportConnectionsWindow(AppState app, object value)
    {
        _app = app;
        Title = Tr("접속 정보 내보내기");
        Width = 560;
        Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Selected = [.. app.Profiles.Select(p => p.Id)];

        var pwPanel = Ui.Stack(6, Passphrase, Confirm,
            Ui.Caption(Tr("가져올 때 이 암호가 필요해요. 암호는 파일에 저장되지 않으니 따로 전달하세요.")), _problem);
        pwPanel.Margin = new Thickness(20, 0, 0, 0);
        var noPw = Ui.Caption(Tr("비밀번호는 넣지 않아요. 가져온 쪽에서 처음 연결할 때 입력해요."));
        var list = new StackPanel { Spacing = 2 };
        foreach (var p in app.Profiles)
        {
            var cb = new CheckBox { IsChecked = true, Tag = p.Id };
            var label = Ui.HStack(8, new TextBlock { Text = p.Name },
                Ui.Text($"{p.Kind.Title()} · {p.Host}:{p.Port.ToString(CultureInfo.InvariantCulture)}", "secondary"));
            if (p.Ssh != null) label.Children.Add(Ui.Text("SSH", "caption"));
            cb.Content = label;
            cb.IsCheckedChanged += (_, _) => { if (cb.IsChecked == true) Selected.Add(p.Id); else Selected.Remove(p.Id); Update(); };
            _boxes.Add(cb);
            list.Children.Add(cb);
        }
        var scroll = new ScrollViewer { Content = list, MinHeight = 160 };
        IncludePasswords.IsCheckedChanged += (_, _) => Update();
        Passphrase.PropertyChanged += (_, e) => { if (e.Property == PasswordField.TextProperty) Update(); };
        Confirm.PropertyChanged += (_, e) => { if (e.Property == PasswordField.TextProperty) Update(); };
        _export = Ui.Button(Tr("내보내기…"), async () =>
        {
            var chosen = app.Profiles.Where(p => Selected.Contains(p.Id)).ToList();
            if (await app.ExportProfilesAsync(chosen, IncludePasswords.IsChecked == true ? Passphrase.Text : null))
            {
                app.ShowExportConnections = false;
                Close();
            }
        }, isDefault: true);

        var body = new DockPanel();
        var top = Ui.Stack(8, Ui.Headline("⇪ " + Tr("접속 정보 내보내기")),
            Ui.Text(Tr("고른 접속을 JSON 파일 하나로 저장해요. 다른 PC 의 GodDB 에서 가져올 수 있어요."), "secondary"));
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);
        var bottom = Ui.Stack(8,
            Ui.ButtonRow([_count], [Hover(Tr("모두 선택"), () => SetAll(true)), Hover(Tr("모두 해제"), () => SetAll(false))]),
            IncludePasswords, pwPanel, noPw,
            Ui.Caption(Tr("SSH 개인키 파일은 넣지 않고 경로만 넣어요. 받는 PC 에도 같은 경로에 키가 있어야 해요.")),
            Ui.ButtonRow([_export, Ui.Button(Tr("취소"), () => { app.ShowExportConnections = false; Close(); }, isCancel: true)]));
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(bottom);
        scroll.Margin = new Thickness(0, 8);
        body.Children.Add(scroll);
        Content = new Border { Padding = new Thickness(20), Child = body };

        void Update()
        {
            var inc = IncludePasswords.IsChecked == true;
            pwPanel.IsVisible = inc;
            noPw.IsVisible = !inc;
            _count.Text = Tr("%@개 선택", Selected.Count);
            var problem = PassphraseProblem;
            _problem.Text = problem ?? "";
            _problem.IsVisible = problem != null && Passphrase.Text.Length > 0;
            _export.IsEnabled = Selected.Count > 0 && problem == null;
        }
        _update = Update;
        Update();
    }

    private readonly Action _update;

    private void SetAll(bool on)
    {
        foreach (var b in _boxes) b.IsChecked = on;
        _update();
    }

    internal static Button Hover(string text, Action click)
    {
        var b = new Button { Content = text, Classes = { "hover" } };
        b.Click += (_, _) => click();
        return b;
    }
}

/// 가져올 접속을 고르는 창 (Mac ImportConnectionsSheet)
[SheetFor(nameof(AppState.ImportRequest))]
public sealed class ImportConnectionsWindow : Window
{
    public HashSet<Guid> Selected { get; }
    public PasswordField Passphrase { get; } = new() { Watermark = Tr("암호 (비우면 비밀번호 없이 가져옴)") };
    private readonly AppState _app;
    private readonly ImportRequest _request;
    private readonly TextBlock _error = Ui.Text("", "error");
    private readonly List<CheckBox> _boxes = [];

    public ImportConnectionsWindow(AppState app, object value)
    {
        _app = app;
        _request = (ImportRequest)value;
        var bundle = _request.Bundle;
        Title = Tr("접속 정보 가져오기");
        Width = 600;
        Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        // 새 접속은 기본 선택, 이미 있는 접속은 덮어쓰지 않도록 기본 해제
        Selected = ConnectionTransfer.DefaultSelection(bundle.Profiles, app.Profiles.ToList());
        var count = Ui.Text("", "secondary");
        var import = Ui.Button(Tr("가져오기"), RunImport, isDefault: true);
        void Update()
        {
            count.Text = Tr("%@개 선택", Selected.Count);
            import.IsEnabled = Selected.Count > 0;
        }

        var list = new StackPanel { Spacing = 4 };
        foreach (var p in bundle.Profiles)
        {
            var info = Ui.Stack(2, Ui.HStack(8, new TextBlock { Text = p.Name },
                Ui.Text($"{p.Kind.Title()} · {p.Host}:{p.Port.ToString(CultureInfo.InvariantCulture)}{(p.Database.Length == 0 ? "" : " / " + p.Database)}", "secondary")));
            foreach (var n in _request.Notes.TryGetValue(p.Id, out var notes) ? notes : [])
            {
                var t = Ui.Caption(n);
                if (n.Contains("SSH")) t.Foreground = new SolidColorBrush(Color.Parse("#FF9500"));
                info.Children.Add(t);
            }
            var action = ConnectionTransfer.Action(p, app.Profiles) switch
            {
                ImportAction.Replace r => Ui.Text(Tr("이미 있음 → 덮어씀 (%@)", r.ExistingName), "caption"),
                _ => Ui.Text(Tr("새로 추가"), "caption"),
            };
            action.Foreground = new SolidColorBrush(Color.Parse(action.Text == Tr("새로 추가") ? "#34C759" : "#FF9500"));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(info);
            Grid.SetColumn(action, 1);
            row.Children.Add(action);
            var cb = new CheckBox { Content = row, IsChecked = Selected.Contains(p.Id), HorizontalAlignment = HorizontalAlignment.Stretch };
            cb.IsCheckedChanged += (_, _) => { if (cb.IsChecked == true) Selected.Add(p.Id); else Selected.Remove(p.Id); Update(); };
            _boxes.Add(cb);
            list.Children.Add(cb);
        }

        var body = new DockPanel();
        var top = Ui.Stack(6, Ui.Headline("⇩ " + Tr("접속 정보 가져오기")),
            Ui.Text($"{_request.FileName} · {bundle.App} · {bundle.ExportedAt.LocalDateTime:g}", "secondary"));
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);
        var bottom = Ui.Stack(8,
            Ui.ButtonRow([count], [ExportConnectionsWindow.Hover(Tr("모두 선택"), () => SetAll(true)),
                                   ExportConnectionsWindow.Hover(Tr("모두 해제"), () => SetAll(false))]));
        if (bundle.HasPasswords)
            bottom.Children.Add(Ui.Stack(6, Ui.Caption(Tr("비밀번호가 암호로 잠겨 들어 있어요. 내보낼 때 정한 암호를 넣으면 함께 가져와요.")), Passphrase));
        _error.IsVisible = false;
        bottom.Children.Add(_error);
        bottom.Children.Add(Ui.ButtonRow([import, Ui.Button(Tr("취소"), () => { app.ImportRequest = null; Close(); }, isCancel: true)]));
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(bottom);
        body.Children.Add(new ScrollViewer { Content = list, Margin = new Thickness(0, 8), MinHeight = 180 });
        Content = new Border { Padding = new Thickness(20), Child = body };
        Update();

        void SetAll(bool on)
        {
            foreach (var b in _boxes) b.IsChecked = on;
        }
    }

    public void RunImport()
    {
        var passwords = new Dictionary<Guid, string>();
        if (_request.Bundle.HasPasswords && Passphrase.Text.Length > 0)
        {
            try { passwords = ConnectionTransfer.Passwords(_request.Bundle, Passphrase.Text); }
            catch (Exception e)
            {
                _error.Text = ErrorText.Describe(e).Message;
                _error.IsVisible = true;
                return;
            }
        }
        var list = _request.Bundle.Profiles.Where(p => Selected.Contains(p.Id)).ToList();
        _app.ImportProfiles(list, passwords, _request.FileName);
        _app.ImportRequest = null;
        Close();
    }
}
