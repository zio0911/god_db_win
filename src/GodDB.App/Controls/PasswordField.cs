using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform;
using GodDB.App.Services;

namespace GodDB.App.Controls;

/// 비밀번호 칸 + 보기(눈) 버튼. (Mac PasswordField)
/// NeedsAuthentication 이 참이면 보이기 전에 Windows 본인 확인(Windows Hello 또는 로그인 암호)을 거친다.
/// 자격 증명 관리자에서 읽을 수 있어도 매번 확인한다 (앱이 직접 요청).
public class PasswordField : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<PasswordField, string>(nameof(Text), "", defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string?> WatermarkProperty = AvaloniaProperty.Register<PasswordField, string?>(nameof(Watermark));
    public static readonly StyledProperty<bool> NeedsAuthenticationProperty = AvaloniaProperty.Register<PasswordField, bool>(nameof(NeedsAuthentication));
    public static readonly StyledProperty<string> AuthenticationReasonProperty = AvaloniaProperty.Register<PasswordField, string>(nameof(AuthenticationReason), "");

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string? Watermark { get => GetValue(WatermarkProperty); set => SetValue(WatermarkProperty, value); }
    public bool NeedsAuthentication { get => GetValue(NeedsAuthenticationProperty); set => SetValue(NeedsAuthenticationProperty, value); }
    public string AuthenticationReason { get => GetValue(AuthenticationReasonProperty); set => SetValue(AuthenticationReasonProperty, value); }

    /// 본인 확인을 거쳐 보였을 때
    public event Action? AuthenticatedReveal;
    /// Enter
    public event Action? Submit;

    /// 본인 확인 (기본: 플랫폼 구현. 테스트에서 바꾼다)
    public static IUserVerifier Verifier { get; set; } = PlatformServices.Verifier;

    private readonly TextBox _box = new() { PasswordChar = '•', HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _eye = new() { Content = "👁", Classes = { "borderless" }, Width = 28, Padding = new Thickness(0) };
    private bool _visible;

    public bool Revealed => _visible;

    public PasswordField()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
        grid.Children.Add(_box);
        Grid.SetColumn(_eye, 1);
        grid.Children.Add(_eye);
        Content = grid;
        _box.Bind(TextBox.TextProperty, new Binding(nameof(Text)) { Source = this, Mode = BindingMode.TwoWay });
        _box.Bind(TextBox.WatermarkProperty, new Binding(nameof(Watermark)) { Source = this });
        _box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Submit?.Invoke(); };
        _eye.Click += async (_, _) => await ToggleAsync();
        UpdateEye();
    }

    public new void Focus() => _box.Focus();

    /// 눈 버튼 (테스트에서도 부른다)
    public async Task ToggleAsync()
    {
        if (_visible) { SetVisible(false); return; }
        if (!NeedsAuthentication) { SetVisible(true); return; }
        _eye.IsEnabled = false;
        try
        {
            var handle = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (await Verifier.VerifyAsync(AuthenticationReason, handle))
            {
                SetVisible(true);
                AuthenticatedReveal?.Invoke();
            }
        }
        finally { _eye.IsEnabled = true; }
    }

    private void SetVisible(bool v)
    {
        _visible = v;
        _box.PasswordChar = v ? '\0' : '•';
        UpdateEye();
    }

    private void UpdateEye()
    {
        var tip = _visible ? Tr("비밀번호 가리기") : Tr("비밀번호 보기");
        ToolTip.SetTip(_eye, tip);
        Avalonia.Automation.AutomationProperties.SetName(_eye, tip);
        _eye.Content = _visible ? "🙈" : "👁";
    }
}
