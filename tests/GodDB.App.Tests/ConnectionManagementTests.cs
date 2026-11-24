using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GodDB.App.Controls;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core;
using GodDB.Core.Platform;
using GodDB.Core.Profiles;
using static GodDB.Core.L10n;

namespace GodDB.App.Tests;

/// 정렬 설정(Prefs)은 모든 사이드바가 같이 보므로, 바꾸는 테스트는 다른 테스트와 동시에 돌리지 않는다
[CollectionDefinition("connection list prefs", DisableParallelization = true)]
public sealed class ConnectionListPrefsCollection;

/// Mac 1.4.0~1.4.3 접속 관리: Redshift (#166), DBeaver 가져오기 (#168 · #195), 접속 목록 찾기 · 정렬 · 순서 바꾸기 (#173)
[Collection("connection list prefs")]
public class ConnectionManagementTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    /// 정렬 설정은 Prefs(공용)에 있으므로 테스트마다 처음 값으로
    private static void ResetSortPrefs()
    {
        Prefs.Remove(ConnectionListKeys.Sort);
        Prefs.Remove(ConnectionListKeys.ConnectedFirst);
    }

    // MARK: - Redshift

    [Fact]
    public Task ConnectionEditor_RedshiftType_SetsPortTlsAndFlag() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var vm = new ConnectionEditorViewModel(app, new ConnectionProfile { UseTLS = false }, isNew: true);
        Assert.Equal(ConnectionTypes.All, vm.Types);
        vm.Type = ConnectionType.Postgres;
        Assert.Equal(5432, vm.Port);
        vm.Type = ConnectionType.Redshift;
        Assert.Equal(DatabaseKind.Postgres, vm.Kind);
        Assert.Equal(5439, vm.Port);
        Assert.True(vm.UseTls);
        Assert.Equal("Redshift", vm.KindTitle);
        vm.Host = "c.example.test";
        Assert.True(vm.Draft.Redshift);
        Assert.Equal(ConnectionType.Redshift, vm.Draft.Type);
        // 다른 종류로 바꾸면 Redshift 표시가 꺼지고 포트도 돌아간다
        vm.Type = ConnectionType.Mysql;
        Assert.Equal(3306, vm.Port);
        Assert.False(vm.Draft.Redshift);
        // 저장된 Redshift 접속을 열면 Redshift 로 보인다
        var saved = new ConnectionEditorViewModel(app, new ConnectionProfile { Kind = DatabaseKind.Postgres, Redshift = true, Port = 5439 }, false);
        Assert.Equal(ConnectionType.Redshift, saved.Type);

        var w = new ConnectionEditorWindow(app, new ConnectionProfile());
        w.Show();
        Pump();
        var combo = w.GetVisualDescendants().OfType<ComboBox>().First(c => ReferenceEquals(c.ItemsSource, ((ConnectionEditorViewModel)w.DataContext!).Types));
        Assert.Equal(5, combo.ItemCount);
        w.Close();
    });

    [Fact]
    public Task KindBadge_Redshift_DrawsPurpleCylinder() => UiThread.Run(() =>
    {
        var badge = new KindBadge { Kind = DatabaseKind.Postgres, Redshift = true };
        Assert.Equal("RedshiftIcon", Assert.IsType<Canvas>(badge.Child).Name);
        Assert.Equal("Redshift", ToolTip.GetTip(badge));
        // MySQL 에 Redshift 를 켜도 MySQL 로고 그대로 (Mac: redshift && kind == .postgres)
        var my = new KindBadge { Kind = DatabaseKind.Mysql, Redshift = true };
        Assert.Equal("MySQL", ToolTip.GetTip(my));
        badge.Redshift = false;
        Assert.Equal("PostgresIcon", Assert.IsType<Canvas>(badge.Child).Name);
    });

    [Fact]
    public Task SidebarRow_RedshiftProfile_ShowsRedshiftIcon() => UiThread.Run(() =>
    {
        ResetSortPrefs();
        var env = new TestEnv();
        var rs = TestEnv.Profile("분석", DatabaseKind.Postgres) with { Redshift = true };
        env.Store.Save([rs]);
        var app = env.NewApp();
        var view = new SidebarView { DataContext = new SidebarViewModel(app) };
        var w = new Window { Content = view, Width = 300, Height = 600 };
        w.Show();
        Pump();
        var badge = view.GetVisualDescendants().OfType<KindBadge>().Single();
        Assert.True(badge.Redshift);
        Assert.Equal("RedshiftIcon", (badge.Child as Control)?.Name);
        w.Close();
    });

    /// 연결 성공: 마지막 연결 시각 기록, PostgreSQL 접속이 Redshift 로 확인되면 종류 전환 (그 반대도)
    [Fact]
    public Task NoteConnected_SavesTimeAndRedshiftFlag() => UiThread.Run(() =>
    {
        var env = new TestEnv();
        var p = TestEnv.Profile("pg", DatabaseKind.Postgres);
        env.Store.Save([p]);
        var app = env.NewApp();
        var before = DateTimeOffset.Now.AddSeconds(-1);
        app.NoteConnected(p.Id, redshift: true);
        var now = app.Profiles.Single();
        Assert.True(now.Redshift);
        Assert.True(now.LastConnectedAt >= before);
        Assert.True(env.Store.Load().Single().Redshift);
        Assert.NotNull(env.Store.Load().Single().LastConnectedAt);
        app.NoteConnected(p.Id, redshift: false);
        Assert.False(app.Profiles.Single().Redshift);
        // Redis 등은 Redshift 표시를 건드리지 않는다
        app.NoteConnected(p.Id);
        Assert.False(app.Profiles.Single().Redshift);
    });

    // MARK: - 접속 목록 찾기 · 정렬 · 순서

    [Fact]
    public Task Sidebar_SearchFiltersAndShowsCount() => UiThread.Run(() =>
    {
        ResetSortPrefs();
        var env = new TestEnv();
        var a = TestEnv.Profile("주문 DB") with { Group = "운영", Host = "orders.example.test" };
        var b = TestEnv.Profile("회원 DB") with { Group = "운영" };
        var c = TestEnv.Profile("로컬");
        env.Store.Save([a, b, c]);
        var app = env.NewApp();
        var vm = new SidebarViewModel(app);
        Assert.True(vm.ShowSortMenu);
        Assert.Null(vm.ConnectionSearchText);
        vm.ConnectionQuery = "orders";
        var group = Assert.IsType<GroupRowViewModel>(Assert.Single(vm.ConnectionItems));
        Assert.Equal("주문 DB", Assert.Single(group.Members).Name);
        Assert.True(group.IsExpanded);
        Assert.Equal(Tr("%@개 중 %@개", 3, 1), vm.ConnectionSearchText);
        vm.ConnectionQuery = "없는접속";
        Assert.Empty(vm.ConnectionItems);
        Assert.Equal(Tr("찾는 접속이 없어요"), vm.ConnectionSearchText);
        vm.ClearConnectionQueryCommand.Execute(null);
        Assert.Equal(2, vm.ConnectionItems.Count);   // 그룹 없는 접속 1 + 그룹 1

        // 찾는 중에 펼친 그룹은 접어 둔 상태를 바꾸지 않는다
        vm.SetExpanded("운영", false);
        vm.RebuildConnections();
        Assert.False(vm.ConnectionItems.OfType<GroupRowViewModel>().Single().IsExpanded);
        vm.ConnectionQuery = "회원";
        Assert.True(vm.ConnectionItems.OfType<GroupRowViewModel>().Single().IsExpanded);
        vm.ConnectionQuery = "";
        Assert.False(vm.ConnectionItems.OfType<GroupRowViewModel>().Single().IsExpanded);
    });

    [Fact]
    public Task Sidebar_SortMenuAndConnectedFirst() => UiThread.Run(() =>
    {
        ResetSortPrefs();
        var env = new TestEnv();
        var b = TestEnv.Profile("b");
        var a = TestEnv.Profile("a");
        var c = TestEnv.Profile("c");
        env.Store.Save([b, a, c]);
        var app = env.NewApp();
        var vm = new SidebarViewModel(app);
        string[] Names() => [.. vm.ConnectionItems.OfType<ProfileRowViewModel>().Select(r => r.Name)];
        Assert.Equal(["b", "a", "c"], Names());
        Assert.Equal(Tr("직접 정한 순"), vm.SortTitle);

        var menu = SidebarView.BuildSortMenu(app);
        var items = menu.ItemsSource!.Cast<Control>().ToList();
        var radios = items.OfType<MenuItem>().Where(m => m.ToggleType == MenuItemToggleType.Radio).ToList();
        Assert.Equal(ConnectionSorts.All.Select(s => s.Title()), radios.Select(m => (string)m.Header!));
        Assert.True(radios[0].IsChecked);
        var connectedFirst = items.OfType<MenuItem>().Single(m => m.ToggleType == MenuItemToggleType.CheckBox);
        Assert.Equal(Tr("연결된 접속 먼저"), connectedFirst.Header);

        app.ConnectionSort = ConnectionSort.Name;
        Assert.Equal(["a", "b", "c"], Names());
        Assert.Equal(Tr("이름순"), vm.SortTitle);
        Assert.Equal("name", Prefs.GetString(ConnectionListKeys.Sort));   // Mac 과 같은 값
        TestEnv.FakeConnect(app, c.Id);
        app.ConnectedFirst = true;
        Assert.Equal(["c", "a", "b"], Names());
        ResetSortPrefs();
    });

    /// 다른 정렬로 보는 중에 순서를 바꾸면, 보이는 순서를 저장 순서로 굳히고 "직접 정한 순" 으로
    [Fact]
    public Task Reorder_FreezesVisibleOrderAndSwitchesToManual() => UiThread.Run(() =>
    {
        ResetSortPrefs();
        var env = new TestEnv();
        var b = TestEnv.Profile("b");
        var a = TestEnv.Profile("a");
        var c = TestEnv.Profile("c");
        var g1 = TestEnv.Profile("g1") with { Group = "G" };
        env.Store.Save([b, a, c, g1]);
        var app = env.NewApp();
        app.ConnectionSort = ConnectionSort.Name;
        app.ReorderProfile(c.Id, ConnectionListOrder.Move.Top);
        Assert.Equal(ConnectionSort.Manual, app.ConnectionSort);
        Assert.Equal(["c", "a", "b", "g1"], app.Profiles.Select(p => p.Name));
        Assert.Equal(["c", "a", "b", "g1"], env.Store.Load().Select(p => p.Name));

        // 끌어다 놓기: 놓은 접속 바로 앞으로, 그 접속의 그룹으로
        app.ReorderProfile(a.Id, g1.Id);
        Assert.Equal(["c", "b", "a", "g1"], app.Profiles.Select(p => p.Name));
        Assert.Equal("G", app.Profiles.Single(p => p.Id == a.Id).Group);

        // 그룹 순서
        var h = TestEnv.Profile("h1") with { Group = "H" };
        app.Profiles.Add(h);
        app.ReorderGroup("H", ConnectionListOrder.Move.Top);
        Assert.Equal(["H", "G"], ConnectionListOrder.SortedGroups(app.Profiles, ConnectionSort.Manual));
        app.ReorderGroup("G", "H");
        Assert.Equal(["G", "H"], ConnectionListOrder.SortedGroups(app.Profiles, ConnectionSort.Manual));
        ResetSortPrefs();
    });

    [Fact]
    public Task ContextMenus_HaveReorderItems() => UiThread.Run(() =>
    {
        var menu = SidebarView.ReorderMenu(_ => { });
        Assert.Equal(Tr("순서 바꾸기"), menu.Header);
        Assert.Equal([Tr("맨 위로"), Tr("위로"), Tr("아래로"), Tr("맨 아래로")], menu.Items.Cast<MenuItem>().Select(m => (string)m.Header!));
        var moves = new List<ConnectionListOrder.Move>();
        var m2 = SidebarView.ReorderMenu(moves.Add);
        foreach (var item in m2.Items.Cast<MenuItem>()) item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal([ConnectionListOrder.Move.Top, ConnectionListOrder.Move.Up, ConnectionListOrder.Move.Down, ConnectionListOrder.Move.Bottom], moves);
    });

    [Fact]
    public Task Sidebar_ImportButtonHasTextAndDBeaverItem() => UiThread.Run(() =>
    {
        ResetSortPrefs();
        var env = new TestEnv();
        env.Store.Save([TestEnv.Profile("a"), TestEnv.Profile("b")]);
        var app = env.NewApp();
        var view = new SidebarView { DataContext = new SidebarViewModel(app) };
        var w = new Window { Content = view, Width = 300, Height = 600 };
        w.Show();
        Pump();
        var importButton = view.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Flyout is MenuFlyout && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Inlines?.Any(i => i is Avalonia.Controls.Documents.Run { Text: var s } && s == Tr("가져오기 · 내보내기")) == true));
        var flyout = (MenuFlyout)importButton.Flyout!;
        Assert.Contains(flyout.Items.OfType<MenuItem>(), m => (string?)m.Header == Tr("DBeaver 접속 정보 가져오기…"));
        Assert.True(view.FindControl<Button>("SortButton")!.IsVisible);
        Assert.NotNull(view.FindControl<TextBox>("ConnectionSearch"));
        w.Close();
    });

    // MARK: - DBeaver

    /// DBeaver 가 credentials-config.json 을 잠그는 고정 키 (DBeaver 소스에 공개된 값)
    private static readonly byte[] DBeaverKey = [0xBA, 0xBB, 0x4A, 0x9F, 0x77, 0x4A, 0xB8, 0x53, 0xC9, 0x6C, 0x2D, 0x65, 0x3D, 0xFE, 0x54, 0x4A];

    private static string WriteDBeaverWorkspace(string dir)
    {
        Directory.CreateDirectory(dir);
        const string ds = """
            {"folders":{"운영":{}},"connections":{
              "pg-1":{"provider":"postgresql","driver":"postgres-jdbc","name":"pg","folder":"운영","configuration":{"host":"h.example.test","port":"5432","database":"d"}},
              "my-1":{"provider":"mysql","driver":"mysql8","name":"my","configuration":{"host":"m.example.test","port":"3306"}},
              "s-1":{"provider":"sqlite","name":"로컬","configuration":{"database":"a.db"}}}}
            """;
        const string creds = """{"pg-1":{"#connection":{"user":"app","password":"s3cret"}},"my-1":{"#connection":{"user":"root"}}}""";
        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = DBeaverKey;
        File.WriteAllText(Path.Combine(dir, "data-sources.json"), ds);
        File.WriteAllBytes(Path.Combine(dir, "credentials-config.json"), [.. iv, .. aes.EncryptCbc(Encoding.UTF8.GetBytes(creds), iv, PaddingMode.PKCS7)]);
        return Path.Combine(dir, "data-sources.json");
    }

    /// DBeaver 파일: 건너뛴 접속 안내, 저장된 비밀번호는 가져오기를 누르면 자격 증명 관리자로 (#195)
    [Fact]
    public Task DBeaverImport_SkippedNoticeAndPasswordsToSecretStore() => UiThread.Run(async () =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        var path = WriteDBeaverWorkspace(Path.Combine(env.Dir, "dbeaver"));
        await app.ImportFileAsync(path);
        var req = Assert.IsType<ImportRequest>(app.ImportRequest);
        Assert.Equal("DBeaver", req.Bundle.App);
        Assert.Equal(["로컬"], req.Skipped);
        var pg = req.Bundle.Profiles.Single(p => p.Name == "pg");
        Assert.Equal("s3cret", req.PlainPasswords[pg.Id]);
        Assert.Equal("운영", pg.Group);
        Assert.Contains(Tr("DBeaver 에 저장된 비밀번호도 가져와 자격 증명 관리자에 넣어요."), req.Notes[pg.Id]);

        var w = new ImportConnectionsWindow(app, req);
        w.Show();
        Pump();
        var texts = w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.Contains(texts, t => t.Contains(Tr("지원하지 않는 접속 %@개는 건너뛰었어요: %@", 1, "로컬")));
        Assert.Contains(texts, t => t == "운영 ›");
        Assert.Contains(texts, t => t.StartsWith("PostgreSQL · h.example.test:5432", StringComparison.Ordinal));
        w.RunImport();
        Assert.Null(app.ImportRequest);
        Assert.Equal(2, app.Profiles.Count);
        Assert.Equal("s3cret", env.Secrets.GetPassword(pg.Id));
        Assert.Null(env.Secrets.GetPassword(app.Profiles.Single(p => p.Name == "my").Id));
        // 화면 · 로그에는 비밀번호를 남기지 않는다
        Assert.DoesNotContain(app.Logs, l => (l.Message + l.Detail).Contains("s3cret"));
    });

    [Fact]
    public Task DBeaverImport_DbpExtensionAndNotDBeaverError() => UiThread.Run(async () =>
    {
        var env = new TestEnv();
        var app = env.NewApp();
        // GodDB 접속 정보 파일이 아니고 DBeaver 도 아닌 JSON → GodDB 파일 오류 (기존과 같음)
        var bad = Path.Combine(env.Dir, "x.json");
        File.WriteAllText(bad, "{\"a\":1}");
        await app.ImportFileAsync(bad);
        Assert.Null(app.ImportRequest);
        // .dbp 확장자는 내용과 상관없이 DBeaver 로 읽는다 → DBeaver 파일 아님 오류
        var dbp = Path.Combine(env.Dir, "x.dbp");
        File.WriteAllText(dbp, "{\"a\":1}");
        await app.ImportFileAsync(dbp);
        Assert.Null(app.ImportRequest);
        Assert.Contains(app.Logs, l => (l.Detail ?? l.Message).Contains("DBeaver") || l.Message.Contains(Tr("접속 정보 가져오기 실패")));
    });
}
