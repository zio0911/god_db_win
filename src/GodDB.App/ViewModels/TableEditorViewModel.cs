using CommunityToolkit.Mvvm.ComponentModel;

namespace GodDB.App.ViewModels;

/// 테이블 수정 화면 상태 (Mac TableEditorView.swift 의 @State 와 편집 동작).
/// 왼쪽 트리(테이블 > 컬럼 / 키 / 인덱스 / 외래 키), 오른쪽 고른 항목의 속성, 아래 ALTER 미리보기.
public sealed partial class TableEditorViewModel : ObservableObject
{
    /// 트리에서 고른 항목
    public abstract record Node
    {
        public sealed record Table : Node;
        public sealed record Column(Guid Id) : Node;
        public sealed record Index(Guid Id) : Node;
        public sealed record ForeignKey(string Name) : Node;
        public sealed record DeletedColumn(string Name) : Node;
    }

    public AppState App { get; }
    public TableMeta Table { get; }
    public List<EditableColumn> Columns { get; }
    public List<EditableIndex> Indexes { get; }
    [ObservableProperty] private string _comment;
    [ObservableProperty] private Node? _selection = new Node.Table();
    [ObservableProperty] private bool _executing;
    [ObservableProperty] private string? _execError;

    /// 컬럼 · 인덱스 · 코멘트가 바뀌었을 때 (트리 · 미리보기를 다시 그린다)
    public event Action? Changed;

    public TableEditorViewModel(AppState app, TableMeta table)
    {
        App = app;
        Table = table;
        Columns = table.Columns.OrderBy(c => c.Position).Select(c => new EditableColumn(c)).ToList();
        Indexes = table.Indexes.Select(i => new EditableIndex(i)).ToList();
        _comment = table.Comment;
    }

    public DatabaseKind Kind => App.Dialect.Kind;
    public bool ReadOnly => App.SelectedProfile?.ReadOnly == true;
    public string ConnectionName => App.SelectedProfile?.Name ?? "";
    public string Database => App.CurrentDatabase;

    partial void OnCommentChanged(string value) => Touch();
    partial void OnExecutingChanged(bool value) => OnPropertyChanged(nameof(CanExecute));

    /// 컬럼·인덱스를 직접 고친 뒤 부른다
    public void Touch()
    {
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(SqlText));
        OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(ChangeSummary));
        Changed?.Invoke();
    }

    // MARK: - 미리보기

    /// (SQL, 오류) — 변경이 없으면 둘 다 null
    public (string? Sql, string? Error) Preview
    {
        get
        {
            try { return (TableAlter.Statement(Table, Columns, Indexes, Comment, App.Dialect), null); }
            catch (TableAlterException e) { return (null, e.Message); }
            catch (Exception e) { return (null, e.Message); }
        }
    }

    public string? SqlText => Preview.Sql;

    /// 미리보기 칸에 보일 글
    public string PreviewText => Preview switch
    {
        ({ } sql, _) => sql,
        (_, { } err) => "-- " + err,
        _ => Tr("-- 변경 사항이 없어요."),
    };

    public bool CanExecute => SqlText != null && !ReadOnly && !Executing;

    public string ChangeSummary
    {
        get
        {
            try
            {
                var list = TableAlter.Changes(Table, Columns, Indexes, Comment);
                return list.Count == 0 ? Tr("변경 없음") : Tr("변경 %@건", list.Count);
            }
            catch { return Tr("변경 없음"); }
        }
    }

    // MARK: - 트리 정보

    /// 기본 키 인덱스 위치
    public int? PrimaryIndex
    {
        get
        {
            for (var i = 0; i < Indexes.Count; i++)
            {
                var e = Indexes[i];
                if (e.OriginalName is { } n && Table.Indexes.FirstOrDefault(o => o.Name == n) is { } o)
                {
                    if (o.IsPrimary) return i;
                }
                else if (e.Name == "PRIMARY" || e.Name.StartsWith("pk_", StringComparison.Ordinal)) return i;
            }
            return null;
        }
    }

    public List<ColumnMeta> DeletedColumns
    {
        get
        {
            var kept = Columns.Select(c => c.OriginalName).OfType<string>().ToHashSet();
            return Table.Columns.Where(c => !kept.Contains(c.Name)).ToList();
        }
    }

    public bool IsPK(string name) =>
        PrimaryIndex is { } p && Indexes[p].Columns.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));

    public bool IsFK(string name) =>
        Table.ForeignKeys.Any(fk => fk.Columns.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public enum Mark { None, New, Changed }

    public Mark ColumnMark(EditableColumn c)
    {
        if (c.OriginalName == null) return Mark.New;
        if (Table.Columns.FirstOrDefault(o => o.Name == c.OriginalName) is { } o
            && (o.Name != c.Name || SchemaDiff.ColumnDifferences(c.MetaForCompare, o).Count > 0)) return Mark.Changed;
        return Mark.None;
    }

    public Mark IndexMark(EditableIndex e)
    {
        if (e.OriginalName == null) return Mark.New;
        if (Table.Indexes.FirstOrDefault(o => o.Name == e.OriginalName) is { } o
            && (o.Name != e.Name || o.Unique != e.Unique || !o.Columns.SequenceEqual(e.Columns))) return Mark.Changed;
        return Mark.None;
    }

    public bool CommentChanged => Comment != Table.Comment;

    public EditableColumn? SelectedColumn => Selection is Node.Column c ? Columns.FirstOrDefault(x => x.Id == c.Id) : null;
    public EditableIndex? SelectedIndex => Selection is Node.Index i ? Indexes.FirstOrDefault(x => x.Id == i.Id) : null;
    public bool IsSelectedPrimary => SelectedIndex is { } e && PrimaryIndex is { } p && Indexes[p].Id == e.Id;

    // MARK: - 편집 동작

    public bool IsColumnSelected => Selection is Node.Column;

    public bool CanRemove => Selection switch
    {
        Node.Column => Columns.Count > 1,
        Node.Index => true,
        _ => false,
    };

    public bool CanMove(int by) =>
        Selection is Node.Column c && Columns.FindIndex(x => x.Id == c.Id) is var i and >= 0 && i + by >= 0 && i + by < Columns.Count;

    public void AddColumn()
    {
        var n = 1;
        while (Columns.Any(c => c.Name == "new_column" + (n == 1 ? "" : n.ToString()))) n++;
        var col = new EditableColumn(name: "new_column" + (n == 1 ? "" : n.ToString()));
        if (Selection is Node.Column s && Columns.FindIndex(x => x.Id == s.Id) is var i and >= 0) Columns.Insert(i + 1, col);
        else Columns.Add(col);
        Touch();
        Selection = new Node.Column(col.Id);
    }

    public void AddIndex()
    {
        var cols = new List<string>();
        if (SelectedColumn is { } c) cols.Add(c.Name);
        var idx = new EditableIndex(name: $"idx_{Table.Name}_{cols.FirstOrDefault() ?? ""}", columns: cols);
        Indexes.Add(idx);
        Touch();
        Selection = new Node.Index(idx.Id);
    }

    public void RemoveSelected()
    {
        switch (Selection)
        {
            case Node.Column s:
            {
                var i = Columns.FindIndex(x => x.Id == s.Id);
                if (i < 0) return;
                var name = Columns[i].Name;
                Columns.RemoveAt(i);
                foreach (var idx in Indexes) idx.Columns.RemoveAll(c => c == name);
                Indexes.RemoveAll(x => x.Columns.Count == 0);
                Touch();
                Selection = i < Columns.Count ? new Node.Column(Columns[i].Id) : Columns.Count > 0 ? new Node.Column(Columns[^1].Id) : null;
                break;
            }
            case Node.Index s:
                Indexes.RemoveAll(x => x.Id == s.Id);
                Touch();
                Selection = new Node.Table();
                break;
        }
    }

    public void DuplicateSelected()
    {
        if (Selection is not Node.Column s || Columns.FindIndex(x => x.Id == s.Id) is not (var i and >= 0)) return;
        var o = Columns[i];
        var col = new EditableColumn(name: o.Name + "_copy", columnType: o.ColumnType, nullable: o.Nullable, defaultValue: o.DefaultValue,
                                     extra: new ColumnExtraParts(o.Extra, Kind).AutoIncrement ? "" : o.Extra, comment: o.Comment);
        Columns.Insert(i + 1, col);
        Touch();
        Selection = new Node.Column(col.Id);
    }

    public void MoveSelected(int by)
    {
        if (Selection is not Node.Column s || Columns.FindIndex(x => x.Id == s.Id) is not (var i and >= 0)
            || i + by < 0 || i + by >= Columns.Count) return;
        (Columns[i], Columns[i + by]) = (Columns[i + by], Columns[i]);
        Touch();
    }

    /// 지울 예정인 컬럼 되살리기
    public void Restore(string name)
    {
        if (Table.Columns.FirstOrDefault(c => c.Name == name) is not { } o) return;
        var col = new EditableColumn(o);
        var pos = Table.Columns.OrderBy(c => c.Position).ToList().FindIndex(c => c.Name == name);
        if (pos < 0) pos = Columns.Count;
        Columns.Insert(Math.Min(pos, Columns.Count), col);
        Touch();
        Selection = new Node.Column(col.Id);
    }

    public void SetPK(string name, bool on)
    {
        if (PrimaryIndex is { } p)
        {
            if (on)
            {
                if (!Indexes[p].Columns.Contains(name)) Indexes[p].Columns.Add(name);
            }
            else
            {
                Indexes[p].Columns.RemoveAll(c => c == name);
            }
            if (Indexes[p].Columns.Count == 0) Indexes.RemoveAt(p);
        }
        else if (on)
        {
            var pkName = Kind == DatabaseKind.Mysql ? "PRIMARY" : $"pk_{Table.Name}";
            Indexes.Insert(0, new EditableIndex(name: pkName, unique: true, columns: [name]));
        }
        if (on && Columns.FirstOrDefault(c => c.Name == name) is { } col) col.Nullable = false;
        Touch();
    }

    /// 컬럼 Extra 의 한 부분 고치기 (자동 증가 / ON UPDATE / 나머지)
    public void EditExtra(EditableColumn c, Action<ColumnExtraParts> change)
    {
        var parts = new ColumnExtraParts(c.Extra, Kind);
        change(parts);
        c.Extra = parts.Compose(Kind);
        Touch();
    }

    public IReadOnlyList<string> CommonTypes => DataModel.CommonTypes(Kind);

    // MARK: - 아래 버튼

    /// 실행하지 않고 SQL 만 새 탭에 연다
    public bool OpenInTab()
    {
        if (SqlText is not { } sql) return false;
        App.AddTab(sql, $"ALTER · {Table.Name}");
        App.AlteringTable = null;
        return true;
    }

    /// 확인창에 보일 문장
    public string ConfirmMessage => Tr("%@ / %@ 에서 바로 실행돼요. 컬럼 삭제는 되돌릴 수 없어요.", ConnectionName, Database);

    /// 확인을 받은 뒤 실행. 성공하면 창을 닫는다 (AlteringTable = null). 입력 오류 · 읽기전용이면 아무것도 하지 않는다.
    public async Task<bool> ExecuteAsync()
    {
        if (!CanExecute || SqlText is not { } sql) return false;
        Executing = true;
        ExecError = null;
        try
        {
            await App.ExecuteAlterAsync(sql, Table.Name);
            App.AlteringTable = null;
            return true;
        }
        catch (Exception e)
        {
            ExecError = ErrorText.Describe(e).Message;
            return false;
        }
        finally
        {
            Executing = false;
        }
    }
}
