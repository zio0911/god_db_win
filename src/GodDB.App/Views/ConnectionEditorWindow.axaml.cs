using Avalonia.Controls;
using GodDB.App.Services;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 접속 편집 (Mac ConnectionEditorView). AppState.EditingProfile 이 생기면 열리고, 저장/취소하면 닫힌다.
[SheetFor(nameof(AppState.EditingProfile))]
public partial class ConnectionEditorWindow : Window
{
    public ConnectionEditorViewModel Vm { get; } = null!;

    /// XAML 미리보기용
    public ConnectionEditorWindow() => InitializeComponent();

    public ConnectionEditorWindow(AppState app, object value)
    {
        InitializeComponent();
        Vm = new ConnectionEditorViewModel(app, (ConnectionProfile)value, app.EditingIsNew);
        DataContext = Vm;
        PasswordBox.AuthenticatedReveal += Vm.OnAuthenticatedReveal;
        // 연결 테스트에서 만난 인증서 · SSH 호스트 키 확인 창 (F9-3 · F9-4)
        Vm.AskSecurity = async prompt =>
        {
            var w = new SecurityPromptWindow(prompt, Vm.Name);
            await w.ShowDialog(this);
            return w.Accepted;
        };
        PickKeyButton.Click += async (_, _) => await PickKeyAsync();
        GroupPick.Click += (_, _) => ShowGroups();
        GroupPick.IsVisible = app.ProfileGroups.Count > 0;
        Opened += (_, _) => NameBox.Focus();
    }

    private void ShowGroups()
    {
        var items = new List<Control>();
        foreach (var g in Vm.App.ProfileGroups)
        {
            var m = new MenuItem { Header = g };
            m.Click += (_, _) => Vm.Group = g;
            items.Add(m);
        }
        items.Add(new Separator());
        var none = new MenuItem { Header = Tr("그룹 없음") };
        none.Click += (_, _) => Vm.Group = "";
        items.Add(none);
        new ContextMenu { ItemsSource = items }.Open(GroupPick);
    }

    private async Task PickKeyAsync()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var files = await new WindowDialogs(this).OpenFilesAsync(Tr("키 파일"), [], startFolder: Path.Combine(home, ".ssh"));
        if (files.Count > 0) Vm.SshKeyPath = files[0];
    }
}
