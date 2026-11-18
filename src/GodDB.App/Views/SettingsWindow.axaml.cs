using Avalonia.Controls;
using Avalonia.Input;
using GodDB.App.Services;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 설정 창 (Ctrl+,): 일반 · AI · 업데이트 · 라이선스 (Mac SettingsView). 언어를 바꾸면 이 창과 메인 창이 바로 새 언어로 바뀐다.
public partial class SettingsWindow : Window
{
    public SettingsViewModel Vm { get; }

    public SettingsWindow() : this(null) { }

    public SettingsWindow(AppState? app)
    {
        InitializeComponent();
        Vm = new SettingsViewModel(app, app?.Dialogs is NullDialogs or null ? new WindowDialogs(this) : app.Dialogs);
        VersionRow.PointerPressed += (_, e) => { if (e.GetCurrentPoint(VersionRow).Properties.IsLeftButtonPressed) Vm.VersionTapped(); };
        DataContext = Vm;
        BuildRadios();
        L10n.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => L10n.LanguageChanged -= OnLanguageChanged;
        Opened += (_, _) =>
        {
            Vm.ResetVersionTaps();
            _ = Vm.RefreshAsync();
        };
        UrlBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) _ = Vm.RefreshAsync(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void OnLanguageChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        BuildRadios();
        Vm.RefreshTexts();
    });

    private void BuildRadios()
    {
        LanguagePanel.Children.Clear();
        foreach (var lang in Vm.Languages)
        {
            var rb = new RadioButton { GroupName = "lang", Content = L10n.DisplayName(lang), IsChecked = Vm.Language == lang };
            rb.IsCheckedChanged += (_, _) => { if (rb.IsChecked == true && Vm.Language != lang) Vm.Language = lang; };
            LanguagePanel.Children.Add(rb);
        }
        UpdatePanel.Children.Clear();
        foreach (var mode in Vm.UpdateModes)
        {
            var rb = new RadioButton { GroupName = "update", Content = UpdateService.Title(mode), IsChecked = Vm.UpdateMode == mode };
            rb.IsCheckedChanged += (_, _) => { if (rb.IsChecked == true) Vm.UpdateMode = mode; };
            UpdatePanel.Children.Add(rb);
        }
    }
}
