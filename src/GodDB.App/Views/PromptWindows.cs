using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using GodDB.App.Controls;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 비밀번호를 저장하지 않는 접속이거나, 저장된 비밀번호가 틀렸을 때 뜨는 입력창 (Mac ContentView.PasswordPromptView)
[SheetFor(nameof(AppState.PasswordPrompt))]
public sealed class PasswordPromptWindow : Window
{
    public PasswordField Field { get; } = new();
    public CheckBox SaveBox { get; }
    private readonly AppState _app;
    private readonly PasswordPrompt _prompt;

    public PasswordPromptWindow(AppState app, object value)
    {
        _app = app;
        _prompt = (PasswordPrompt)value;
        var p = _prompt.Profile;
        Title = Tr("%@ 비밀번호", p.Name);
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SaveBox = new CheckBox { Content = Tr("Windows 자격 증명 관리자에 비밀번호 저장"), IsChecked = p.SavePassword };
        var hint = Ui.Caption("");
        void UpdateHint() => hint.Text = SaveBox.IsChecked == true
            ? Tr("다음부터는 묻지 않고 바로 연결해요.")
            : Tr("앱을 끄기 전까지만 기억하고, 다음 실행 때 다시 물어봐요.");
        SaveBox.IsCheckedChanged += (_, _) => UpdateHint();
        UpdateHint();
        Field.Watermark = Tr("비밀번호");
        Field.Submit += Submit;
        var stack = Ui.Stack(12,
            Ui.Headline("🔑 " + Tr("%@ 비밀번호", p.Name)),
            Ui.Text($"{p.Username}@{p.Host}:{p.Port}", "secondary", "mono"));
        if (_prompt.Message is { } m) stack.Children.Add(Ui.Text(m, "error"));
        stack.Children.Add(Field);
        stack.Children.Add(SaveBox);
        stack.Children.Add(hint);
        stack.Children.Add(Ui.ButtonRow([
            Ui.Button(Tr("연결"), Submit, isDefault: true),
            Ui.Button(Tr("취소"), () => { _app.PasswordPrompt = null; Close(); }, isCancel: true),
        ]));
        Content = new Border { Padding = new Thickness(20), Child = stack };
        Opened += (_, _) => Field.Focus();
    }

    public void Submit()
    {
        _app.SubmitPassword(Field.Text, SaveBox.IsChecked == true, _prompt.Profile);
        Close();
    }
}

/// 접속 그룹 이름 입력 (새 그룹으로 옮기기 / 그룹 이름 바꾸기)
[SheetFor(nameof(AppState.GroupPrompt))]
public sealed class GroupPromptWindow : Window
{
    public GroupPromptWindow(AppState app, object value)
    {
        var prompt = (GroupPrompt)value;
        var rename = prompt.Action is GroupPromptAction.Rename;
        Title = rename ? Tr("그룹 이름 바꾸기") : Tr("새 그룹");
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var box = new TextBox { Text = prompt.Initial, Watermark = Tr("그룹 이름 (예: 운영, 개발, 프로젝트명)") };
        Button ok = null!;
        void Submit()
        {
            if (box.Text?.Trim().Length is null or 0) return;
            app.SubmitGroupPrompt(prompt, box.Text ?? "");
            Close();
        }
        ok = Ui.Button(Tr("확인"), Submit, isDefault: true);
        void Update() => ok.IsEnabled = box.Text?.Trim().Length > 0;
        box.TextChanged += (_, _) => Update();
        Update();
        Content = new Border
        {
            Padding = new Thickness(20),
            Child = Ui.Stack(12,
                Ui.Headline((rename ? "📁 " : "📁＋ ") + Title),
                box,
                Ui.ButtonRow([ok, Ui.Button(Tr("취소"), () => { app.GroupPrompt = null; Close(); }, isCancel: true)])),
        };
        Opened += (_, _) => { box.Focus(); box.SelectAll(); };
    }
}
