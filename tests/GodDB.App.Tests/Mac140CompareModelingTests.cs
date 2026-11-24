using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using GodDB.App.Services;
using GodDB.App.ViewModels;
using GodDB.App.Views;
using GodDB.Core;
using GodDB.Core.Audit;
using GodDB.Core.Modeling;
using GodDB.Core.Platform;
using GodDB.Core.Schema;

namespace GodDB.App.Tests;

/// Mac 1.4.0 ~ 1.4.3 의 스키마 비교 개선 · 모델링 내보내기 · 자동 배치 · 도구 말풍선 · 모델 파일 열기 · 테이블 삭제 (headless)
/// (Mac 화면 테스트 schemaCompare · modeling 시나리오와 같은 흐름)
public class Mac140CompareModelingTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private sealed class Recording : NullDialogs
    {
        public string? SavePath { get; set; }
        public List<string> OpenPaths { get; set; } = [];
        public List<(string Title, string Name, IReadOnlyList<FileFilter> Filters)> Saves { get; } = [];
        public List<IReadOnlyList<FileFilter>> Opens { get; } = [];

        public override Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileFilter> filters)
        {
            Saves.Add((title, suggestedName, filters));
            return Task.FromResult(SavePath);
        }

        public override Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<FileFilter> filters, bool multiple = false, string? startFolder = null)
        {
            Opens.Add(filters);
            return Task.FromResult<IReadOnlyList<string>>(OpenPaths);
        }
    }

    // MARK: - 스키마 비교: 여러 개 골라 복사

    private static SchemaDiffReport Report() => new(
    [
        new(SchemaDiffKind.DifferentTable, "tb_member", null, "테이블 코멘트 변경", "ALTER TABLE `tb_member` COMMENT = '회원';"),
        new(SchemaDiffKind.MissingColumn, "tb_member", "email", "컬럼 없음", "ALTER TABLE `tb_member` ADD COLUMN `email` varchar(100) NULL;"),
        new(SchemaDiffKind.ExtraColumn, "tb_member", "extra_b", "대상에만 있음", null),
        new(SchemaDiffKind.MissingIndex, "tb_order", "idx_member", "인덱스 없음", "ALTER TABLE `tb_order` ADD INDEX `idx_member` (`member_id`);"),
    ], 0);

    private static void Key(Control target, Key key, KeyModifiers mods) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods, Source = target });

    [Fact]
    public Task Compare_MultiSelect_CopiesInListOrder_PerTableHeader() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        var view = new CompareView { DataContext = app };
        var w = new Window { Width = 1100, Height = 700, Content = view };
        w.Show();
        var vm = view.Vm!;
        vm.Report = Report();
        Pump();
        var grid = view.FindControl<DataGrid>("Items")!;
        Assert.Equal("보정 SQL", vm.SqlHeader);
        Assert.Equal("왼쪽에서 항목을 선택하세요.", vm.SelectedSql);
        Assert.False(vm.CopySelectedCommand.CanExecute(null));

        // Ctrl · Shift 클릭처럼 여러 개: 고른 순서와 관계없이 목록 순서, 테이블마다 "-- 테이블"
        var visible = vm.Visible;
        grid.SelectedItems.Add(visible[2]);   // tb_order 인덱스
        grid.SelectedItems.Add(visible[0]);   // tb_member 코멘트
        Pump();
        Assert.Equal(2, vm.SelectionCount);
        Assert.Equal("보정 SQL · 2개 선택", vm.SqlHeader);
        Assert.Equal("선택한 2개 SQL 복사", vm.CopySelectedLabel);
        Assert.Equal("복사 (2개, Ctrl+C)", vm.ContextCopyLabel);
        var expected = "-- tb_member\nALTER TABLE `tb_member` COMMENT = '회원';\n\n-- tb_order\nALTER TABLE `tb_order` ADD INDEX `idx_member` (`member_id`);";
        Assert.Equal(expected, vm.SelectedSql);

        // Ctrl+C: 목록에서 고른 항목들의 보정 SQL
        Key(grid, Avalonia.Input.Key.C, KeyModifiers.Control);
        await Task.Delay(10);
        Assert.Equal(expected, ((NullDialogs)app.Dialogs).Clipboard);

        // Ctrl+A: 보이는 항목 모두 (표의 고른 것도 같이)
        Key(grid, Avalonia.Input.Key.A, KeyModifiers.Control);
        Pump();
        Assert.Equal(vm.Visible.Count, vm.SelectionCount);
        Assert.Equal(vm.Visible.Count, grid.SelectedItems.Count);
        Assert.Equal(vm.Report!.FixScript, SchemaDiffReport.FixSql(vm.Visible, vm.SelectedIds));

        // 우클릭 메뉴: 복사 (n개) · 모두 선택
        var menu = grid.ContextMenu!;
        menu.Open(grid);
        Pump();
        var copy = menu.Items.OfType<MenuItem>().First();
        Assert.Equal($"복사 ({vm.Visible.Count}개, Ctrl+C)", copy.Header);
        Assert.Contains(menu.Items.OfType<MenuItem>(), m => m.Header as string == "모두 선택");
        menu.Close();

        // 참고용 항목만 고르면 SQL 없음, 복사 버튼은 꺼짐
        vm.ShowExtra = true;
        Pump();
        vm.Selection = vm.Visible.First(i => i.Kind == SchemaDiffKind.ExtraColumn);
        Pump();
        Assert.Equal("-- 참고용 항목이라 SQL을 만들지 않아요.", vm.SelectedSql);
        Assert.Equal("이 SQL 복사", vm.CopySelectedLabel);
        Assert.False(vm.CopySelectedCommand.CanExecute(null));
        Assert.Single(grid.SelectedItems);
        // 대상에만 있는 항목을 숨기면 고른 것에서도 빠진다
        vm.ShowExtra = false;
        Pump();
        Assert.Equal(0, vm.SelectionCount);
        w.Close();
    });

    /// 응답하지 않는 서버: TCP 연결만 받고 아무 말도 하지 않는다 (MySQL 인사말을 끝없이 기다림)
    private sealed class SilentServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _clients = [];
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public SilentServer()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try { while (true) { var c = await _listener.AcceptTcpClientAsync(); lock (_clients) _clients.Add(c); } }
                catch { }
            });
        }

        public void Dispose()
        {
            _listener.Stop();
            lock (_clients) foreach (var c in _clients) c.Dispose();
        }
    }

    private static int ClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static ConnectionProfile Local(string name, int port) => new()
    {
        Name = name, Kind = DatabaseKind.Mysql, Host = "127.0.0.1", Port = port, Username = "root", Database = "shop",
        UseTLS = false, SavePassword = false,
    };

    [Fact]
    public Task Compare_ConnectFailure_SaysWhichSide_AndRetries() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        var s = Local("src_closed", ClosedPort());
        var t = Local("dst_closed", ClosedPort());
        app.Profiles.Add(s);
        app.Profiles.Add(t);
        app.SessionPasswords[s.Id] = "x";
        app.SessionPasswords[t.Id] = "x";
        var view = new CompareView { DataContext = app };
        var w = new Window { Width = 1000, Height = 600, Content = view };
        w.Show();
        var vm = view.Vm!;
        vm.Select(s.Id, t.Id);
        await vm.CompareAsync();
        Assert.True(vm.ShowError);
        Assert.Contains("기준 접속 src_closed 연결 실패", vm.Error);
        Assert.Contains("대상 접속 dst_closed 연결 실패", vm.Error);
        Pump();
        var retry = view.FindControl<Button>("RetryButton")!;
        Assert.True(retry.IsEffectivelyVisible);
        Assert.True(vm.RetryCommand.CanExecute(null));
        var errors = app.Logs.Count(l => l.Level == LogLevel.Error);
        await vm.RetryCommand.ExecuteAsync(null);
        Assert.True(vm.ShowError);
        Assert.True(app.Logs.Count(l => l.Level == LogLevel.Error) > errors);   // 다시 시도했다
        // 접속을 고르지 않으면 다시 시도할 수 없다
        vm.Select(null, null);
        vm.Source = vm.Choices[0];
        Assert.False(vm.RetryCommand.CanExecute(null));
        w.Close();
    });

    [Fact]
    public Task Compare_Cancel_WhileConnecting() => UiThread.Run(async () =>
    {
        using var server = new SilentServer();
        var app = new TestEnv().NewApp();
        var s = Local("silent_a", server.Port);
        var t = Local("silent_b", server.Port);
        app.Profiles.Add(s);
        app.Profiles.Add(t);
        app.SessionPasswords[s.Id] = "x";
        app.SessionPasswords[t.Id] = "x";
        var vm = new CompareViewModel(app);
        vm.Select(s.Id, t.Id);
        var started = DateTime.UtcNow;
        var run = vm.CompareAsync();
        await Task.Delay(300);
        Assert.True(vm.Running);
        vm.CancelCommand.Execute(null);
        await run;
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "연결 시간 제한(15초)보다 먼저 끝나야 한다");
        Assert.False(vm.Running);
        Assert.Null(vm.Error);
        Assert.Null(vm.Report);
        Assert.True(vm.ShowEmpty);
        Assert.Contains(app.Logs, l => l.Message == "스키마 비교를 취소했어요.");
    });

    // MARK: - 모델링

    private static (ModelingView View, Window Host) Open(AppState app, DataModel model)
    {
        app.Modeling.UseForPreview([model]);
        var view = new ModelingView { DataContext = app };
        var host = new Window { Content = view, Width = 1300, Height = 800 };
        host.Show();
        Pump();
        return (view, host);
    }

    [Fact]
    public Task Modeling_Toolbar_TooltipsAndExportMenu() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var (view, host) = Open(app, ModelingSample.Model());
        var tips = view.GetLogicalDescendants().OfType<Control>().Select(c => ToolTip.GetTip(c) as string).OfType<string>().ToList();
        foreach (var tip in new[]
        {
            "모델 이름", "대상 DB — SQL 추출 문법과 자주 쓰는 타입 목록에 써요",
            "선택 / 이동 — 테이블·관계를 눌러 고르고, 카드를 끌어 옮겨요",
            "테이블 추가 — 이 도구를 고른 뒤 다이어그램 빈 곳을 누르면 새 테이블이 생겨요",
            "1:N 비식별 관계 — 부모 테이블, 자식 테이블 순서로 누르면 FK 컬럼이 자식에 생겨요",
            "1:N 식별 관계 — 부모 키가 자식의 기본 키에 포함돼요 (부모 → 자식 순서로 누르기)",
            "1:1 관계 — 부모 → 자식 순서로 누르면 유니크 FK 컬럼이 자식에 생겨요",
            "되돌리기 — 마지막 구조 변경(테이블·관계 추가/삭제, 이동, 자동 정렬)을 취소해요",
            "자동 정렬 — 테이블을 관계에 따라 겹치지 않게 다시 배치",
            "모델 검사 — 물리명·타입·기본 키 문제를 보여 줘요. 오류가 있으면 SQL 추출을 막아요",
            "내보내기 — 모델링 파일(.goddbmodel), 테이블 명세서(엑셀), 다이어그램 PNG 로 저장해요",
            "SQL 추출 — 모델을 CREATE TABLE · FK SQL 로 뽑아 복사하거나 저장해요",
            "축소", "확대",
        })
            Assert.Contains(tip, tips);
        Assert.DoesNotContain("PNG 저장", tips);   // PNG 버튼은 내보내기 메뉴로 들어갔다

        var menu = Assert.IsType<MenuFlyout>(view.ExportButton!.Flyout);
        Assert.Equal(["모델링 파일 저장하기…", "테이블 명세서로 저장하기…", null, "다이어그램 PNG 저장하기…"],
                     menu.Items.Select(i => (i as MenuItem)?.Header as string));

        // 자동 정렬은 테이블이 없으면 꺼진다
        var vm = view.Vm!;
        var auto = view.GetLogicalDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "자동 정렬 — 테이블을 관계에 따라 겹치지 않게 다시 배치");
        Assert.True(auto.IsEnabled);
        foreach (var t in vm.Model!.Tables.ToList()) vm.Delete(new ModelSelection.Table(t.Id));
        Pump();
        Assert.False(auto.IsEnabled);
        host.Close();
    });

    /// Mac 23a13c0: 모델링에서 테이블을 삭제하면 아래 편집기가 지운 테이블을 읽다가 앱이 꺼지던 문제
    [Fact]
    public Task Modeling_DeleteTable_WhileEditorOpen_DoesNotCrash() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var m = ModelingSample.Model();
        var (view, host) = Open(app, m);
        var vm = view.Vm!;
        var count = vm.Model!.Tables.Count;
        foreach (var tab in Enumerable.Range(0, 4))
        {
            var table = vm.Model!.Tables[0];
            vm.TapTable(table.Id);
            vm.EditorTab = tab;
            vm.Changed(structure: true);
            Pump();
            var editor = view.GetLogicalDescendants().OfType<ModelTableEditor>().Single();
            // 편집기 입력칸에 포커스가 있는 채로 지운다 (포커스를 잃으며 지운 테이블에 값을 쓰려 해도 괜찮아야 함)
            editor.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault()?.Focus();
            Pump();
            vm.Delete(vm.Selection!);
            Pump();
            Assert.Null(vm.Selection);
            Assert.Empty(view.GetLogicalDescendants().OfType<ModelTableEditor>());
            Assert.Null(vm.Model!.Table(table.Id));
            // 되돌리면 다시 살아나고, 다시 골라 편집할 수 있다
            vm.Undo();
            Pump();
            Assert.NotNull(vm.Model!.Table(table.Id));
            Assert.Equal(count, vm.Model.Tables.Count);
        }
        // 관계 편집기에서 관계 삭제
        var rel = vm.Model!.Relations[0];
        vm.SelectRelation(rel.Id);
        Pump();
        vm.DeleteRelation(rel.Id);
        Pump();
        Assert.Empty(view.GetLogicalDescendants().OfType<ModelRelationEditor>());
        host.Close();
    });

    [Fact]
    public Task Modeling_AutoLayout_NoOverlap() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var m = ModelingSample.Model();
        foreach (var t in m.Tables) { t.X = 40; t.Y = 40; }
        var (view, host) = Open(app, m);
        var vm = view.Vm!;
        vm.AutoLayout();
        Pump();
        var frames = vm.Model!.Tables.Select(ModelDiagram.Frame).ToList();
        for (var i = 0; i < frames.Count; i++)
            for (var j = i + 1; j < frames.Count; j++)
                Assert.False(frames[i].Intersects(frames[j]), $"{frames[i]} {frames[j]}");
        foreach (var r in vm.Model.Relations.Where(r => r.Parent != r.Child))
            Assert.True(vm.Model.Table(r.Parent)!.X < vm.Model.Table(r.Child)!.X);
        Assert.True(vm.CanUndo);
        host.Close();
    });

    [Fact]
    public Task Modeling_ExportTableSpec_AsksInfo_ThenSavesXlsx() => UiThread.Run(async () =>
    {
        var app = new TestEnv().NewApp();
        var dialogs = new Recording();
        app.Dialogs = dialogs;
        var m = ModelingSample.Model();
        var (view, host) = Open(app, m);
        var vm = view.Vm!;
        Prefs.Remove(TableSpecExportWindow.AuthorKey);

        vm.ExportTableSpec(m.Id);
        Pump();
        var sheet = Assert.IsType<TableSpecExportWindow>(view.LastDialog);
        Assert.Null(vm.TableSpecModel);
        Assert.Equal(m.Name, sheet.SystemNameBox.Text);
        Assert.Equal(Environment.UserName, sheet.AuthorBox.Text);
        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), sheet.DateBox.Text);

        var dir = Path.Combine(Path.GetTempPath(), "goddb-spec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            dialogs.SavePath = Path.Combine(dir, "spec.xlsx");
            sheet.SystemNameBox.Text = "  쇼핑몰 시스템 ";
            sheet.AuthorBox.Text = "홍길동";
            sheet.Save();
            for (var i = 0; i < 50 && sheet.Saving is { IsCompleted: false }; i++) { Pump(); await Task.Delay(20); }
            Assert.True(sheet.Saving!.IsCompleted);
            Assert.Equal($"{m.Name}_테이블명세서.xlsx", dialogs.Saves.Single().Name);
            Assert.Equal(["*.xlsx"], dialogs.Saves.Single().Filters.Single().Patterns);
            using (var zip = ZipFile.OpenRead(dialogs.SavePath))
            {
                using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
                var toc = reader.ReadToEnd();
                Assert.Contains("테이블 명세서 — 쇼핑몰 시스템", toc);   // 앞뒤 공백은 뺀다
                Assert.Contains("홍길동", toc);
                Assert.Equal(m.Tables.Count + 1, zip.Entries.Count(e => e.FullName.StartsWith("xl/worksheets/sheet")));
            }
            // 작성자는 다음에도 기억
            Assert.Equal("홍길동", TableSpecExportWindow.DefaultAuthor());

            // 목록 우클릭 > 내보내기 > PNG (지금 모델이 아니어도 그 모델로)
            dialogs.SavePath = Path.Combine(dir, "d.png");
            await view.ExportPngAsync(m.Id);
            Assert.True(File.Exists(dialogs.SavePath));
            // headless 그리기(테스트 플랫폼)는 PNG 내용을 만들지 않는다. 내용이 있으면 PNG 서명인지 본다 (DiagramTests 와 같음)
            var png = await File.ReadAllBytesAsync(dialogs.SavePath);
            if (png.Length > 0) Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
            Assert.Equal($"{m.Name}.png", dialogs.Saves[^1].Name);

            // 모델링 파일 저장하기
            dialogs.SavePath = Path.Combine(dir, "m.goddbmodel");
            await vm.ExportAsync(m.Id);
            Assert.True(File.Exists(dialogs.SavePath));
            Assert.Equal("모델링 파일 저장하기…", dialogs.Saves[^1].Title);

            // 모델 파일 열기: 모델 파일과 JSON 을 한 형식에서 함께 고를 수 있다 (Mac allowedContentTypes = [모델, .json])
            var json = Path.Combine(dir, "copy.json");
            File.Copy(dialogs.SavePath, json);
            dialogs.OpenPaths = [dialogs.SavePath, json];
            await vm.ImportAsync();
            Assert.Equal(["*.goddbmodel", "*.json"], dialogs.Opens.Single()[0].Patterns);
            Assert.Equal(3, vm.Rows.Count);
            Assert.Empty(dialogs.Alerts);
        }
        finally
        {
            Prefs.Remove(TableSpecExportWindow.AuthorKey);
            try { Directory.Delete(dir, true); } catch { }
        }
        host.Close();
    });

    [Fact]
    public Task Modeling_ListContextMenu_HasExportSubmenu() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var m = ModelingSample.Model();
        var (view, host) = Open(app, m);
        var list = view.FindControl<ListBox>("ModelList")!;
        var item = list.ContainerFromIndex(0)!;
        var panel = item.GetLogicalDescendants().OfType<StackPanel>().First(p => p.ContextMenu != null);
        var menu = panel.ContextMenu!;
        menu.Open(panel);
        Pump();
        var export = menu.Items.OfType<MenuItem>().First(i => i.Header as string == "내보내기");
        Assert.Equal(["모델링 파일 저장하기…", "테이블 명세서로 저장하기…", null, "다이어그램 PNG 저장하기…"],
                     export.Items.Select(i => (i as MenuItem)?.Header as string));
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), i => i.Header as string == "파일로 내보내기…");
        // 테이블 명세서 → 그 모델로 창
        ((MenuItem)export.Items[1]!).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
        Assert.IsType<TableSpecExportWindow>(view.LastDialog);
        menu.Close();
        host.Close();
    });
}
