using GodDB.App.Services;

namespace GodDB.App.ViewModels;

/// 사이드바 접속 목록 정렬 · 찾기 설정 키 (Mac ConnectionListKeys)
public static class ConnectionListKeys
{
    public const string Sort = "GodDB.sidebar.connectionSort";
    public const string ConnectedFirst = "GodDB.sidebar.connectedFirst";
    /// 그룹을 끌 때 실어 보내는 글자의 앞부분 (접속은 UUID 를 싣는다)
    public const string GroupDragPrefix = "goddb-group:";
}

// 접속 목록 정렬 · 순서 바꾸기 · 마지막 연결 시각 · DBeaver 가져오기 (Mac ConnectionGroups.swift · ConnectionTransferUI.swift)
public sealed partial class AppState
{
    /// 정렬이나 "연결된 접속 먼저" 가 바뀌면 (사이드바가 다시 그린다)
    public event Action? ConnectionListOrderChanged;

    /// 사이드바 접속 목록 정렬
    public ConnectionSort ConnectionSort
    {
        get => ConnectionSorts.FromRawValue(Prefs.GetString(ConnectionListKeys.Sort));
        set
        {
            if (value == ConnectionSort) return;
            Prefs.Set(ConnectionListKeys.Sort, value.RawValue());
            OnPropertyChanged();
            ConnectionListOrderChanged?.Invoke();
        }
    }

    /// 연결된 접속을 먼저
    public bool ConnectedFirst
    {
        get => Prefs.Get(ConnectionListKeys.ConnectedFirst, false);
        set
        {
            if (value == ConnectedFirst) return;
            Prefs.Set(ConnectionListKeys.ConnectedFirst, value);
            OnPropertyChanged();
            ConnectionListOrderChanged?.Invoke();
        }
    }

    /// 연결 성공 시각 기록 (사이드바 "최근 연결순"). redshift 가 있으면 PostgreSQL 접속의 Redshift 표시도 맞춘다
    public void NoteConnected(Guid id, bool? redshift = null)
    {
        var i = IndexOfProfile(id);
        if (i < 0) return;
        var p = Profiles[i];
        Profiles[i] = p with { LastConnectedAt = DateTimeOffset.Now, Redshift = redshift ?? p.Redshift };
        Persist();
        if (id == SelectedProfileId) OnPropertyChanged(nameof(SelectedProfile));
    }

    /// 접속 순서 직접 바꾸기 (우클릭 > 순서 바꾸기)
    public void ReorderProfile(Guid id, ConnectionListOrder.Move move)
    {
        FreezeVisibleOrder();
        ApplyOrder(ConnectionListOrder.Moved(Profiles, id, move));
    }

    /// 끌어다 놓기: 놓은 접속 바로 앞으로, 그 접속의 그룹으로
    public void ReorderProfile(Guid id, Guid before)
    {
        FreezeVisibleOrder();
        ApplyOrder(ConnectionListOrder.Moved(Profiles, id, before));
    }

    /// 그룹 순서 바꾸기 (그룹 우클릭 > 순서 바꾸기)
    public void ReorderGroup(string group, ConnectionListOrder.Move move)
    {
        FreezeVisibleOrder();
        ApplyOrder(ConnectionListOrder.MovedGroup(Profiles, group, move));
    }

    /// 그룹 끌어다 놓기: 놓은 그룹 바로 앞으로
    public void ReorderGroup(string group, string before)
    {
        FreezeVisibleOrder();
        ApplyOrder(ConnectionListOrder.MovedGroup(Profiles, group, before));
    }

    /// 다른 정렬로 보고 있으면, 지금 보이는 순서를 저장 순서로 굳히고 "직접 정한 순" 으로 바꾼다
    /// (보이는 그대로에서 옮겨야 헷갈리지 않는다)
    private void FreezeVisibleOrder()
    {
        var sort = ConnectionSort;
        var connectedFirst = ConnectedFirst;
        if (sort == ConnectionSort.Manual && !connectedFirst) return;
        ApplyOrder(ConnectionListOrder.VisibleOrder(Profiles, sort, connectedFirst, id => ConnectionState(id).IsConnected), persist: false);
        Prefs.Set(ConnectionListKeys.Sort, ConnectionSort.Manual.RawValue());
        Prefs.Set(ConnectionListKeys.ConnectedFirst, false);
        OnPropertyChanged(nameof(ConnectionSort));
        OnPropertyChanged(nameof(ConnectedFirst));
        ConnectionListOrderChanged?.Invoke();
    }

    /// 새 순서를 접속 목록에 넣는다 (바뀐 칸만). 순서 · 그룹이 그대로면 아무것도 하지 않는다
    private void ApplyOrder(List<ConnectionProfile> next, bool persist = true)
    {
        if (next.Count != Profiles.Count) return;
        var changed = false;
        for (var i = 0; i < next.Count; i++)
        {
            if (ReferenceEquals(Profiles[i], next[i])) continue;
            Profiles[i] = next[i];
            changed = true;
        }
        if (changed && persist) Persist();
    }

    // MARK: - DBeaver 가져오기

    private static readonly FileFilter ImportFilter = new(Tr("접속 정보 파일"), ["*.json", "*.dbp"]);

    /// DBeaver 파일을 골라 가져오기 창을 연다. DBeaver 작업 폴더(%APPDATA%\DBeaverData\workspace6)가 있으면 거기서 시작한다
    public async Task ChooseDBeaverFileAsync()
    {
        var workspace = DBeaverImport.DefaultWorkspaceFolder;
        var files = await Dialogs.OpenFilesAsync(Tr("DBeaver 에서 내보낸 프로젝트 파일(.dbp) 또는 작업 폴더의 data-sources.json 을 고르세요."),
            [ImportFilter], startFolder: Directory.Exists(workspace) ? workspace : null);
        if (files.Count == 0) return;
        await ImportFileAsync(files[0]);
    }

    private static ImportRequest DBeaverRequest(string path, DBeaverImport.Result result)
    {
        var bundle = new ConnectionBundle
        {
            ExportedAt = DateTimeOffset.Now, App = "DBeaver", Profiles = [.. result.Sources.Select(s => s.Profile)],
        };
        var notes = new Dictionary<Guid, IReadOnlyList<string>>();
        var passwords = new Dictionary<Guid, string>();
        foreach (var s in result.Sources)
        {
            notes.TryAdd(s.Profile.Id, s.Notes);
            if (s.Password is { } pw) passwords.TryAdd(s.Profile.Id, pw);
        }
        return new ImportRequest(Path.GetFileName(path), bundle) { Notes = notes, Skipped = result.Skipped, PlainPasswords = passwords };
    }
}
