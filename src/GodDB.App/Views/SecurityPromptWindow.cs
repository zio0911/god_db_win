using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// TLS 서버 인증서 · SSH 호스트 키 확인 창 (Mac SecurityPromptView). 메인 창은 AppState.SecurityPrompt 로 열고,
/// 접속 편집의 연결 테스트는 같은 창을 직접 띄운다 (Show(owner) → 결과).
[SheetFor(nameof(AppState.SecurityPrompt))]
public sealed class SecurityPromptWindow : Window
{
    public SecurityPrompt Prompt { get; }
    public Button AcceptButton { get; }
    public Button CancelButton { get; }
    /// [신뢰] · [지우기] 를 눌렀는지 (닫히면 정해진다)
    public bool Accepted { get; private set; }

    /// 메인 창: 결과를 AppState 로 넘긴다
    public SecurityPromptWindow(AppState app, object value) : this(((SecurityPromptRequest)value).Prompt, ((SecurityPromptRequest)value).ProfileName)
    {
        var request = (SecurityPromptRequest)value;
        // [신뢰] · [지우기] 도 창을 닫으므로, 닫힐 때 Accepted 를 보고 한 번만 넘긴다
        Closed += (_, _) =>
        {
            if (ReferenceEquals(app.SecurityPrompt, request)) app.Fire(() => app.ResolveSecurityPromptAsync(request, Accepted));
        };
    }

    /// 접속 편집: 결과는 Accepted 로 읽는다
    public SecurityPromptWindow(SecurityPrompt prompt, string profileName)
    {
        Prompt = prompt;
        Title = prompt.Title(profileName);
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var stack = Ui.Stack(12);
        var title = Ui.Headline((prompt is SecurityPrompt.TlsUntrusted or SecurityPrompt.SshUnknown ? "⚠ " : "⛔ ") + prompt.Title(profileName));
        if (prompt.IsRemoval) title.Classes.Add("error");
        stack.Children.Add(title);
        foreach (var c in Body(prompt)) stack.Children.Add(c);

        AcceptButton = Ui.Button(prompt.AcceptTitle, () => { Accepted = true; Close(); });
        if (prompt.IsRemoval) AcceptButton.Classes.Add("danger");
        CancelButton = Ui.Button(Tr("취소"), Close, isCancel: true);
        stack.Children.Add(Ui.ButtonRow([CancelButton, AcceptButton]));
        Content = new Border { Padding = new Thickness(20), Child = stack };
    }

    private static IEnumerable<Control> Body(SecurityPrompt prompt)
    {
        switch (prompt)
        {
            case SecurityPrompt.TlsUntrusted u:
                yield return CertGrid(u.Info);
                yield return Row(Tr("이유"), string.Join(" / ", u.Reasons.Select(r => r.Text())));
                yield return Ui.Text(Tr("처음 연결하는 서버라면, 관리자에게 받은 지문과 같은지 확인하세요."), "secondary");
                break;
            case SecurityPrompt.TlsPinMismatch m:
                yield return Ui.Text(Tr("서버 인증서가 바뀌었어요. 서버를 교체한 게 아니라면 중간에서 가로채는 공격일 수 있어요. 관리자에게 확인한 뒤 [저장된 인증서 지우기] 를 누르세요."));
                yield return CertGrid(m.Info);
                break;
            case SecurityPrompt.SshUnknown s:
                var keys = Ui.Stack(4);
                foreach (var key in s.Scan.Keys)
                {
                    var g = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*") };
                    var type = Ui.Text(key.Type, "mono");
                    var fp = new SelectableTextBlock { Text = key.Fingerprint, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "mono" } };
                    Grid.SetColumn(fp, 1);
                    g.Children.Add(type);
                    g.Children.Add(fp);
                    keys.Children.Add(g);
                }
                yield return keys;
                yield return Ui.Text(Tr("서버 관리자에게 받은 지문과 같은지 확인하세요."), "secondary");
                break;
            case SecurityPrompt.SshChanged:
                yield return Ui.Text(Tr("SSH 서버 키가 바뀌었어요. 서버를 다시 설치한 게 아니라면 중간에서 가로채는 공격일 수 있어요. 관리자에게 확인한 뒤 [저장된 키 지우기] (GodDB 전용 파일에서만 지움)."));
                // Mac: "~/.ssh/known_hosts … 터미널에서 ssh-keygen -R" — Windows 경로 · 명령 프롬프트 표기로
                yield return Ui.Caption(Tr("%USERPROFILE%\\.ssh\\known_hosts 에 있는 키는 지우지 않아요. 그 파일의 키가 바뀐 거라면 명령 프롬프트에서 ssh-keygen -R 로 지우세요."));
                break;
        }
    }

    private static Control CertGrid(TlsCertInfo info) => Ui.Stack(4,
        Row(Tr("주체"), info.Subject),
        Row(Tr("발급"), info.Issuer),
        Row(Tr("유효"), info.ValidityText),
        Row("SHA-256", info.Sha256));

    private static Control Row(string label, string value)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*") };
        var l = Ui.Text(label, "secondary");
        l.VerticalAlignment = VerticalAlignment.Top;
        var v = new SelectableTextBlock { Text = value, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "mono" } };
        Grid.SetColumn(v, 1);
        g.Children.Add(l);
        g.Children.Add(v);
        return g;
    }
}
