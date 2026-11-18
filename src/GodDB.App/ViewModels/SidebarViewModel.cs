using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GodDB.App.ViewModels;

/// 사이드바 화면 상태 (Mac SidebarView · ConnectionGroups.ProfileList). AppState 를 보고 다시 만든다.
public sealed partial class SidebarViewModel : ObservableObject
{
    public const string CollapsedGroupsKey = "GodDB.sidebar.collapsedGroups";

    public AppState App { get; }

    /// 그룹 없는 접속 먼저, 그다음 그룹별 (ProfileRowViewModel 또는 GroupRowViewModel)
    public ObservableCollection<object> ConnectionItems { get; } = [];
    public ObservableCollection<TableNodeViewModel> TableNodes { get; } = [];
    public ObservableCollection<RoutineSectionViewModel> RoutineSections { get; } = [];

    [ObservableProperty] private bool _showSchema;
    [ObservableProperty] private bool _showRedis;
    [ObservableProperty] private string? _failureMessage;
    [ObservableProperty] private string _containerTitle = "DB";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _multipleSelected;
    [ObservableProperty] private IReadOnlyList<string> _databases = [];
    [ObservableProperty] private string? _currentDatabase;
    [ObservableProperty] private bool _schemaLoading;

    private bool _syncingDatabase;

    public SidebarViewModel(AppState app)
    {
        App = app;
        app.Profiles.CollectionChanged += (_, _) => RebuildConnections();
        app.Tabs.CollectionChanged += (_, _) => RebuildConnections();
        app.ConnectionChanged += id =>
        {
            RebuildConnections();
            if (id == app.SelectedProfileId) RebuildSchema();
        };
        app.PropertyChanged += OnAppChanged;
        // 언어를 바꾸면 VM 이 만든 문장(개수, 절 제목)도 새 언어로
        L10n.LanguageChanged += () => AppState.RunOnUi(() =>
        {
            RebuildConnections();
            _builtTables = null;
            RebuildSchema();
        });
        RebuildConnections();
        RebuildSchema();
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.SelectedProfileId):
                RebuildConnections();
                RebuildSchema();
                break;
            case nameof(AppState.TableFilter):
                RebuildTables();
                RebuildRoutines();
                break;
            case nameof(AppState.SelectedTables):
                UpdateSelectionText();
                break;
        }
    }

    // MARK: - 접속 목록

    private HashSet<string> Collapsed => ConnectionGroups.DecodeCollapsed(Prefs.GetString(CollapsedGroupsKey) ?? "");

    public void SetExpanded(string group, bool expanded)
    {
        var c = Collapsed;
        if (expanded) c.Remove(group); else c.Add(group);
        Prefs.Set(CollapsedGroupsKey, ConnectionGroups.EncodeCollapsed(c));
    }

    public void RebuildConnections()
    {
        var collapsed = Collapsed;
        ConnectionItems.Clear();
        foreach (var p in ConnectionGroups.Ungrouped(App.Profiles)) ConnectionItems.Add(Row(p));
        foreach (var g in App.ProfileGroups)
        {
            var members = ConnectionGroups.Members(App.Profiles, g).Select(Row).ToList();
            ConnectionItems.Add(new GroupRowViewModel(this, g, members, !collapsed.Contains(g)));
        }
        FailureMessage = App.State.IsFailed ? App.State.Message : null;
        ShowRedis = App.State.IsConnected && App.SelectedRedis != null;
        ShowSchema = App.State.IsConnected && App.SelectedRedis == null;
        OnPropertyChanged(nameof(HasProfiles));
    }

    public bool HasProfiles => App.Profiles.Count > 0;

    private ProfileRowViewModel Row(ConnectionProfile p) =>
        new(p, App.ConnectionState(p.Id), App.Tabs.Count(t => t.ProfileId == p.Id), App.SelectedProfileId == p.Id);

    // MARK: - 스키마 (DB 선택 · 테이블 · 뷰/프로시저/함수/트리거)

    private IReadOnlyList<TableMeta>? _builtTables;
    private string _builtFilter = "";

    public void RebuildSchema()
    {
        ShowRedis = App.State.IsConnected && App.SelectedRedis != null;
        ShowSchema = App.State.IsConnected && App.SelectedRedis == null;
        FailureMessage = App.State.IsFailed ? App.State.Message : null;
        ContainerTitle = App.SelectedProfile?.Kind.ContainerTitle() ?? "DB";
        SchemaLoading = App.SchemaLoading;
        _syncingDatabase = true;
        Databases = App.Databases.ToList();
        CurrentDatabase = App.CurrentDatabase;
        _syncingDatabase = false;
        if (!ReferenceEquals(_builtTables, App.Tables) || _builtFilter != App.TableFilter) RebuildTables();
        RebuildRoutines();
        UpdateSelectionText();
    }

    partial void OnCurrentDatabaseChanged(string? value)
    {
        if (_syncingDatabase || string.IsNullOrEmpty(value) || value == App.CurrentDatabase) return;
        App.Fire(() => App.SelectDatabaseAsync(value));
    }

    private void RebuildTables()
    {
        _builtTables = App.Tables;
        _builtFilter = App.TableFilter;
        TableNodes.Clear();
        var readOnly = App.SelectedProfile?.ReadOnly == true;
        foreach (var t in App.FilteredTables) TableNodes.Add(new TableNodeViewModel(this, t, readOnly));
    }

    private void RebuildRoutines()
    {
        RoutineSections.Clear();
        foreach (var kind in Enum.GetValues<RoutineKind>())
        {
            var items = App.FilteredRoutines(kind);
            if (items.Count > 0)
                RoutineSections.Add(new RoutineSectionViewModel($"{kind.Title()} {items.Count}", [.. items.Select(r => new RoutineRowViewModel(this, r))]));
        }
    }

    private void UpdateSelectionText()
    {
        MultipleSelected = App.SelectedTables.Count > 1;
        CountText = MultipleSelected ? Tr("%@개 선택", App.SelectedTables.Count) : App.Tables.Count.ToString();
        foreach (var n in TableNodes) n.NotifySelection();
    }

    public string DiagramButtonTip => App.SelectedTables.Count > 1
        ? Tr("선택한 테이블 %@개로 ER 다이어그램", App.SelectedTables.Count)
        : Tr("스키마 전체 ER 다이어그램 (Ctrl/Shift 클릭으로 테이블을 여러 개 고르면 그 테이블만)");

    [RelayCommand]
    private void Diagram()
    {
        var id = App.SelectedTables.Count > 1 ? App.MakeDiagram(App.SelectedTables) : App.MakeDiagram((string?)null);
        App.OpenDiagram(id);
    }

    [RelayCommand]
    private void RefreshSchema() => App.Fire(() => App.RefreshSchemaAsync());

    [RelayCommand]
    private void NewProfile() => App.NewProfile();

    [RelayCommand]
    private void ExportConnections() => App.ShowExportConnections = true;

    [RelayCommand]
    private void ImportConnections() => App.Fire(App.ChooseImportFileAsync);

    [RelayCommand]
    private void PasteDataGrip() => App.Fire(() => App.PasteDataGripAsync());

    public Task CopyAsync(string text) => App.Dialogs.SetClipboardTextAsync(text);
}

/// 사이드바 접속 한 줄
public sealed class ProfileRowViewModel(ConnectionProfile profile, ConnectionState state, int tabCount, bool selected)
{
    public ConnectionProfile Profile { get; } = profile;
    public ConnectionState State { get; } = state;
    public int TabCount { get; } = tabCount;
    public bool IsSelected { get; } = selected;
    public string Name => Profile.Name;
    public DatabaseKind Kind => Profile.Kind;
    public ConnectionEnvironment Environment => Profile.Environment;
    public bool ReadOnly => Profile.ReadOnly;
    public bool IsConnecting => State.Kind == ConnectionStateKind.Connecting;
    public bool HasTabs => TabCount > 0;
    public string TabCountText => TabCount.ToString();
    public string TabCountTip => Tr("이 접속의 쿼리 탭 %@개", TabCount);
    /// 연결 상태 점: 초록 = 연결됨, 빨강 = 실패, 회색 = 안 됨
    public Avalonia.Media.IBrush DotBrush => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(State.Kind switch
    {
        ConnectionStateKind.Connected => "#34C759",
        ConnectionStateKind.Disconnected => "#668A8A8E",
        ConnectionStateKind.Connecting => "#00000000",
        _ => "#FF3B30",
    }));
}

/// 사이드바 접속 그룹 (접었다 펴기 상태는 Prefs 에 기억)
public sealed partial class GroupRowViewModel : ObservableObject
{
    private readonly SidebarViewModel _owner;
    public string Name { get; }
    public IReadOnlyList<ProfileRowViewModel> Members { get; }
    [ObservableProperty] private bool _isExpanded;

    public GroupRowViewModel(SidebarViewModel owner, string name, IReadOnlyList<ProfileRowViewModel> members, bool expanded)
    {
        _owner = owner;
        Name = name;
        Members = members;
        _isExpanded = expanded;
    }

    partial void OnIsExpandedChanged(bool value) => _owner.SetExpanded(Name, value);

    public int ConnectedCount => Members.Count(m => m.State.IsConnected);
    public bool HasConnected => ConnectedCount > 0;
    public string ConnectedText => ConnectedCount.ToString();
    public string ConnectedTip => Tr("연결된 접속 %@개", ConnectedCount);
    public string CountText => Members.Count.ToString();
}

/// 테이블 트리 노드 (컬럼 + 인덱스 폴더)
public sealed partial class TableNodeViewModel : ObservableObject
{
    private readonly SidebarViewModel _owner;
    private AppState App => _owner.App;
    public TableMeta Table { get; }
    public string Name => Table.Name;
    public string Tip => Table.Comment.Length == 0 ? Table.Name : Table.Comment;
    public bool CanModify { get; }
    public IReadOnlyList<object> Children { get; }

    public TableNodeViewModel(SidebarViewModel owner, TableMeta table, bool readOnly)
    {
        _owner = owner;
        Table = table;
        CanModify = !readOnly;
        var children = new List<object>(table.Columns.Select(c => new ColumnNodeViewModel(c)));
        if (table.Indexes.Count > 0) children.Add(new IndexFolderViewModel(owner.App, table));
        Children = children;
    }

    private bool UseSelection => App.SelectedTables.Count > 1 && App.SelectedTables.Contains(Name);

    public string DiagramTitle => UseSelection ? Tr("다이어그램 보기 (선택한 %@개)", App.SelectedTables.Count) : Tr("다이어그램 보기");

    internal void NotifySelection() => OnPropertyChanged(nameof(DiagramTitle));

    [RelayCommand] private void OpenQuery() => App.OpenTableQuery(Name);
    [RelayCommand] private void Alter() => App.AlteringTable = Table;
    [RelayCommand] private void Ddl() => App.Fire(() => App.OpenTableDdlAsync(Table));
    [RelayCommand] private void Indexes() => App.OpenIndexes(Name);

    [RelayCommand]
    private void Diagram()
    {
        // 여러 개를 골랐고 그중 하나를 우클릭했으면 → 고른 테이블 전부, 아니면 → 이 테이블 + 바로 이어진 테이블
        var id = UseSelection ? App.MakeDiagram(App.SelectedTables) : App.MakeDiagram(Name);
        App.OpenDiagram(id);
    }

    [RelayCommand] private void CopyName() => App.Fire(() => _owner.CopyAsync(Name));
    [RelayCommand] private void ImportCsv() => App.ImportingTable = Table;
    [RelayCommand] private void Rename() => App.RequestRename(Name);
    [RelayCommand] private void Drop() => App.RequestDrop(Name);
}

public sealed class ColumnNodeViewModel(ColumnMeta column)
{
    public ColumnMeta Column { get; } = column;
    public string Name => Column.Name;
    public string Type => Column.ColumnType;
    public bool IsPrimaryKey => Column.IsPrimaryKey;
    public bool NotNull => !Column.Nullable;
    public string Icon => Column.IsPrimaryKey ? "🔑" : "•";
    public string? Tip => Column.Comment.Length == 0 ? null : Column.Comment;
}

public sealed partial class IndexFolderViewModel(AppState app, TableMeta table)
{
    public string Title => Tr("인덱스 %@", table.Indexes.Count);
    public IReadOnlyList<IndexNodeViewModel> Children { get; } = [.. table.Indexes.Select(i => new IndexNodeViewModel(i))];

    [RelayCommand] private void Details() => app.OpenIndexes(table.Name);
}

public sealed class IndexNodeViewModel(IndexMeta index)
{
    public string Name => index.Name;
    public string ColumnsText => $"({string.Join(", ", index.Columns)})";
    public bool ShowUnique => index.Unique && !index.IsPrimary;
    public string Icon => index.IsPrimary ? "🔑" : "≡";
    public string Tip => $"{(index.Unique ? "UNIQUE " : "")}{index.Name} ({string.Join(", ", index.Columns)})";
}

public sealed class RoutineSectionViewModel(string title, IReadOnlyList<RoutineRowViewModel> items)
{
    public string Title { get; } = title;
    public IReadOnlyList<RoutineRowViewModel> Items { get; } = items;
}

public sealed partial class RoutineRowViewModel(SidebarViewModel owner, RoutineMeta routine)
{
    public RoutineMeta Routine { get; } = routine;
    public string Name => Routine.Name;
    public string Icon => Routine.Kind switch
    {
        RoutineKind.View => "👁",
        RoutineKind.Procedure => "⚙",
        RoutineKind.Function => "ƒ",
        _ => "⚡",
    };
    public string Tip => Routine.Comment.Length == 0 ? Tr("더블클릭하면 정의를 열어요") : Routine.Comment;
    public bool IsView => Routine.Kind == RoutineKind.View;
    public bool IsProcedure => Routine.Kind == RoutineKind.Procedure;

    [RelayCommand] private void OpenDefinition() => owner.App.Fire(() => owner.App.OpenRoutineAsync(Routine));
    [RelayCommand] private void SelectData() => owner.App.OpenTableQuery(Routine.Name);
    [RelayCommand] private void CallTemplate() => owner.App.OpenCallTemplate(Routine);
    [RelayCommand] private void CopyName() => owner.App.Fire(() => owner.CopyAsync(Routine.Name));
}
