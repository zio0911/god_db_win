using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using GodDB.App.ViewModels;

namespace GodDB.App.Controls;

/// 쿼리 편집기 빨간 물결 밑줄 (Mac HintTextView 의 scheduleLint · drawIssues · 툴팁).
/// 입력이 0.3초 멈추면 GodDB 검사(SqlLint), 1초 멈추면 커서가 있는 구문을 DB 에 실행 없이 검사 요청.
/// 마우스를 올리면 이유를 툴팁으로 보여 주고, 화면 읽기 프로그램용 도움말(AutomationProperties.HelpText)에도 넣는다.
public partial class SqlEditor
{
    private List<SqlIssue> _issues = [];
    private DispatcherTimer? _lintTimer;
    private CancellationTokenSource? _serverCts;
    private readonly LintRenderer _lintRenderer = new();

    /// 빨간 밑줄 검사용 (테이블, 접속 종류, 지금 DB)
    public Func<(IReadOnlyList<TableMeta> Tables, DatabaseKind? Kind, string? Schema)> LintContext { get; set; } = () => ([], null, null);
    /// DB 에 검사만 맡기기 (null: 묻지 않음, Failure 가 null: 문제 없음)
    public Func<string, Task<DbSession.ServerCheckOutcome?>> ServerChecker { get; set; } = _ => Task.FromResult<DbSession.ServerCheckOutcome?>(null);
    /// 편집기 우클릭 > 쿼리 저장…
    public event Action? SaveQueryRequested;
    /// 밑줄이 바뀌었을 때 (테스트용)
    public event Action? IssuesChanged;

    /// 지금 보이는 밑줄
    public IReadOnlyList<SqlIssue> Issues => _issues;
    /// DB 검사 요청이 끝났을 때 (테스트용)
    public Task? PendingServerCheck { get; private set; }
    /// DB 검사를 기다리는 시간 (테스트에서 줄인다)
    public TimeSpan ServerCheckDelay { get; set; } = TimeSpan.FromSeconds(1);

    private void InitLint()
    {
        _lintRenderer.Editor = this;
        TextArea.TextView.BackgroundRenderers.Add(_lintRenderer);
        TextArea.TextView.PointerMoved += OnLintPointerMoved;
        TextArea.TextView.PointerExited += (_, _) => HideLintTip();
    }

    /// 입력이 멈추면 검사: 0.3초 뒤 GodDB 검사, 1초 뒤 커서 구문을 DB 에 검사 요청
    public void ScheduleLint(bool clear = false)
    {
        _lintTimer?.Stop();
        _serverCts?.Cancel();
        _serverCts = null;
        if (clear && _issues.Count > 0) SetIssues([]);
        // 글자가 바뀌면 DB 검사 결과는 낡았으니 지운다
        if (_issues.Any(i => i.Source == SqlIssue.IssueSource.Server)) SetIssues(_issues.Where(i => i.Source == SqlIssue.IssueSource.Local).ToList());
        if (!EditorLint.Enabled) { if (_issues.Count > 0) SetIssues([]); return; }
        _lintTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.3) };
        _lintTimer.Tick -= OnLintTick;
        _lintTimer.Tick += OnLintTick;
        _lintTimer.Start();
        var text = _text;
        var cursor = CaretOffset;
        var cts = new CancellationTokenSource();
        _serverCts = cts;
        PendingServerCheck = ServerCheckAfterDelayAsync(text, cursor, cts.Token);
    }

    private void OnLintTick(object? sender, EventArgs e)
    {
        _lintTimer?.Stop();
        RunLocalLint();
    }

    /// GodDB 검사를 바로 한다 (0.3초 기다리지 않음 — 테스트용으로도 쓴다)
    public void RunLocalLint()
    {
        _lintTimer?.Stop();
        if (!EditorLint.Enabled) { SetIssues([]); return; }
        var ctx = LintContext();
        if (ctx.Kind == DatabaseKind.Redis) { SetIssues([]); return; }
        var local = SqlLint.Check(_text, ctx.Tables, ctx.Kind, ctx.Schema);
        SetIssues([.. local, .. _issues.Where(i => i.Source == SqlIssue.IssueSource.Server)]);
    }

    private async Task ServerCheckAfterDelayAsync(string text, int cursor, CancellationToken ct)
    {
        try
        {
            // 0 이어도 한 번은 양보해서, 바로 이어진 입력이 앞 요청을 취소할 수 있게 한다
            if (ServerCheckDelay > TimeSpan.Zero) await Task.Delay(ServerCheckDelay, ct);
            else await Task.Yield();
        }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested || !EditorLint.ServerEnabled) return;
        var stmt = GodDB.Core.Sql.SqlText.Statements(text).Cast<SqlStatement?>().FirstOrDefault(s => s!.Value.Start <= cursor && cursor <= s.Value.End);
        if (stmt is not { } st) return;
        // GodDB 검사에서 이미 문제가 보이면 DB 에 묻지 않는다
        var from = Math.Min(st.Start, text.Length);
        var found = text.IndexOf(st.Sql, from, StringComparison.Ordinal);
        if (found < 0) return;
        var foundEnd = found + st.Sql.Length;
        var ctx = LintContext();
        var local = SqlLint.Check(text, ctx.Tables, ctx.Kind, ctx.Schema);
        if (local.Any(i => i.Start < foundEnd && found < i.End)) return;
        DbSession.ServerCheckOutcome? result;
        try { result = await ServerChecker(st.Sql); }
        catch { return; }
        if (ct.IsCancellationRequested || _text != text || result?.Failure is not { } failure) return;
        var issue = failure.Start is int s && failure.Length is int l
            ? new SqlIssue(s + found, l, Tr("DB 검사: %@", failure.Message), source: SqlIssue.IssueSource.Server)
            : new SqlIssue(found, st.Sql.Length, Tr("DB 검사: %@", failure.Message), source: SqlIssue.IssueSource.Server);
        SetIssues([.. _issues.Where(i => i.Source == SqlIssue.IssueSource.Local), issue]);
    }

    private void SetIssues(List<SqlIssue> issues)
    {
        var length = Document?.TextLength ?? 0;
        _issues = issues.Where(i => i.End <= length).ToList();
        // 화면 읽기 · 자동 테스트용
        AutomationProperties.SetHelpText(this, _issues.Count == 0 ? null : string.Join("\n", _issues.Select(i => i.Text)));
        TextArea.TextView.InvalidateLayer(_lintRenderer.Layer);
        IssuesChanged?.Invoke();
    }

    /// 화면 위치(TextView 기준)에 있는 밑줄 (없으면 null)
    public SqlIssue? IssueAt(int offset) => _issues.FirstOrDefault(i => i.Start <= offset && offset < Math.Max(i.End, i.Start + 1));

    private SqlIssue? _tipIssue;

    private void OnLintPointerMoved(object? sender, PointerEventArgs e)
    {
        var tv = TextArea.TextView;
        if (_issues.Count == 0 || tv.Document == null) { HideLintTip(); return; }
        var p = e.GetPosition(tv) + tv.ScrollOffset;
        var pos = tv.GetPositionFloor(p);
        if (pos is not { } vp) { HideLintTip(); return; }
        var offset = tv.Document.GetOffset(vp.Location);
        var issue = IssueAt(offset);
        if (issue == null) { HideLintTip(); return; }
        if (ReferenceEquals(issue, _tipIssue)) return;
        _tipIssue = issue;
        ToolTip.SetTip(tv, issue.Text);
        ToolTip.SetIsOpen(tv, true);
    }

    private void HideLintTip()
    {
        if (_tipIssue == null) return;
        _tipIssue = null;
        var tv = TextArea.TextView;
        ToolTip.SetIsOpen(tv, false);
        ToolTip.SetTip(tv, null);
    }

    /// 빨간 물결 밑줄 (Mac drawIssues: 2pt 간격 지그재그, 높이 2)
    private sealed class LintRenderer : IBackgroundRenderer
    {
        private static readonly IPen Pen = new Pen(new SolidColorBrush(Color.Parse("#FF3B30")), 1);
        public SqlEditor? Editor { get; set; }
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Editor is not { } editor || editor._issues.Count == 0 || textView.Document == null) return;
            var docLength = textView.Document.TextLength;
            foreach (var issue in editor._issues)
            {
                if (issue.End > docLength) continue;
                var segment = new TextSegment { StartOffset = issue.Start, EndOffset = Math.Max(issue.End, issue.Start + (issue.Start < docLength ? 1 : 0)) };
                foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                {
                    var y = r.Bottom - 1.5;
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        var x = r.Left;
                        g.BeginFigure(new Point(x, y), false);
                        var up = true;
                        while (x < r.Right)
                        {
                            x += 2;
                            g.LineTo(new Point(Math.Min(x, r.Right), up ? y - 2 : y));
                            up = !up;
                        }
                        g.EndFigure(false);
                    }
                    drawingContext.DrawGeometry(null, Pen, geo);
                }
            }
        }
    }
}
