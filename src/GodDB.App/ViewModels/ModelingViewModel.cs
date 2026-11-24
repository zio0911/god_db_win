using System.Globalization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GodDB.App.Services;

namespace GodDB.App.ViewModels;

/// 모델 목록의 한 줄 (Mac ModelListView 행)
public sealed partial class ModelRow(DataModel model) : ObservableObject
{
    public Guid Id { get; } = model.Id;
    [ObservableProperty] private string _name = model.Name;
    [ObservableProperty] private DatabaseKind _kind = model.Kind;
    [ObservableProperty] private string _subtitle = Describe(model);
    [ObservableProperty] private string _updatedText = Relative(model.Updated);
    /// 이름 바꾸는 중 (목록 안 입력칸)
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";

    public void Refresh(DataModel m)
    {
        Name = m.Name;
        Kind = m.Kind;
        Subtitle = Describe(m);
        UpdatedText = Relative(m.Updated);
    }

    private static string Describe(DataModel m) => Tr("테이블 %@ · 관계 %@", m.Tables.Count, m.Relations.Count);

    public static string Relative(DateTime updated) => Relative(updated, DateTime.Now);

    /// Mac `updated.formatted(.relative(presentation: .named))` (RelativeDateTimeFormatter) 와 같은 규칙:
    /// 두 시각의 달력 차이(년 · 월 · 주 · 일 · 시 · 분 · 초) 중 0 이 아닌 가장 큰 단위 하나로,
    /// 1일 · 1주 · 1개월 · 1년은 이름(어제 · 지난주 · 지난달 · 작년), 차이가 없으면 '지금'.
    public static string Relative(DateTime date, DateTime now)
    {
        date = date.Kind == DateTimeKind.Utc ? date.ToLocalTime() : date;
        now = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        var past = date <= now;
        var (from, to) = past ? (date, now) : (now, date);
        // 큰 단위부터 넘치지 않을 만큼 더해 간다 (Calendar.dateComponents 와 같게)
        var years = 0;
        while (from.AddYears(years + 1) <= to) years++;
        var cur = from.AddYears(years);
        var months = 0;
        while (cur.AddMonths(months + 1) <= to) months++;
        cur = cur.AddMonths(months);
        var rest = to - cur;
        var weeks = (int)(rest.TotalDays / 7);
        var days = (int)rest.TotalDays - weeks * 7;
        var hours = rest.Hours;
        var minutes = rest.Minutes;
        var seconds = rest.Seconds;

        static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
        if (years > 0) return years == 1 ? Tr(past ? "작년" : "내년") : Tr(past ? "%@년 전" : "%@년 후", N(years));
        if (months > 0) return months == 1 ? Tr(past ? "지난달" : "다음 달") : Tr(past ? "%@개월 전" : "%@개월 후", N(months));
        if (weeks > 0) return weeks == 1 ? Tr(past ? "지난주" : "다음 주") : Tr(past ? "%@주 전" : "%@주 후", N(weeks));
        if (days > 0) return days == 1 ? Tr(past ? "어제" : "내일") : Tr(past ? "%@일 전" : "%@일 후", N(days));
        if (hours > 0) return hours == 1 ? Tr(past ? "1시간 전" : "1시간 후") : Tr(past ? "%@시간 전" : "%@시간 후", N(hours));
        if (minutes > 0) return minutes == 1 ? Tr(past ? "1분 전" : "1분 후") : Tr(past ? "%@분 전" : "%@분 후", N(minutes));
        if (seconds > 0) return seconds == 1 ? Tr(past ? "1초 전" : "1초 후") : Tr(past ? "%@초 전" : "%@초 후", N(seconds));
        return Tr("지금");
    }
}

/// 모델링 화면 상태 (Mac ModelingView · ModelListView · ModelEditorView · ModelCanvas 의 @State 와 동작).
/// 모델 자체(목록·자동 저장·되돌리기)는 Core ModelWorkspace(AppState.Modeling)에 있다.
/// DataModel 은 클래스라서 편집기는 모델을 바로 고친 뒤 <see cref="Changed"/> 를 불러 자동 저장·다시 그리기를 한다
/// (Mac 은 값 타입 바인딩의 set 에서 scheduleSave). 되돌리기는 모델 객체를 바꾸므로 항상 <see cref="Model"/> 로 다시 찾는다.
public sealed partial class ModelingViewModel : ObservableObject
{
    public const string NameModeKey = "GodDB.model.nameMode";

    public AppState App { get; }
    public ModelWorkspace Workspace => App.Modeling;
    public ObservableCollection<ModelRow> Rows { get; } = [];

    [ObservableProperty] private ModelRow? _selectedRow;
    [ObservableProperty] private ModelTool _tool = ModelTool.SelectTool;
    [ObservableProperty] private Guid? _pendingParent;
    [ObservableProperty] private ModelSelection? _selection;
    [ObservableProperty] private double _zoom = 1;
    [ObservableProperty] private ModelNameMode _nameMode = LoadNameMode();
    /// 아래 테이블 편집기의 탭 (컬럼 / 인덱스 / 관계 / 설명)
    [ObservableProperty] private int _editorTab;
    /// 관계 편집기의 "FK 컬럼도 삭제"
    [ObservableProperty] private bool _dropColumns = true;
    /// 테이블 명세서 저장 창을 띄울 모델 (Mac ModelingState.tableSpecModel — 화면이 보고 창을 연다)
    [ObservableProperty] private DataModel? _tableSpecModel;

    /// 다이어그램을 다시 그려야 할 때 (모든 변경)
    public event Action? Redraw;
    /// 자동 정렬 직전 · 직후 (다이어그램이 카드를 움직이는 애니메이션용)
    public event Action? AutoLayoutStarting, AutoLayoutFinished;
    /// 아래 편집기를 다시 만들어야 할 때 (고른 것 · 구조가 바뀜)
    public event Action? EditorRebuild;

    private readonly Dictionary<Guid, (double X, double Y)> _dragStart = [];
    private bool _syncingRow;

    public ModelingViewModel(AppState app)
    {
        App = app;
        Workspace.LoadIfNeeded();
        ReloadRows();
        // 리버스 엔지니어링 창이 모델을 넣거나(창이 닫힘) 다른 모드에서 돌아오면 목록을 다시 읽는다
        App.PropertyChanged += OnAppChanged;
        // AI 어시스턴트가 물리명을 적용하면 바로 다시 그린다
        App.ModelEditedExternally += _ => Refresh();
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.ShowReverseEngineer) when !App.ShowReverseEngineer:
            case nameof(AppState.Mode) when App.Mode == MainMode.Modeling:
                Refresh();
                break;
            case nameof(AppState.State):
            case nameof(AppState.SelectedProfileId):
            case nameof(AppState.Tables):
                OnPropertyChanged(nameof(CanReverseEngineer));
                OnPropertyChanged(nameof(CanCompare));
                OnPropertyChanged(nameof(IsConnected));
                break;
        }
    }

    // MARK: - 지금 모델

    public DataModel? Model => Workspace.Selected;
    public Guid? ModelId => Workspace.SelectedId;
    public bool HasModel => Model != null;
    public bool CanReverseEngineer => App.CanReverseEngineer;
    /// DB와 비교: 연결된 SQL 접속이 있어야 한다
    public bool CanCompare => App.State.IsConnected && App.SelectedProfile?.Kind.IsSql() == true;
    public bool IsConnected => App.State.IsConnected;
    public bool CanUndo => ModelId is { } id && Workspace.CanUndo(id);
    /// 자동 정렬: 테이블이 있어야 한다 (Mac .disabled(model.tables.isEmpty))
    public bool CanAutoLayout => Model?.Tables.Count > 0;

    public IReadOnlyList<ModelIssue> Issues => Model?.Issues() ?? [];
    public bool HasErrors => Issues.Any(i => i.Level == ModelIssueLevel.Error);
    public string IssueBadge => ModelDiagram.IssueBadge(Issues);
    public string? ToolHint => Model is { } m ? ModelDiagram.ToolHint(m, Tool, PendingParent) : null;
    public string ZoomText => $"{(int)Math.Round(Zoom * 100)}%";

    /// 모델 목록을 저장소(Workspace)에서 다시 읽는다
    public void Refresh()
    {
        ReloadRows();
        NotifyAll(structure: true);
    }

    private void ReloadRows()
    {
        _syncingRow = true;
        Rows.Clear();
        foreach (var m in Workspace.Models) Rows.Add(new ModelRow(m));
        SelectedRow = Rows.FirstOrDefault(r => r.Id == Workspace.SelectedId);
        _syncingRow = false;
    }

    partial void OnSelectedRowChanged(ModelRow? value)
    {
        if (_syncingRow || value == null || value.Id == Workspace.SelectedId) return;
        SelectModel(value.Id);
    }

    /// 다른 모델을 고르면 편집 상태를 처음으로 (Mac: ModelEditorView 에 .id(id))
    public void SelectModel(Guid id)
    {
        Workspace.SelectedId = id;
        ResetEditorState();
        _syncingRow = true;
        SelectedRow = Rows.FirstOrDefault(r => r.Id == id);
        _syncingRow = false;
        NotifyAll(structure: true);
    }

    private void ResetEditorState()
    {
        Tool = ModelTool.SelectTool;
        PendingParent = null;
        Selection = null;
        Zoom = 1;
        EditorTab = 0;
        _dragStart.Clear();
    }

    partial void OnToolChanged(ModelTool value)
    {
        PendingParent = null;
        OnPropertyChanged(nameof(ToolHint));
        Redraw?.Invoke();
    }

    partial void OnPendingParentChanged(Guid? value)
    {
        OnPropertyChanged(nameof(ToolHint));
        Redraw?.Invoke();
    }

    partial void OnSelectionChanged(ModelSelection? value)
    {
        Redraw?.Invoke();
        EditorRebuild?.Invoke();
    }

    partial void OnZoomChanged(double value) => OnPropertyChanged(nameof(ZoomText));

    partial void OnNameModeChanged(ModelNameMode value)
    {
        Prefs.Set(NameModeKey, NameModeRaw(value));
        Redraw?.Invoke();
    }

    /// Mac DiagramNameMode rawValue ("logical" / "physical" / "both")
    public static string NameModeRaw(ModelNameMode m) => m switch
    {
        ModelNameMode.Physical => "physical",
        ModelNameMode.Both => "both",
        _ => "logical",
    };

    public static ModelNameMode LoadNameMode() => Prefs.GetString(NameModeKey) switch
    {
        "physical" => ModelNameMode.Physical,
        "both" => ModelNameMode.Both,
        _ => ModelNameMode.Logical,
    };

    public static string NameModeTitle(ModelNameMode m) => m switch
    {
        ModelNameMode.Physical => Tr("물리명"),
        ModelNameMode.Both => Tr("둘 다"),
        _ => Tr("논리명"),
    };

    /// 편집기가 모델을 고친 뒤: 자동 저장 예약 + 다시 그리기 (structure 면 편집기도 다시 만든다)
    public void Changed(bool structure = false)
    {
        if (ModelId is not { } id) return;
        Workspace.ScheduleSave(id);
        NotifyAll(structure);
    }

    /// 구조를 바꾸기 전 되돌리기 기록
    public void Checkpoint()
    {
        if (ModelId is { } id) Workspace.Checkpoint(id);
    }

    private void NotifyAll(bool structure)
    {
        if (Model is { } m) Rows.FirstOrDefault(r => r.Id == m.Id)?.Refresh(m);
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(HasModel));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanAutoLayout));
        OnPropertyChanged(nameof(Issues));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(IssueBadge));
        OnPropertyChanged(nameof(ToolHint));
        OnPropertyChanged(nameof(CanReverseEngineer));
        OnPropertyChanged(nameof(CanCompare));
        OnPropertyChanged(nameof(IsConnected));
        Redraw?.Invoke();
        if (structure) EditorRebuild?.Invoke();
    }

    // MARK: - 모델 목록

    public void NewModel()
    {
        Workspace.Create(App.SelectedProfile?.Kind ?? DatabaseKind.Mysql);
        AfterAdd();
    }

    private void AfterAdd()
    {
        ResetEditorState();
        ReloadRows();
        NotifyAll(structure: true);
    }

    public void Duplicate(Guid id)
    {
        if (Workspace.Duplicate(id) != null) AfterAdd();
    }

    public void StartRename(ModelRow row)
    {
        foreach (var r in Rows) r.IsRenaming = false;
        row.RenameText = row.Name;
        row.IsRenaming = true;
    }

    public void CommitRename(ModelRow row)
    {
        if (!row.IsRenaming) return;
        row.IsRenaming = false;
        Workspace.Rename(row.Id, row.RenameText);
        if (Workspace.Find(row.Id) is { } m) row.Refresh(m);
        NotifyAll(structure: false);
    }

    public void CancelRename(ModelRow row) => row.IsRenaming = false;

    public async Task DeleteAsync(Guid id)
    {
        if (Workspace.Find(id) is not { } m) return;
        var ok = await App.Dialogs.ConfirmAsync(Tr("이 모델을 삭제할까요?"),
            Tr("%@ 모델 파일을 지워요. 되돌릴 수 없어요. (필요하면 먼저 파일로 내보내 두세요)", m.Name), Tr("삭제"), Tr("취소"), destructive: true);
        if (!ok) return;
        Workspace.Delete(id);
        ResetEditorState();
        ReloadRows();
        NotifyAll(structure: true);
    }

    public static readonly FileFilter ModelFileFilter = new(Tr("GodDB 모델"), [$"*.{DataModel.FileExtension}"]);
    public static readonly FileFilter JsonFileFilter = new("JSON", ["*.json"]);
    /// 모델 파일 열기 창의 첫 형식: 모델 파일과 JSON 을 한 번에 고를 수 있게 (Mac: allowedContentTypes = [모델 파일, .json]).
    /// Windows 파일 창은 형식을 하나씩만 보여 줘서, 형식이 따로면 JSON 으로 저장한 모델은 형식을 바꿔야 보였다
    public static readonly FileFilter OpenModelFilter = new(Tr("GodDB 모델"), [$"*.{DataModel.FileExtension}", "*.json"]);

    /// 내보내기 > 모델링 파일 저장하기…
    public async Task ExportAsync(Guid id)
    {
        if (Workspace.Find(id) is not { } m) return;
        var path = await App.Dialogs.SaveFileAsync(Tr("모델링 파일 저장하기…"), ModelWorkspace.ExportFileName(m), [ModelFileFilter]);
        if (path == null) return;
        try { Workspace.ExportFile(id, path); }
        catch (Exception e) { await App.Dialogs.AlertAsync(Tr("모델링 파일 저장하기…"), e.Message); }
    }

    /// 내보내기 > 테이블 명세서로 저장하기…: 시스템명 · 작성자 · 작성일을 묻는 창부터 (Mac exportTableSpec)
    public void ExportTableSpec(Guid id) => TableSpecModel = Workspace.Find(id)?.Clone();

    public async Task ImportAsync()
    {
        var files = await App.Dialogs.OpenFilesAsync(Tr("모델 파일 열기…"), [OpenModelFilter, ModelFileFilter, JsonFileFilter], multiple: true);
        if (files.Count == 0) return;
        var failed = Workspace.ImportFiles(files);
        AfterAdd();
        foreach (var (path, error) in failed) await App.Dialogs.AlertAsync(Path.GetFileName(path), error);
    }

    public void ReverseEngineer() => App.ShowReverseEngineer = true;

    // MARK: - 도구 막대

    public void SetModelName(string name)
    {
        if (Model is not { } m || m.Name == name) return;
        m.Name = name;
        Changed();
    }

    public void SetKind(DatabaseKind kind)
    {
        if (Model is not { } m || m.Kind == kind) return;
        m.Kind = kind;
        Changed(structure: true);
    }

    public void PressTool(ModelTool pressed) => Tool = ModelTool.Toggle(Tool, pressed);

    public void Undo()
    {
        if (ModelId is not { } id) return;
        Workspace.UndoLast(id);
        // 되돌린 모델에 없는 것을 고르고 있으면 닫는다
        if (Selection is ModelSelection.Table t && Model?.Table(t.Id) == null) Selection = null;
        if (Selection is ModelSelection.Relation r && Model?.Relations.Any(x => x.Id == r.Id) != true) Selection = null;
        NotifyAll(structure: true);
    }

    public void AutoLayout()
    {
        if (Model is not { } m) return;
        Checkpoint();
        // Mac withAnimation(.easeInOut(duration: 0.25)) { autoLayout() } — 화면이 지금 자리를 기억했다가 움직인다
        AutoLayoutStarting?.Invoke();
        ModelDiagram.AutoLayout(m);
        AutoLayoutFinished?.Invoke();
        Changed();
    }

    public void ZoomIn() => Zoom = Math.Min(ModelDiagram.MaxZoom, Math.Round((Zoom + ModelDiagram.ZoomStep) * 10) / 10);
    public void ZoomOut() => Zoom = Math.Max(ModelDiagram.MinZoom, Math.Round((Zoom - ModelDiagram.ZoomStep) * 10) / 10);

    public void SetCardColor(Guid tableId, string color)
    {
        if (Model?.Table(tableId) is not { } t) return;
        t.Color = color == "blue" ? null : color;
        Changed();
    }

    // MARK: - 캔버스

    /// 빈 곳을 눌렀을 때: 테이블 추가 도구면 테이블을 만들고, 아니면 선택 해제
    public void TapBackground(double x, double y)
    {
        if (Model is not { } m) return;
        if (Tool is ModelTool.AddTable)
        {
            Checkpoint();
            var id = m.AddTable(at: ModelDiagram.AddTablePosition(x, y));
            Tool = ModelTool.SelectTool;
            Changed(structure: true);
            Selection = new ModelSelection.Table(id);
        }
        else
        {
            Selection = null;
            PendingParent = null;
        }
    }

    /// 카드를 눌렀을 때 (Mac ModelCanvas.tap): 관계 도구면 부모 → 자식 순서로 눌러 관계를 만든다
    public void TapTable(Guid id)
    {
        if (Model is not { } m) return;
        switch (Tool)
        {
            case ModelTool.Relation rel:
                if (PendingParent is { } parent)
                {
                    Checkpoint();
                    var r = m.AddRelation(parent, id, rel.Cardinality, rel.Identifying);
                    if (r == null) App.Log(LogLevel.Warning, LogSource.Schema, Tr("부모 테이블에 컬럼이 없어서 관계를 만들 수 없어요."));
                    Tool = ModelTool.SelectTool;   // PendingParent 도 지워진다
                    Changed(structure: true);
                    if (r is { } rid) Selection = new ModelSelection.Relation(rid);
                }
                else
                {
                    PendingParent = id;
                }
                break;
            default:
                Selection = new ModelSelection.Table(id);
                Tool = ModelTool.SelectTool;
                break;
        }
    }

    public void SelectRelation(Guid id)
    {
        Selection = new ModelSelection.Relation(id);
        Tool = ModelTool.SelectTool;
    }

    /// 카드 끌기 (선택 도구일 때만). 처음 움직일 때 되돌리기 기록.
    public void DragTable(Guid id, double dx, double dy)
    {
        if (Tool is not ModelTool.Select || Model?.Table(id) is not { } t) return;
        if (!_dragStart.TryGetValue(id, out var start))
        {
            Checkpoint();
            start = (t.X, t.Y);
            _dragStart[id] = start;
        }
        // 되돌리기 기록을 남기면서 모델 객체는 그대로이므로 다시 찾을 필요 없음
        (t.X, t.Y) = ModelDiagram.DragPosition(start, dx, dy);
        Redraw?.Invoke();
    }

    public void EndDrag(Guid id)
    {
        if (_dragStart.Remove(id)) Changed();
    }

    /// Esc: 선택 도구로
    public void Escape()
    {
        Tool = ModelTool.SelectTool;
        PendingParent = null;
    }

    /// Delete 키 / 편집기의 삭제 버튼 (확인 뒤 지움, 되돌리기 가능)
    public async Task DeleteSelectionAsync()
    {
        if (Selection is not { } s) return;
        var ok = await App.Dialogs.ConfirmAsync(Tr("삭제할까요?"), Tr("되돌리기 버튼으로 다시 살릴 수 있어요."), Tr("삭제"), Tr("취소"), destructive: true);
        if (ok) Delete(s);
    }

    public void Delete(ModelSelection s)
    {
        if (Model is not { } m) return;
        Checkpoint();
        // 편집기가 지울 대상을 더 그리지 않게 선택부터 푼다 (Mac 1.4.3: 테이블을 지우면 아래 편집기가 옛 번호를 읽다가 앱이 꺼지던 문제)
        Selection = null;
        switch (s)
        {
            case ModelSelection.Table t: m.RemoveTable(t.Id); break;
            case ModelSelection.Relation r: m.RemoveRelation(r.Id, dropColumns: true); break;
        }
        Selection = null;
        Changed(structure: true);
    }

    /// 관계 편집기의 "관계 삭제" (FK 컬럼도 삭제 선택에 따라)
    public void DeleteRelation(Guid id)
    {
        if (Model is not { } m) return;
        Checkpoint();
        Selection = null;
        m.RemoveRelation(id, DropColumns);
        Changed(structure: true);
    }

    /// 관계 속성 바꾸기 (Mac: ModelRelationEditor 의 set → updateRelation)
    public void UpdateRelation(Guid id, Action<ModelRelation> change, bool rebuild = true)
    {
        if (Model is not { } m || m.Relations.FirstOrDefault(r => r.Id == id) is not { } r) return;
        var x = r.Clone();
        change(x);
        m.UpdateRelation(x);
        Changed(structure: rebuild);
    }

    // MARK: - 컬럼 · 인덱스 (테이블 편집기)

    public void AddColumn(Guid tableId)
    {
        if (Model?.Table(tableId) is not { } t) return;
        t.Columns.Add(new ModelColumn(logicalName: Tr("새 컬럼")));
        Changed(structure: true);
    }

    public void RemoveColumn(Guid tableId, Guid columnId)
    {
        if (Model is not { } m || m.TableIndex(tableId) is not int i) return;
        Checkpoint();
        m.RemoveColumn(i, columnId);
        Changed(structure: true);
    }

    public void MoveColumn(Guid tableId, int index, int by)
    {
        if (Model is not { } m || m.TableIndex(tableId) is not int i) return;
        m.MoveColumn(i, index, by);
        Changed(structure: true);
    }

    public void AddIndex(Guid tableId)
    {
        if (Model?.Table(tableId) is not { } t) return;
        t.Indexes.Add(new ModelIndex(name: $"idx_{t.PhysicalName}_"));
        Changed(structure: true);
    }

    public void RemoveIndex(Guid tableId, Guid indexId)
    {
        if (Model?.Table(tableId) is not { } t) return;
        t.Indexes.RemoveAll(x => x.Id == indexId);
        Changed(structure: true);
    }

    /// 인덱스 컬럼 고르기: 이미 있으면 빼고, 없으면 끝에 (고른 순서)
    public void ToggleIndexColumn(Guid tableId, Guid indexId, Guid columnId)
    {
        if (Model?.Table(tableId)?.Indexes.FirstOrDefault(x => x.Id == indexId) is not { } idx) return;
        if (!idx.Columns.Remove(columnId)) idx.Columns.Add(columnId);
        Changed(structure: true);
    }

    // MARK: - SQL 추출 · 비교

    /// 쿼리 탭으로 열기 (실행은 하지 않는다)
    public bool OpenInQueryTab(string sql, string title)
    {
        if (App.AddTab(sql, title) == null) return false;
        App.Mode = MainMode.Query;
        return true;
    }

    public async Task SavePngAsync(byte[] png, DataModel? model = null)
    {
        if ((model ?? Model) is not { } m) return;
        var path = await App.Dialogs.SaveFileAsync(Tr("다이어그램 PNG 저장하기…"), $"{m.Name}.png", [new FileFilter("PNG", ["*.png"])]);
        if (path == null) return;
        try { await File.WriteAllBytesAsync(path, png); }
        catch (Exception e) { await App.Dialogs.AlertAsync(Tr("다이어그램 PNG 저장하기…"), e.Message); }
    }
}
