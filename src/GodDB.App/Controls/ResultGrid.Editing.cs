using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GodDB.App.ViewModels;

namespace GodDB.App.Controls;

// 결과 표: 칸 그 자리 편집 · 외래 키 따라가기 · 머리글 ↗ (Mac: ResultEditingUI.swift · FKFollowUI.swift · QueryWorkspaceView.swift 의 ResultGrid)
public sealed partial class ResultGrid
{
    private FKContext? _fk;

    /// 외래 키 따라가기 (한 테이블 조회 결과일 때만). 바꾸면 머리글 ↗ 를 다시 맞춘다.
    public FKContext? Fk
    {
        get => _fk;
        set
        {
            _fk = value;
            foreach (var c in Grid.Columns)
                if (c.Header is HeaderLabel h && c.Tag is QueryResult.Column col) h.InferredOnly = value?.InferredOnly(col.Id);
        }
    }

    /// 그 자리에서 고칠 수 있는 컬럼 (테이블 컬럼과 이어진 결과 컬럼)
    public IReadOnlySet<int> EditableColumns { get; set; } = new HashSet<int>();
    /// 칸 값 바꾸기 (행 id, 컬럼, 새 값 — null 은 NULL)
    public Action<int, int, string?>? OnSetCell { get; set; }

    /// 마지막으로 우클릭한 칸 (행 id, 컬럼) — "NULL 로 설정" 대상
    private (int Row, int Column)? _pointerCell;

    // MARK: - 머리글 ↗

    /// 컬럼 머리글 글자. 같은 이름 컬럼이 있어도 머리글 칸 → 컬럼을 찾을 수 있게 컬럼마다 다른 객체로 둔다.
    /// 외래 키 컬럼이면 이름 옆에 ↗ (추정 관계만 있으면 흐리게).
    private sealed class HeaderLabel(string name)
    {
        private bool? _inferredOnly;
        public string Name => name;
        /// 진짜 외래 키가 있으면 false, 추정만 있으면 true, 관계 없으면 null
        public bool? InferredOnly
        {
            get => _inferredOnly;
            set
            {
                if (_inferredOnly == value) return;
                _inferredOnly = value;
                Changed?.Invoke();
            }
        }
        public event Action? Changed;
        public override string ToString() => name;
    }

    private static readonly IDataTemplate HeaderTemplate = new FuncDataTemplate<HeaderLabel>((label, _) => new HeaderView(label), supportsRecycling: false);

    private sealed class HeaderView : TextBlock
    {
        private readonly HeaderLabel _label;

        public HeaderView(HeaderLabel label)
        {
            _label = label;
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            Build();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _label.Changed += Build;
            Build();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _label.Changed -= Build;
        }

        private void Build()
        {
            Inlines ??= [];
            Inlines.Clear();
            Inlines.Add(new Run(_label.Name));
            if (_label.InferredOnly is not { } inferred) return;
            var arrow = new Run(" ↗");
            if (inferred)
            {
                // Mac Color.secondary.opacity(0.5)
                var secondary = this.TryFindResource("SecondaryText", ActualThemeVariant, out var s) && s is ISolidColorBrush b ? b.Color : Colors.Gray;
                arrow.Foreground = new SolidColorBrush(secondary, 0.5);
            }
            else arrow.Foreground = Accent;
            Inlines.Add(arrow);
        }
    }

    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#0A84FF"));

    /// 머리글에 보이는 글자 (테스트용, ↗ 포함)
    public string HeaderText(int column) =>
        Grid.Columns.FirstOrDefault(c => c.Tag is QueryResult.Column col && col.Id == column)?.Header is HeaderLabel h
            ? h.Name + (h.InferredOnly != null ? " ↗" : "")
            : "";

    /// 머리글 ↗ 상태 (테스트용): 진짜 외래 키 false, 추정만 true, 없음 null
    public bool? HeaderArrow(int column) =>
        Grid.Columns.FirstOrDefault(c => c.Tag is QueryResult.Column col && col.Id == column)?.Header is HeaderLabel h ? h.InferredOnly : null;

    // MARK: - 칸 찾기

    /// 마우스 이벤트가 떨어진 칸 (칸 밖이면 null)
    private static ResultCell? CellAt(object? source) => (source as Visual)?.FindAncestorOfType<ResultCell>(includeSelf: true);

    // MARK: - 칸 그 자리 편집

    private (int Row, int Column)? _editingCell;
    private readonly HashSet<ResultCell> _cells = [];

    /// 그 자리에서 고치고 있는 칸 (이 칸에만 입력란을 만든다)
    public (int Row, int Column)? EditingCell
    {
        get => _editingCell;
        private set
        {
            if (_editingCell == value) return;
            _editingCell = value;
            foreach (var c in _cells.ToList()) c.SyncEditor();
        }
    }

    /// 지금 열린 입력란 (테스트용)
    public TextBox? ActiveEditor => _cells.Select(c => c.Editor).FirstOrDefault(e => e != null);

    /// 그 자리에서 고칠 수 있는 칸인지: 테이블 컬럼, 삭제 표시 안 한 행, 한 줄 값
    public bool CanEditInline(int row, int column)
    {
        if (!Editable || OnSetCell == null || !EditableColumns.Contains(column) || Edits.Deletes.Contains(row)
            || !_byId.TryGetValue(row, out var g) || column < 0 || column >= g.Values.Count) return false;
        return InlineCellEdit.CanEditInline(g.Values[column]);
    }

    /// 칸 편집 시작 (고칠 수 없는 칸이면 false). 그 행을 고르고 입력란을 연다.
    public bool BeginEditing(int row, int column)
    {
        if (!CanEditInline(row, column)) return false;
        if (!(SelectedIds.Count == 1 && SelectedIds[0] == row))
        {
            // 행을 고를 때 표가 포커스를 가져가지 않게 먼저 편집 칸을 정한다
            _editingCell = (row, column);
            Select([row]);
            _editingCell = null;
        }
        EditingCell = (row, column);
        if (_byId.TryGetValue(row, out var g)) Grid.ScrollIntoView(g, Grid.Columns.FirstOrDefault(c => c.Tag is QueryResult.Column col && col.Id == column));
        return true;
    }

    /// 칸 편집을 끝낸다: 바뀌었으면 변경에 넣고, Tab/Shift+Tab 이면 같은 행 옆 칸으로
    public void FinishEditing(string text, InlineCellEdit.Finish how, bool keyboard)
    {
        if (EditingCell is not { } c) return;
        var original = _byId.TryGetValue(c.Row, out var g) && c.Column < g.Values.Count ? g.Values[c.Column] : null;
        if (InlineCellEdit.CommittedValue(original, text, how) is { } value) OnSetCell?.Invoke(c.Row, c.Column, value);
        if (InlineCellEdit.Neighbor(c.Column, EditableColumns, how) is { } next && CanEditInline(c.Row, next))
        {
            EditingCell = (c.Row, next);
            return;
        }
        EditingCell = null;
        // 키보드로 끝냈으면 표로 포커스를 돌려 화살표 · Ctrl+C 가 이어지게 (다른 곳을 눌렀으면 그대로)
        if (keyboard) Grid.Focus();
    }

    /// 칸을 고치는 중이면 지금 값을 넣고 끝낸다 (적용 · 되돌리기 · 페이지 이동 · 정렬 등 전에)
    public void EndActiveEditing()
    {
        if (EditingCell == null) return;
        if (ActiveEditor is { } box) FinishEditing(box.Text ?? "", InlineCellEdit.Finish.Commit, keyboard: false);
        else EditingCell = null;
    }

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        // 편집 가능한 결과에서 칸 더블클릭: 그 칸을 그 자리에서 고친다.
        // 그 밖(편집 불가 결과 · 테이블 컬럼이 아닌 칸 · 여러 줄 값 · 칸 밖): 첫 클릭으로 고른 행의 상세 패널이 그대로 열려 있다.
        if (e.Source is TextBox) return;
        if (CellAt(e.Source) is { Row: { } row } cell && BeginEditing(row.Id, cell.Column)) e.Handled = true;
    }

    // MARK: - 셀

    /// 한 칸 (한 줄 미리보기, 고치는 칸이면 한 줄 입력란). 행이 바뀌거나(재활용) 값 · 편집 상태가 바뀌면 다시 그린다.
    private sealed class ResultCell : Panel
    {
        private readonly ResultGrid _grid;
        private readonly TextBlock _text;
        private GridRow? _row;
        private TextBox? _editor;
        private bool _editorFinished;
        private bool _editorFocused;

        public int Column { get; }
        public GridRow? Row => _row;
        public TextBox? Editor => _editor;

        public ResultCell(ResultGrid grid, int column)
        {
            _grid = grid;
            Column = column;
            Background = Brushes.Transparent;
            _text = new TextBlock
            {
                FontFamily = Mono,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Children.Add(_text);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _grid._cells.Add(this);
            SyncEditor();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _grid._cells.Remove(this);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_row != null) _row.PropertyChanged -= OnRowChanged;
            _row = DataContext as GridRow;
            if (_row != null) _row.PropertyChanged += OnRowChanged;
            Refresh();
            SyncEditor();
        }

        private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

        private void Refresh()
        {
            if (_row == null) { _text.Text = ""; return; }
            var value = Column < _row.Values.Count ? _row.Values[Column] : null;
            var look = QueryUi.Cell(_grid.Edits.State(_row.Id, Column), value);
            _text.Text = look.Text;
            _text.FontWeight = look.Bold ? FontWeight.SemiBold : FontWeight.Normal;
            _text.TextDecorations = look.Strike ? TextDecorations.Strikethrough : null;
            if (ToneBrush(look.Tone, _text) is { } b) _text.Foreground = b;
            else _text.ClearValue(TextBlock.ForegroundProperty);
        }

        /// 고치는 칸이면 입력란을 만들고, 아니면 없앤다 (입력란은 고치는 칸 하나에만 — 행이 많아도 느려지지 않게)
        public void SyncEditor()
        {
            var editing = _row != null && _grid.EditingCell is { } c && c.Row == _row.Id && c.Column == Column;
            if (editing && _editor == null)
            {
                var value = Column < _row!.Values.Count ? _row.Values[Column] : null;
                var box = new TextBox
                {
                    Text = InlineCellEdit.InitialText(value),
                    FontFamily = Mono,
                    FontSize = 12,
                    MinHeight = 0,
                    Padding = new Thickness(3, 0),
                    BorderThickness = new Thickness(1),
                    VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    AcceptsReturn = false,
                    AcceptsTab = false,
                };
                AutomationProperties.SetName(box, Tr("칸 편집"));
                _editorFinished = false;
                _editorFocused = false;
                box.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
                box.GotFocus += (_, _) => _editorFocused = true;
                box.LostFocus += OnEditorLostFocus;
                box.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_editor, box)) return;
                    box.Focus();
                    box.SelectAll();
                });
                _editor = box;
                _text.IsVisible = false;
                Children.Add(box);
            }
            else if (!editing && _editor != null)
            {
                _editorFinished = true;
                var box = _editor;
                _editor = null;
                Children.Remove(box);
                _text.IsVisible = true;
            }
        }

        private void OnEditorKeyDown(object? sender, KeyEventArgs e)
        {
            if (_editor is not { } box || _editorFinished) return;
            InlineCellEdit.Finish? how = e.Key switch
            {
                Key.Enter when e.KeyModifiers == KeyModifiers.None => InlineCellEdit.Finish.Commit,
                Key.Escape => InlineCellEdit.Finish.Cancel,
                Key.Tab when e.KeyModifiers == KeyModifiers.Shift => InlineCellEdit.Finish.Previous,
                Key.Tab when e.KeyModifiers == KeyModifiers.None => InlineCellEdit.Finish.Next,
                _ => null,
            };
            if (how is not { } h) return;
            e.Handled = true;
            _editorFinished = true;
            _grid.FinishEditing(box.Text ?? "", h, keyboard: true);
        }

        /// 다른 곳을 눌러 포커스가 빠지면 넣는다 (한 번도 포커스를 받지 않은 채 빠진 것은 무시 — 열자마자 닫히지 않게)
        private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box || !ReferenceEquals(box, _editor) || _editorFinished || !_editorFocused) return;
            // 포커스가 이 입력란 안(문맥 메뉴 등)에 남아 있으면 그대로
            if (box.IsKeyboardFocusWithin) return;
            _editorFinished = true;
            var text = box.Text ?? "";
            // 레이아웃 도중에 표를 바꾸지 않게 다음 차례에 넣는다
            Dispatcher.UIThread.Post(() =>
            {
                if (_grid.EditingCell is { } c && _row != null && c.Row == _row.Id && c.Column == Column)
                    _grid.FinishEditing(text, InlineCellEdit.Finish.Commit, keyboard: false);
            });
        }
    }
}
