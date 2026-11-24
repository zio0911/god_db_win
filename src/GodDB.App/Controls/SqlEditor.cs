using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using GodDB.App.Infrastructure;

namespace GodDB.App.Controls;

/// 문서 줄바꿈을 LF 로만 유지한다 (Windows 전용 처리).
/// Core 의 SqlText · SqlEditing · InsertHints 오프셋은 "\n" 기준이라, 편집기 글에 "\r\n" 이 섞이면 위치가 어긋난다.
/// AvaloniaEdit 은 Enter 를 칠 때 문서의 기존 줄바꿈(없으면 Environment.NewLine = "\r\n")을 쓰고, 붙여 넣기도 그대로 넣으므로
/// 바뀐 글에 \r 이 들어오면 같은 변경 묶음(되돌리기 한 번) 안에서 "\n" 으로 바꾼다.
public static class LineEndings
{
    private static readonly ConditionalWeakTable<TextDocument, object> Attached = new();

    public static string Normalize(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;

    /// 파일 글의 줄바꿈 ("\r\n" / "\r" / "\n"). 섞여 있으면 CRLF 를 우선한다.
    /// Mac 은 편집기 글을 그대로 저장해서 원래 줄바꿈이 유지된다. 여기서는 편집기 안을 LF 로 두므로 열 때 기억했다가 저장할 때 되돌린다.
    public static string Detect(string text) =>
        text.Contains("\r\n") ? "\r\n" : text.Contains('\r') ? "\r" : "\n";

    /// LF 글을 주어진 줄바꿈으로 바꾼다 (파일에 쓸 때)
    public static string Apply(string lfText, string ending) =>
        ending == "\n" ? lfText : Normalize(lfText).Replace("\n", ending);

    /// 문서에 LF 유지 장치를 붙인다 (이미 붙어 있으면 그대로). 지금 글에 \r 이 있으면 바로 바꾼다.
    public static void Attach(TextDocument doc)
    {
        if (Attached.TryGetValue(doc, out _)) return;
        Attached.Add(doc, new object());
        var pending = false;
        doc.Changed += (_, e) =>
        {
            if (e.InsertionLength > 0 && e.InsertedText.Text.Contains('\r')) pending = true;
        };
        // TextChanged 는 변경 묶음이 끝나기 직전에 불린다: 여기서 고친 것은 같은 되돌리기 단위에 들어간다
        doc.TextChanged += (_, _) =>
        {
            if (!pending) return;
            pending = false;
            NormalizeInPlace(doc);
        };
        if (doc.TextLength > 0 && doc.Text.Contains('\r')) NormalizeInPlace(doc);
    }

    private static void NormalizeInPlace(TextDocument doc)
    {
        var text = doc.Text;
        if (!text.Contains('\r')) return;
        doc.BeginUpdate();
        try
        {
            for (var i = text.Length - 1; i >= 0; i--)
            {
                if (text[i] != '\r') continue;
                if (i + 1 < text.Length && text[i + 1] == '\n') doc.Remove(i, 1);
                else doc.Replace(i, 1, "\n");
            }
        }
        finally
        {
            doc.EndUpdate();
        }
    }
}

/// SQL 편집기 (Mac SQLEditorView + HintTextView).
/// - 키워드 · 문자열 · 숫자 · 식별자 · 주석 색칠 (DB 별 키워드), 자동 따옴표 변환 같은 것은 없음 (AvaloniaEdit 기본)
/// - Tab: SqlCompleter.Tab (들여쓰기 / 바로 채움 / 목록 ↑↓ Enter Esc / 경고음)
/// - Ctrl+/ 주석 토글, Ctrl+Alt+L SQL 정렬, 선택이 없으면 Ctrl+Enter 로 실행될 문장을 옅게 칠함
/// - INSERT … VALUES 값 앞에 컬럼 이름을 회색으로 (문서 글자가 아닌 화면 요소 — 복사·실행 SQL 은 그대로)
/// 키 처리는 이 단축키들만 가로챈다 (한글 IME 입력은 AvaloniaEdit 기본 처리 그대로).
public partial class SqlEditor : TextEditor
{
    protected override Type StyleKeyOverride => typeof(TextEditor);

    private readonly SqlColorizer _colorizer = new();
    private readonly InsertHintGenerator _hints = new();
    private readonly StatementHighlighter _statement;
    private CompletionWindow? _completion;
    private string _text = "";
    private DatabaseKind _kind = DatabaseKind.Mysql;

    /// 자동완성 · INSERT 힌트에 쓸 현재 스키마 (테이블, 뷰/프로시저)
    public Func<(IReadOnlyList<TableMeta> Tables, IReadOnlyList<RoutineMeta> Routines)> SchemaProvider { get; set; } = () => ([], []);

    /// 후보가 없어 경고음을 냈을 때 (테스트용)
    public event Action? Beeped;
    /// 글이 바뀐 뒤 (LF 로 맞춘 다음)
    public event Action? SqlTextChanged;

    /// 마지막 Tab 처리 결과 (테스트용)
    public TabAction? LastTabAction { get; private set; }
    /// 지금 떠 있는 자동완성 목록의 후보 (없으면 null)
    public IReadOnlyList<string>? CompletionCandidates { get; private set; }
    public IReadOnlyList<InsertHints.Hint> CurrentHints => _hints.Hints;
    public IReadOnlyList<SqlToken> CurrentTokens => _colorizer.Tokens;

    /// 색칠할 키워드의 DB 종류
    public DatabaseKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            RefreshDecorations();
        }
    }

    public SqlEditor()
    {
        Styles.Add(new StyleInclude(new Uri("avares://GodDB/")) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
        FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace");
        FontSize = 13;
        WordWrap = true;
        ShowLineNumbers = false;
        Padding = new Thickness(6, 8);
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.HighlightCurrentLine = false;
        Options.ConvertTabsToSpaces = true;
        Options.IndentationSize = 4;
        Options.EnableTextDragDrop = true;
        _statement = new StatementHighlighter(this);
        TextArea.TextView.LineTransformers.Add(_colorizer);
        TextArea.TextView.ElementGenerators.Add(_hints);
        TextArea.TextView.BackgroundRenderers.Add(_statement);
        TextArea.Caret.PositionChanged += (_, _) => TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        TextArea.SelectionChanged += (_, _) => TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        TextArea.DocumentChanged += (_, _) => OnDocumentSwapped();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ContextMenu = BuildContextMenu();
        InitLint();
        Document ??= new TextDocument();
        OnDocumentSwapped();
    }

    private TextDocument? _attached;

    private void OnDocumentSwapped()
    {
        CloseCompletion();
        if (_attached != null) _attached.TextChanged -= OnDocumentTextChanged;
        _attached = Document;
        if (_attached == null) return;
        LineEndings.Attach(_attached);
        _attached.TextChanged += OnDocumentTextChanged;
        _text = _attached.Text;
        RefreshDecorations();
        // 다른 탭의 문서로 바뀌면 앞 문서의 밑줄은 바로 지우고 다시 검사한다
        ScheduleLint(clear: true);
    }

    private void OnDocumentTextChanged(object? sender, EventArgs e)
    {
        if (Document is not { } doc) return;
        var text = doc.Text;
        if (text.Contains('\r')) return;   // LF 로 바꾸는 중 — 바꾼 뒤 다시 불린다
        _text = text;
        RefreshDecorations();
        ScheduleLint();
        SqlTextChanged?.Invoke();
    }

    /// 지금 문서 글 (LF)
    public string SqlText => _text;

    /// 색칠 · INSERT 힌트를 다시 계산한다 (스키마나 설정이 바뀌었을 때도 부른다)
    public void RefreshDecorations()
    {
        _colorizer.Update(_text, _kind);
        // Mac HintTextView.enabled: INSERT 컬럼 이름 표시는 항상 켬 (설정에서 끄는 옵션 없음)
        var show = _text.Length < SqlHighlighter.MaxLength;
        var hints = show ? InsertHints.Displayable(_text, SchemaProvider().Tables) : [];
        hints.Sort((a, b) => a.Location.CompareTo(b.Location));
        _hints.Hints = hints;
        // 글이 바뀌면 AvaloniaEdit 이 바뀐 줄을 다시 그리지만, 힌트·색은 다른 줄(INSERT 컬럼 목록)에 따라 바뀌므로 전체를 다시 그린다
        TextArea.TextView.Redraw();
    }

    /// 글 전체를 바꾼다 (탭의 글이 밖에서 바뀌었을 때). LF 로 맞춘다.
    public void SetSqlText(string text)
    {
        var normalized = LineEndings.Normalize(text);
        if (normalized == _text && Document != null) return;
        Document ??= new TextDocument();
        Document.Text = normalized;
    }

    // MARK: - 선택 · 실행할 SQL

    private (int Start, int Length) Sel()
    {
        if (TextArea.Selection.IsEmpty) return (CaretOffset, 0);
        return (SelectionStart, SelectionLength);
    }

    /// 선택 영역 (없거나 공백뿐이면 null)
    public string? SelectedSql()
    {
        var (s, l) = Sel();
        if (l == 0 || s + l > _text.Length) return null;
        var t = _text.Substring(s, l);
        return string.IsNullOrWhiteSpace(t) ? null : t;
    }

    /// 선택 영역이 있으면 선택 영역, 없으면 커서가 있는 문장 (실행 계획 · 쿼리 저장용)
    public string? SqlToRun()
    {
        var (s, l) = Sel();
        if (l > 0 && s + l <= _text.Length) return _text.Substring(s, l);
        return GodDB.Core.Sql.SqlText.Statement(_text, Math.Min(s, _text.Length));
    }

    // MARK: - 키

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        if (e.Key == Key.Tab && mods == KeyModifiers.None)
        {
            // 목록이 떠 있으면 목록이 Tab 을 처리한다
            if (_completion != null) return;
            if (HandleTab() != null) e.Handled = true;
            return;
        }
        // 입력 방식(한글 등)과 상관없이 물리 키로도 본다 (Mac: 키 번호 44 = /, 37 = L)
        if (mods == KeyModifiers.Control && (e.PhysicalKey == PhysicalKey.Slash || e.Key is Key.Oem2 or Key.Divide))
        {
            ToggleComment();
            e.Handled = true;
        }
        else if (mods == (KeyModifiers.Control | KeyModifiers.Alt) && (e.PhysicalKey == PhysicalKey.L || e.Key == Key.L))
        {
            FormatSql();
            e.Handled = true;
        }
    }

    /// Tab 키 처리. 기본 동작(선택 영역 들여쓰기)에 맡기면 null.
    public TabAction? HandleTab()
    {
        var (s, l) = Sel();
        var (tables, routines) = SchemaProvider();
        var action = SqlCompleter.Tab(_text, s, l, tables, routines);
        LastTabAction = action;
        switch (action)
        {
            case null:
                return null;
            case TabAction.Indent i:
                Document.Insert(i.Position, i.Text);
                CaretOffset = i.Position + i.Text.Length;
                break;
            case TabAction.Replace r:
                Document.Replace(r.Start, r.Length, r.Text);
                CaretOffset = r.Start + r.Text.Length;
                break;
            case TabAction.ShowList list:
                ShowCompletion(list);
                break;
            case TabAction.Beep:
                Beep();
                break;
        }
        return action;
    }

    private void ShowCompletion(TabAction.ShowList list)
    {
        CloseCompletion();
        CompletionCandidates = list.Candidates;
        try
        {
            var w = new CompletionWindow(TextArea)
            {
                StartOffset = list.Start,
                EndOffset = list.Start + list.Length,
                CloseWhenCaretAtBeginning = true,
            };
            foreach (var c in list.Candidates) w.CompletionList.CompletionData.Add(new SqlCompletionItem(c));
            // 잘못 쓴 이름의 "비슷한 후보" 목록(a.username → name …)은 입력한 글자로 거르면 목록이 비므로 거르지 않는다 (Mac 1.4.2)
            var typed = list.Start + list.Length <= _text.Length ? _text.Substring(list.Start, list.Length) : "";
            if (list.Candidates.Any(c => !c.StartsWith(typed, StringComparison.OrdinalIgnoreCase))) w.CompletionList.IsFiltering = false;
            w.Closed += (_, _) =>
            {
                if (ReferenceEquals(_completion, w)) { _completion = null; CompletionCandidates = null; }
            };
            _completion = w;
            if (TopLevel.GetTopLevel(this) != null)
            {
                w.Show();
                // 첫 후보를 고른 채로 (Mac 과 같음). 목록 템플릿이 적용된 뒤에만 고를 수 있다.
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_completion, w) && w.CompletionList.ListBox != null)
                        w.CompletionList.SelectedItem = w.CompletionList.CompletionData.FirstOrDefault();
                });
            }
        }
        catch
        {
            // 화면에 붙지 않은 편집기(테스트)에서는 목록을 띄우지 못할 수 있다 — 후보만 기억한다
        }
    }

    /// 자동완성 목록에서 후보를 고른다 (Enter 와 같음, 테스트용)
    public void CommitCompletion(int index = 0)
    {
        if (_completion is not { } w || CompletionCandidates is not { } c || index < 0 || index >= c.Count) return;
        var start = w.StartOffset;
        var end = Math.Max(w.EndOffset, CaretOffset);
        var text = c[index];
        CloseCompletion();
        Document.Replace(start, end - start, text);
        CaretOffset = start + text.Length;
    }

    public void CloseCompletion()
    {
        var w = _completion;
        _completion = null;
        CompletionCandidates = null;
        try { w?.Hide(); } catch { }
    }

    private void Beep()
    {
        Beeped?.Invoke();
        if (OperatingSystem.IsWindows())
        {
            try { MessageBeep(0xFFFFFFFF); } catch { }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);

    // MARK: - 주석 토글 · SQL 정렬

    private void Apply(TextEdit e)
    {
        Document.Replace(e.Start, e.Length, e.Replacement);
        Select(e.SelectionStart, e.SelectionLength);
    }

    public void ToggleComment()
    {
        var (s, l) = Sel();
        Apply(SqlEditing.ToggleLineComment(_text, s, l));
    }

    /// 선택 영역이 있으면 그 부분, 없으면 커서가 있는 문장을 정렬한다
    public void FormatSql()
    {
        var (s, l) = Sel();
        if (SqlEditing.FormatSelectionOrStatement(_text, s, l) is { } e) Apply(e);
    }

    /// 선택이 없고 문장이 여러 개면 칠할 범위
    public (int Start, int End)? StatementRange()
    {
        var (s, l) = Sel();
        return SqlEditing.StatementHighlight(_text, s, l);
    }

    private ContextMenu BuildContextMenu()
    {
        MenuItem Item(string key, Action action, string? gesture = null)
        {
            var m = new MenuItem();
            m.Bind(MenuItem.HeaderProperty, Loc.Bind(key));
            if (gesture != null) m.InputGesture = KeyGesture.Parse(gesture);
            m.Click += (_, _) => action();
            return m;
        }
        var menu = new ContextMenu();
        menu.Items.Add(Item("주석 토글 (Ctrl+/)", ToggleComment));
        menu.Items.Add(Item("SQL 정렬 (Ctrl+Alt+L)", FormatSql));
        menu.Items.Add(new Separator());
        // 선택 영역(없으면 커서 문장)을 저장 쿼리로 (Mac 1.4.0)
        menu.Items.Add(Item("쿼리 저장… (Ctrl+Alt+S)", () => SaveQueryRequested?.Invoke()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("잘라내기", Cut, "Ctrl+X"));
        menu.Items.Add(Item("복사", Copy, "Ctrl+C"));
        menu.Items.Add(Item("붙여넣기", Paste, "Ctrl+V"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("모두 선택", SelectAll, "Ctrl+A"));
        return menu;
    }

    // MARK: - 그리기 도우미

    private sealed class SqlColorizer : DocumentColorizingTransformer
    {
        private static readonly IBrush KeywordBrush = new SolidColorBrush(Color.Parse("#2F7CF6"));
        private static readonly IBrush NumberBrush = new SolidColorBrush(Color.Parse("#AF52DE"));
        private static readonly IBrush IdentBrush = new SolidColorBrush(Color.Parse("#20A4B8"));
        private static readonly IBrush StringBrush = new SolidColorBrush(Color.Parse("#E8870E"));
        private static readonly IBrush CommentBrush = new SolidColorBrush(Color.Parse("#8E8E93"));

        public List<SqlToken> Tokens { get; private set; } = [];

        public void Update(string text, DatabaseKind kind) => Tokens = SqlHighlighter.Tokens(text, kind);

        protected override void ColorizeLine(DocumentLine line)
        {
            var tokens = Tokens;
            if (tokens.Count == 0) return;
            int ls = line.Offset, le = line.EndOffset;
            // 줄과 겹치는 첫 토큰 (End > ls)
            int lo = 0, hi = tokens.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (tokens[mid].End <= ls) lo = mid + 1; else hi = mid;
            }
            var docLength = CurrentContext.Document.TextLength;
            for (var i = lo; i < tokens.Count && tokens[i].Start < le; i++)
            {
                var t = tokens[i];
                int s = Math.Max(t.Start, ls), e = Math.Min(Math.Min(t.End, le), docLength);
                if (e <= s) continue;
                var brush = t.Kind switch
                {
                    SqlTokenKind.Keyword => KeywordBrush,
                    SqlTokenKind.Number => NumberBrush,
                    SqlTokenKind.Identifier => IdentBrush,
                    SqlTokenKind.String => StringBrush,
                    _ => CommentBrush,
                };
                ChangeLinePart(s, e, el => el.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }

    /// 실행할 문장 옅게 칠하기 (Mac drawBackground)
    private sealed class StatementHighlighter(SqlEditor editor) : IBackgroundRenderer
    {
        private static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(18, 0x0A, 0x84, 0xFF));

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (textView.Document == null || editor.StatementRange() is not { } r) return;
            var docLength = textView.Document.TextLength;
            if (r.End > docLength) return;
            var segment = new TextSegment { StartOffset = r.Start, EndOffset = r.End };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                drawingContext.FillRectangle(Fill, new Rect(0, rect.Y, Math.Max(textView.Bounds.Width, rect.Right), rect.Height));
        }
    }

    /// INSERT 컬럼 힌트: 값 바로 앞에 길이 0 짜리 화면 요소를 넣는다 (문서 글자는 그대로)
    private sealed class InsertHintGenerator : VisualLineElementGenerator
    {
        public List<InsertHints.Hint> Hints { get; set; } = [];

        private int IndexAtOrAfter(int offset)
        {
            int lo = 0, hi = Hints.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (Hints[mid].Location < offset) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (Hints.Count == 0) return -1;
            var end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
            var i = IndexAtOrAfter(startOffset);
            return i < Hints.Count && Hints[i].Location <= end && Hints[i].Location < CurrentContext.Document.TextLength
                ? Hints[i].Location : -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var i = IndexAtOrAfter(offset);
            if (i >= Hints.Count || Hints[i].Location != offset) return null;
            return new HintElement(HintBox(Hints[i].Column));
        }

        private static Control HintBox(string column) => new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 0x8E, 0x8E, 0x93)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(3, 0),
            Margin = new Thickness(1, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = column,
                FontSize = 10,
                FontWeight = FontWeight.Medium,
                FontFamily = FontFamily.Default,
                Foreground = new SolidColorBrush(Color.Parse("#8E8E93")),
            },
        };
    }

    /// 문서 길이 0 인 화면 요소. 커서는 이 요소 안에 멈추지 않는다.
    private sealed class HintElement(Control box) : InlineObjectElement(0, box)
    {
        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode) => -1;
    }

    private sealed class SqlCompletionItem(string text) : ICompletionData
    {
        public IImage? Image => null;
        public string Text { get; } = text;
        public object Content => Text;
        public object? Description => null;
        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, Text);
    }
}
