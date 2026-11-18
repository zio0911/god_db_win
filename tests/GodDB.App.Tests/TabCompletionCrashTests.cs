using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using GodDB.Core.Sql;

namespace GodDB.App.Tests;

/// 1.3.7 사용자 보고 (Windows, SQL Server, `from t` + Tab): 자동완성 목록이 뜰 때
/// "처리하지 못한 오류 (TargetInvocationException ← NullReferenceException)".
/// 헤드리스에서는 목록 틀(ListBox)이 만들어지지 않아 첫 후보 고르기가 건너뛰어졌다 → 틀을 강제로 만들어 같은 길을 탄다.
public class TabCompletionCrashTests
{
    [Fact]
    public Task SelectingFirstCandidateDoesNotThrow() => UiThread.Run(() =>
    {
        var errors = new List<Exception>();
        void OnError(object? s, DispatcherUnhandledExceptionEventArgs e) { errors.Add(e.Exception); e.Handled = true; }
        Dispatcher.UIThread.UnhandledException += OnError;
        try
        {
            var (w, editor) = SqlEditorTests.HostFor("SELECT * FROM t");
            SqlEditorTests.PressTabFor(editor);
            Assert.IsType<TabAction.ShowList>(editor.LastTabAction);
            var win = (CompletionWindow?)typeof(GodDB.App.Controls.SqlEditor)
                .GetField("_completion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(editor);
            Assert.NotNull(win);
            win!.CompletionList.ApplyTemplate();
            // 편집기 테마(AvaloniaEdit.xaml)가 없으면 목록 틀이 없어 ListBox 가 null → 실제 앱에서 NullReference
            Assert.NotNull(win.CompletionList.ListBox);
            try { win.CompletionList.SelectedItem = win.CompletionList.CompletionData.FirstOrDefault(); }
            catch (Exception e) { errors.Add(e); }
            for (int i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
            w.Close();
        }
        finally { Dispatcher.UIThread.UnhandledException -= OnError; }
        if (errors.Count > 0) Assert.Fail(string.Join("\n----\n", errors.Select(e => e.ToString())));
    });
}

/// 사용자 보고: 언어를 English 로 바꿔도 Redis 화면 등 코드로 만든 문장이 한국어로 남음 → 언어가 바뀌면 화면을 새로 만든다
public class LanguageSwitchRebuildTests
{
    [Fact]
    public Task MainContentIsRebuiltOnLanguageChange() => UiThread.Run(() =>
    {
        var app = new TestEnv().NewApp();
        var w = new GodDB.App.Views.MainWindow(app);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var host = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).OfType<Avalonia.Controls.ContentControl>().First(c => c.Name == "ContentHost");
        var before = host.Content;
        var old = GodDB.Core.L10n.Language;
        try
        {
            GodDB.Core.L10n.SetLanguage(old == GodDB.Core.AppLanguage.En ? GodDB.Core.AppLanguage.Ko : GodDB.Core.AppLanguage.En);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(host.Content);
            Assert.NotSame(before, host.Content);
        }
        finally { GodDB.Core.L10n.SetLanguage(old); Dispatcher.UIThread.RunJobs(); w.Close(); }
    });
}
