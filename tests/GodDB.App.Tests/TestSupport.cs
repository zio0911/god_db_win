using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using GodDB.App.ViewModels;
using GodDB.Core;
using GodDB.Core.Audit;
using GodDB.Core.Platform;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(GodDB.App.Tests.TestAppBuilder))]

namespace GodDB.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<GodDB.App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// 테스트 본문을 headless Avalonia UI 스레드에서 실행한다.
/// (Avalonia.Headless.XUnit 12.1.6 의 [AvaloniaFact] 는 xunit.v3 3.x 용이라 이 저장소의 xunit.v3 4.x 와 맞지 않아 직접 둔다)
public static class UiThread
{
    private static HeadlessUnitTestSession Session => HeadlessUnitTestSession.GetOrStartForAssembly(typeof(UiThread).Assembly);

    public static Task Run(Action body) => Session.Dispatch(body, TestContext.Current.CancellationToken);

    public static Task Run(Func<Task> body) =>
        Session.Dispatch(async () => { await body(); return 0; }, TestContext.Current.CancellationToken);
}

internal static class TestSetup
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "goddb-app-tests-" + Guid.NewGuid().ToString("N")[..8]);

    /// 실제 사용자 폴더·설정을 건드리지 않도록 가장 먼저 데이터 폴더를 임시 폴더로 바꾼다
    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("GODDB_DATA_DIR", Root);
        Prefs.UseFile(Path.Combine(Root, "settings.json"));
    }
}

/// 테스트용 비밀번호 보관소 (기록을 본다)
public sealed class FakeSecretStore : ISecretStore
{
    public ConcurrentDictionary<Guid, string> Values { get; } = new();
    public bool FailWrites { get; set; }
    public string DisplayName => "fake";
    public string? GetPassword(Guid profileId) => Values.TryGetValue(profileId, out var v) ? v : null;
    public bool SetPassword(Guid profileId, string password)
    {
        if (FailWrites) return false;
        Values[profileId] = password;
        return true;
    }
    public void DeletePassword(Guid profileId) => Values.TryRemove(profileId, out _);
}

public sealed class FakeVerifier(bool result) : IUserVerifier
{
    public int Calls { get; private set; }
    public Task<bool> VerifyAsync(string reason, IntPtr ownerWindow = default)
    {
        Calls++;
        return Task.FromResult(result);
    }
}

/// 테스트 하나의 저장소 묶음 (접속 목록 · 탭 · 히스토리를 임시 폴더에)
public sealed class TestEnv
{
    public string Dir { get; } = Path.Combine(TestSetup.Root, Guid.NewGuid().ToString("N")[..10]);
    public FakeSecretStore Secrets { get; } = new();
    public ProfileStore Store { get; }
    public TabStore Tabs { get; }
    public QueryHistory History { get; }

    public TestEnv()
    {
        Directory.CreateDirectory(Dir);
        Store = new ProfileStore(Path.Combine(Dir, "profiles.json"));
        Tabs = new TabStore(Path.Combine(Dir, "tabs.json"));
        History = new QueryHistory(Path.Combine(Dir, "history.json"));
    }

    public AppState NewApp() => new(Store, Secrets, new FakeVerifier(true), Tabs, History, logFile: null,
                                    modelStore: new GodDB.Core.Modeling.ModelStore(Path.Combine(Dir, "models")));

    public static ConnectionProfile Profile(string name, DatabaseKind kind = DatabaseKind.Mysql) => new()
    {
        Name = name, Kind = kind, Host = "127.0.0.1", Port = 1, Username = "root", Database = "shop", UseTLS = false,
    };

    /// 실제로 연결하지 않고 연결된 것처럼 (탭 만들기 등 화면 상태 테스트)
    public static void FakeConnect(AppState app, Guid id, string db = "shop") =>
        app.UpdateConnection(id, c => { c.State = ConnectionState.Connected; c.CurrentDatabase = db; c.Databases = [db]; });
}
