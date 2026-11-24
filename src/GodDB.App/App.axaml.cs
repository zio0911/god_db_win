using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GodDB.App.Infrastructure;
using GodDB.App.Services;
using GodDB.App.ViewModels;
using GodDB.App.Views;

namespace GodDB.App;

public partial class App : Application
{
    public override void Initialize()
    {
        MenuText.Install();
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppStartup.HookCrashLog();
            AppStartup.ApplyPreferences();
            var state = AppState.CreateDefault();
            var window = new MainWindow(state);
            desktop.MainWindow = window;
            window.Opened += (_, _) => window.StartUp(new UpdateService());
            desktop.ShutdownRequested += (_, _) =>
            {
                state.SaveTabsNow();
                LogFile.Shared.Flush();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
