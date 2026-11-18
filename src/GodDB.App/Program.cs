using Avalonia;
using Velopack;

namespace GodDB.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack: 설치·업데이트 훅을 가장 먼저 처리한다 (설치 프로그램이 앱을 불렀을 때 바로 끝남)
        VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
