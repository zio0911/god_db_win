using GodDB.App.Services;

namespace GodDB.App.ViewModels;

// 접속 관리 · 비밀번호 · 연결 (여러 접속 동시) · DB 선택 · 접속 그룹 · 내보내기/가져오기 · Redis 연결
public sealed partial class AppState
{
    // MARK: - 접속 관리

    public void NewProfile()
    {
        EditingIsNew = true;
        EditingProfile = new ConnectionProfile();
    }

    public void Edit(ConnectionProfile profile)
    {
        EditingIsNew = false;
        EditingProfile = profile;
    }

    /// password: null 이면 기존 저장값 유지
    public void SaveProfile(ConnectionProfile profile, string? password)
    {
        var i = IndexOfProfile(profile.Id);
        if (i >= 0) Profiles[i] = profile;
        else Profiles.Add(profile);
        if (profile.SavePassword)
        {
            if (password != null)
            {
                if (!Secrets.SetPassword(profile.Id, password))
                    Log(LogLevel.Warning, LogSource.App, Tr("%@ 비밀번호를 자격 증명 관리자에 저장하지 못했어요. 이번 실행 중에만 기억해요.", profile.Name));
                SessionPasswords[profile.Id] = password;
            }
        }
        else
        {
            Secrets.DeletePassword(profile.Id);
            if (!string.IsNullOrEmpty(password)) SessionPasswords[profile.Id] = password;
        }
        Persist();
        EditingProfile = null;
        Log(LogLevel.Info, LogSource.App, Tr("%@ 접속 정보 저장", profile.Name)
                + (profile.SavePassword ? Tr(" (비밀번호 자격 증명 관리자 저장)") : Tr(" (비밀번호 저장 안 함)")),
            audit: new AuditRecord(AuditAction.Settings, profile.Name, "접속 정보 저장"));
        // 열려 있던 접속이면 바뀐 설정으로 다시 연결
        if (ConnectionState(profile.Id).IsConnected) RequestReconnect(profile.Id);
        if (profile.Id == SelectedProfileId) OnPropertyChanged(nameof(SelectedProfile));
    }

    private int IndexOfProfile(Guid id)
    {
        for (var i = 0; i < Profiles.Count; i++) if (Profiles[i].Id == id) return i;
        return -1;
    }

    /// 다시 연결. 열린 트랜잭션이 있으면 커밋/롤백을 먼저 묻고, 정리된 뒤 다시 연결한다.
    public void RequestReconnect(Guid id)
    {
        if (Connections.TryGetValue(id, out var c) && c.Tx.Open)
            TxPrompt = new TxPrompt(id, TxPromptReason.Reconnect);
        else
            Fire(() => ReconnectAsync(id));
    }

    /// 끊고 다시 연결한다 (바뀐 접속 설정 적용)
    public async Task ReconnectAsync(Guid id)
    {
        await DisconnectAsync(id, force: true);
        await ConnectAsync(id);
    }

    /// 접속 삭제 (확인은 화면에서 받는다). 열린 트랜잭션이 있으면 먼저 커밋/롤백을 묻는다.
    public async Task DeleteProfileAsync(ConnectionProfile profile)
    {
        if (Connections.TryGetValue(profile.Id, out var c) && c.Tx.Open)
        {
            TxPrompt = new TxPrompt(profile.Id, TxPromptReason.Delete);
            return;
        }
        await DisconnectAsync(profile.Id, force: true);
        var i = IndexOfProfile(profile.Id);
        if (i >= 0) Profiles.RemoveAt(i);
        Secrets.DeletePassword(profile.Id);
        SessionPasswords.Remove(profile.Id);
        foreach (var t in Tabs.Where(t => t.ProfileId == profile.Id).ToList()) Tabs.Remove(t);
        if (!Tabs.Any(t => t.Id == SelectedTabId)) SelectedTabId = Tabs.LastOrDefault()?.Id;
        Persist();
        if (SelectedProfileId == profile.Id) SelectedProfileId = null;
    }

    public void Duplicate(ConnectionProfile profile)
    {
        var copy = profile with { Id = Guid.NewGuid(), Name = profile.Name + Tr(" 복사본") };
        if (Secrets.GetPassword(profile.Id) is { } pw) Secrets.SetPassword(copy.Id, pw);
        Profiles.Add(copy);
        Persist();
    }

    public void Persist()
    {
        try { _store.Save(Profiles); }
        catch (Exception e) { LogError(e, LogSource.App, Tr("접속 목록 저장 실패")); }
    }

    /// 자격 증명 관리자 또는 이번 실행 중 입력한 비밀번호
    public string? Password(ConnectionProfile profile)
    {
        if (SessionPasswords.TryGetValue(profile.Id, out var pw)) return pw;
        if (AppPaths.UiTestPassword is { } test) return test;   // 화면 자동 테스트: 로컬 Docker 테스트 DB
        // Windows 인증(SQL Server)은 비밀번호가 필요 없다
        if (!profile.NeedsPassword) return "";
        return profile.SavePassword ? Secrets.GetPassword(profile.Id) : null;
    }

    /// 비밀번호 입력창에서 확인을 눌렀을 때
    public void SubmitPassword(string password, bool save, ConnectionProfile profile)
    {
        PasswordPrompt = null;
        var p = profile with { SavePassword = save };
        var i = IndexOfProfile(p.Id);
        if (i >= 0) Profiles[i] = p;
        if (save)
        {
            if (!Secrets.SetPassword(p.Id, password))
                Log(LogLevel.Warning, LogSource.App, Tr("%@ 비밀번호를 자격 증명 관리자에 저장하지 못했어요. 이번 실행 중에만 기억해요.", p.Name));
        }
        else
        {
            Secrets.DeletePassword(p.Id);
        }
        // 이번 실행에서는 입력한 값을 그대로 쓴다 (저장/읽기가 실패해도 연결되도록)
        SessionPasswords[p.Id] = password;
        Persist();
        Fire(() => ConnectAsync(p.Id));
    }

    // MARK: - 연결 (여러 접속 동시)

    /// 사이드바에서 접속을 눌렀을 때: 이미 연결돼 있으면 보기만 바꾸고, 아니면 연결한다. 다른 접속은 끊지 않는다.
    public async Task OpenAsync(Guid id)
    {
        SelectedProfileId = id;
        switch (ConnectionState(id).Kind)
        {
            case ConnectionStateKind.Connected:
                // 이 접속의 탭이 선택돼 있지 않으면 마지막 탭으로 이동
                if (SelectedTab?.ProfileId != id && Tabs.LastOrDefault(t => t.ProfileId == id) is { } tab)
                    SelectTab(tab.Id);
                break;
            case ConnectionStateKind.Connecting:
                break;
            default:
                await ConnectAsync(id);
                break;
        }
    }

    /// 사이드바에서 접속을 더블클릭했을 때: 연결한 뒤 이 접속의 쿼리 탭으로 가고, 탭이 없으면 새로 연다.
    public async Task OpenQueryTabAsync(Guid id)
    {
        await OpenAsync(id);
        // 한 번 클릭이 먼저 연결을 시작했으면 끝날 때까지 기다린다 (최대 60초)
        for (var n = 0; n < 600 && ConnectionState(id).Kind == ConnectionStateKind.Connecting; n++)
            await Task.Delay(100);
        if (!ConnectionState(id).IsConnected || Profile(id)?.Kind == DatabaseKind.Redis) return;
        if (SelectedTab?.ProfileId == id) Mode = MainMode.Query;
        else if (Tabs.LastOrDefault(t => t.ProfileId == id) is { } tab) SelectTab(tab.Id);
        else AddTab(profileId: id);
    }

    public async Task ConnectAsync(Guid id)
    {
        if (Profile(id) is not { } profile) return;
        SelectedProfileId = id;
        if (ConnectionState(id).IsConnected) return;
        var password = Password(profile);
        if (password == null)
        {
            // 저장 안 함 설정이거나, 저장한다고 했지만 보관소에 없거나 읽지 못한 경우 → 비밀번호 없이 연결하지 않고 묻는다
            PasswordPrompt = new PasswordPrompt(profile, profile.SavePassword
                ? Tr("자격 증명 관리자에 저장된 비밀번호가 없어요 (또는 읽지 못했어요). 비밀번호를 입력해 주세요. 비밀번호가 없는 DB 면 비워 두고 연결하세요.")
                : null);
            return;
        }
        UpdateConnection(id, c => c.State = ViewModels.ConnectionState.Connecting);
        var target = profile.Ssh is { } ssh
            ? Tr("%@:%@ (SSH %@ 경유)", profile.Host, profile.Port, ssh.Host)
            : $"{profile.Host}:{profile.Port}";
        // 이 PC 에서 나가는 IP (감사 로그용). DNS 조회가 있을 수 있어 백그라운드에서 구한다.
        var note = await Task.Run(() => ClientInfo.ConnectionNote(profile.Host, profile.Port, profile.Ssh));
        Log(LogLevel.Info, LogSource.Connection, Tr("%@ 연결 중… %@", profile.Name, target), detail: note + "\nuser: " + ClientInfo.Identity);
        if (profile.Kind == DatabaseKind.Redis)
        {
            await ConnectRedisAsync(profile, password, target + " · " + note);
            return;
        }
        try
        {
            var s = await Bg(() => DbSession.ConnectAsync(profile, password));
            UpdateConnection(id, c =>
            {
                c.Session = s;
                c.State = ViewModels.ConnectionState.Connected;
            });
            // PostgreSQL 로 저장한 접속이 실제로는 Redshift 면 종류 · 아이콘을 Redshift 로 (또는 그 반대) + 마지막 연결 시각
            NoteConnected(id, profile.Kind == DatabaseKind.Postgres ? s.Dialect.IsRedshift() : null);
            Log(LogLevel.Success, LogSource.Connection, Tr("%@ 연결됨", profile.Name) + (profile.ReadOnly ? Tr(" (읽기전용)") : "")
                    + (ConnectedCount > 1 ? Tr(" · 열린 접속 %@개", ConnectedCount) : ""),
                audit: new AuditRecord(AuditAction.Connect, profile.Name, profile.ReadOnly ? "성공 (읽기전용)" : "성공", target + " · " + note));
            try
            {
                var dbs = await Bg(() => s.DatabasesAsync());
                UpdateConnection(id, c => c.Databases = dbs);
            }
            catch (Exception e)
            {
                LogError(e, LogSource.Schema, Tr("%@ DB 목록 조회 실패", profile.Name));
            }
            await SelectDatabaseAsync(s.Dialect.InitialContainer(profile, Connections.TryGetValue(id, out var ci) ? ci.Databases : []), id);
            // 이 접속의 탭이 없으면 하나 열어 준다.
            if (!Tabs.Any(t => t.ProfileId == id))
                AddTab(profileId: id);
            else if (Tabs.LastOrDefault(t => t.ProfileId == id) is { } tab && SelectedProfileId == id)
                SelectTab(tab.Id);
        }
        catch (Exception e)
        {
            var message = LogError(e, LogSource.Connection, Tr("%@ 연결 실패", profile.Name),
                                   audit: new AuditRecord(AuditAction.Connect, profile.Name, Statement: target + " · " + note));
            UpdateConnection(id, c => { c.State = ViewModels.ConnectionState.Failed(message); c.Session = null; });
            // 비밀번호가 틀렸으면 다시 입력받는다.
            if (ErrorText.IsAuthFailure(e))
            {
                SessionPasswords.Remove(id);
                PasswordPrompt = new PasswordPrompt(profile, Tr("비밀번호가 틀렸어요. 다시 입력해 주세요."));
            }
        }
    }

    /// 접속 하나를 끊는다. 그 접속의 탭은 남겨 두고, 다시 연결하면 이어서 쓸 수 있다.
    public async Task DisconnectAsync(Guid id, bool force = false)
    {
        // 커밋하지 않은 트랜잭션이 있으면 먼저 묻는다
        if (!force && Connections.TryGetValue(id, out var c) && c.Tx.Open)
        {
            TxPrompt = new TxPrompt(id, TxPromptReason.Disconnect);
            return;
        }
        if (RedisWorkspaces.ContainsKey(id)) await DisconnectRedisAsync(id);
        if (Connections.TryGetValue(id, out var info) && info.Session is { } session)
        {
            await Bg(() => session.CloseAsync());
            Log(LogLevel.Info, LogSource.Connection, Tr("%@ 연결 끊음", session.Profile.Name),
                audit: new AuditRecord(AuditAction.Disconnect, session.Profile.Name));
        }
        ResetConnection(id);
    }

    public async Task DisconnectAllAsync()
    {
        foreach (var id in Connections.Keys.ToList()) await DisconnectAsync(id);
    }

    /// 사이드바 DB(스키마) 선택. 보고 있는 탭이 같은 접속이면 그 탭의 DB 도 바꾼다.
    public async Task SelectDatabaseAsync(string db, Guid? forId = null)
    {
        if ((forId ?? SelectedProfileId) is not { } id || !Connections.TryGetValue(id, out var info) || info.Session is not { } session
            || string.IsNullOrEmpty(db)) return;
        UpdateConnection(id, c =>
        {
            c.CurrentDatabase = db;
            c.SchemaLoading = true;
        });
        if (id == SelectedProfileId) SelectedTables = new HashSet<string>();
        if (SelectedTab is { } tab && tab.ProfileId == id) tab.Database = db;
        try
        {
            await Bg(() => session.UseAsync(db));
            var tables = await Bg(() => session.LoadSchemaAsync(db));
            UpdateConnection(id, c => c.Tables = tables);
            Log(LogLevel.Info, LogSource.Schema, Tr("%@ / %@ 스키마 불러옴 · 테이블 %@개", session.Profile.Name, db, tables.Count));
        }
        catch (Exception e)
        {
            UpdateConnection(id, c => c.Tables = []);
            LogError(e, LogSource.Schema, Tr("%@ 스키마 조회 실패", db));
        }
        try
        {
            var routines = await Bg(() => session.LoadRoutinesAsync(db));
            UpdateConnection(id, c => c.Routines = routines);
        }
        catch (Exception e)
        {
            UpdateConnection(id, c => c.Routines = []);
            LogError(e, LogSource.Schema, Tr("%@ 뷰/프로시저 조회 실패", db));
        }
        UpdateConnection(id, c => c.SchemaLoading = false);
    }

    public async Task RefreshSchemaAsync(Guid? forId = null)
    {
        if ((forId ?? SelectedProfileId) is not { } id || !Connections.TryGetValue(id, out var info)) return;
        await SelectDatabaseAsync(info.CurrentDatabase, id);
    }

    // MARK: - 접속 그룹

    /// 이름순 그룹 목록 (비어 있는 그룹 이름은 없음)
    public List<string> ProfileGroups => ConnectionGroups.Groups(Profiles);

    public void MoveProfile(Guid id, string group)
    {
        var i = IndexOfProfile(id);
        if (i < 0) return;
        var g = group.Trim();
        if (Profiles[i].Group == g) return;
        Profiles[i] = Profiles[i] with { Group = g };
        Persist();
        Log(LogLevel.Info, LogSource.App, g.Length == 0 ? Tr("%@ 접속을 그룹에서 뺐어요", Profiles[i].Name)
                                                         : Tr("%@ 접속을 %@ 그룹으로 옮겼어요", Profiles[i].Name, g));
    }

    public void RenameGroup(string oldName, string newName)
    {
        var n = newName.Trim();
        if (oldName.Length == 0 || oldName == n) return;
        for (var i = 0; i < Profiles.Count; i++)
            if (Profiles[i].Group == oldName) Profiles[i] = Profiles[i] with { Group = n };
        Persist();
        Log(LogLevel.Info, LogSource.App, n.Length == 0 ? Tr("%@ 그룹을 풀었어요", oldName) : Tr("%@ 그룹 이름을 %@ 로 바꿨어요", oldName, n));
    }

    /// 그룹 이름 입력창에서 확인
    public void SubmitGroupPrompt(GroupPrompt prompt, string name)
    {
        var n = name.Trim();
        if (n.Length == 0) return;
        switch (prompt.Action)
        {
            case GroupPromptAction.Move m: MoveProfile(m.ProfileId, n); break;
            case GroupPromptAction.Rename r: RenameGroup(r.OldName, n); break;
        }
        GroupPrompt = null;
    }

    // MARK: - 접속 정보 내보내기 · 가져오기

    private static readonly FileFilter JsonFilter = new("JSON", ["*.json"]);

    /// 파일을 골라 읽고 가져오기 창을 연다. GodDB 파일과 DBeaver(.dbp · data-sources.json) 를 모두 받는다.
    public async Task ChooseImportFileAsync()
    {
        var files = await Dialogs.OpenFilesAsync(Tr("GodDB 에서 내보낸 접속 정보 파일(.json) 또는 DBeaver 파일(.dbp · data-sources.json)을 고르세요."),
            [ImportFilter]);
        if (files.Count == 0) return;
        await ImportFileAsync(files[0]);
    }

    /// 파일 하나를 읽어 가져오기 창을 연다 (끌어 놓기 등)
    public async Task ImportFileAsync(string path)
    {
        try
        {
            var data = await File.ReadAllBytesAsync(path);
            if (Path.GetExtension(path).Equals(".dbp", StringComparison.OrdinalIgnoreCase) || DBeaverImport.LooksLikeDBeaver(data))
            {
                ImportRequest = DBeaverRequest(path, await Task.Run(() => DBeaverImport.ParseFile(path)));
                return;
            }
            var bundle = ConnectionTransfer.Parse(data);
            ImportRequest = new ImportRequest(Path.GetFileName(path), bundle);
        }
        catch (Exception e)
        {
            LogError(e, LogSource.App, Tr("접속 정보 가져오기 실패"));
            await Dialogs.AlertAsync(Tr("접속 정보 가져오기 실패"), ErrorText.Describe(e).Message);
        }
    }

    /// 클립보드(또는 text)의 DataGrip 접속 정보를 읽어 가져오기 창을 연다.
    public async Task PasteDataGripAsync(string? text = null)
    {
        text ??= await Dialogs.GetClipboardTextAsync() ?? "";
        try
        {
            var sources = DataGripImport.Parse(text);
            var bundle = new ConnectionBundle
            {
                ExportedAt = DateTimeOffset.Now, App = "DataGrip", Profiles = [.. sources.Select(s => s.Profile)],
            };
            var notes = new Dictionary<Guid, IReadOnlyList<string>>();
            foreach (var s in sources) notes.TryAdd(s.Profile.Id, s.Notes);
            ImportRequest = new ImportRequest(Tr("DataGrip 클립보드"), bundle) { Notes = notes };
        }
        catch (Exception e)
        {
            LogError(e, LogSource.App, Tr("DataGrip 접속 정보 붙여넣기 실패"));
            await Dialogs.AlertAsync(Tr("DataGrip 접속 정보 붙여넣기 실패"), ErrorText.Describe(e).Message);
        }
    }

    /// 고른 접속을 합친다. 비밀번호가 있으면 보관소에 넣는다.
    public void ImportProfiles(IReadOnlyList<ConnectionProfile> incoming, IReadOnlyDictionary<Guid, string> passwords, string fileName)
    {
        var merged = ConnectionTransfer.Merge(incoming, Profiles);
        Profiles.Clear();
        foreach (var p in merged) Profiles.Add(p);
        var saved = 0;
        foreach (var p in incoming)
        {
            if (!passwords.TryGetValue(p.Id, out var pw)) continue;
            if (p.SavePassword) Secrets.SetPassword(p.Id, pw);
            else SessionPasswords[p.Id] = pw;
            saved++;
        }
        Persist();
        var entry = ConnectionTransfer.ImportedLog(incoming, fileName, saved);
        Log(entry.Level, entry.Source, entry.Message, entry.Detail, entry.Hint, entry.Audit);
    }

    /// 내보내기: 저장 위치를 고르고 파일을 쓴다.
    public async Task<bool> ExportProfilesAsync(IReadOnlyList<ConnectionProfile> list, string? passphrase)
    {
        var path = await Dialogs.SaveFileAsync(Tr("접속 정보 내보내기"), ConnectionTransfer.ExportFileName(DateTime.Now), [JsonFilter]);
        if (path == null) return false;
        try
        {
            Dictionary<Guid, string>? passwords = null;
            if (passphrase != null)
            {
                passwords = [];
                foreach (var p in list)
                {
                    var pw = Secrets.GetPassword(p.Id) ?? (SessionPasswords.TryGetValue(p.Id, out var s) ? s : null);
                    if (pw != null) passwords[p.Id] = pw;
                }
            }
            var data = await Task.Run(() => ConnectionTransfer.Export(list, passwords, passphrase, AppInfo.Version));
            ConnectionTransfer.WriteFile(path, data);
            var entry = ConnectionTransfer.ExportedLog(list, path, passwords?.Count);
            Log(entry.Level, entry.Source, entry.Message, entry.Detail, entry.Hint, entry.Audit);
            Dialogs.RevealInFileManager(path);
            return true;
        }
        catch (Exception e)
        {
            LogError(e, LogSource.App, Tr("접속 정보 내보내기 실패"));
            await Dialogs.AlertAsync(Tr("접속 정보 내보내기 실패"), ErrorText.Describe(e).Message);
            return false;
        }
    }

    // MARK: - Redis

    public RedisWorkspace? SelectedRedis =>
        SelectedProfileId is { } id && RedisWorkspaces.TryGetValue(id, out var ws) ? ws : null;

    /// Redis 화면 상태를 만드는 곳 (Redis 화면 포팅에서 자기 클래스로 바꿀 수 있다)
    public static Func<Guid, RedisSession, AppState, RedisWorkspace> RedisWorkspaceFactory { get; set; } =
        (id, s, app) => new RedisWorkspace(id, s, app);

    public async Task ConnectRedisAsync(ConnectionProfile profile, string? password, string route = "")
    {
        var id = profile.Id;
        try
        {
            var s = await Bg(() => RedisSession.ConnectAsync(profile, password));
            var ws = RedisWorkspaceFactory(id, s, this);
            await ws.LoadTopologyAsync();
            RedisWorkspaces[id] = ws;
            UpdateConnection(id, c => c.State = ViewModels.ConnectionState.Connected);
            NoteConnected(id);
            string version = "";
            try
            {
                var info = await Bg(() => s.InfoAsync("server"));
                version = info.FirstOrDefault()?.Items.FirstOrDefault(p => p.Key == "redis_version")?.Value ?? "";
            }
            catch { }
            var topo = ws.TopologyText.Length == 0 ? "" : " · " + ws.TopologyText;
            Log(LogLevel.Success, LogSource.Connection, Tr("%@ 연결됨", profile.Name) + $" · Redis {version} · db{s.Db}" + topo
                    + (profile.ReadOnly ? Tr(" (읽기전용)") : ""),
                detail: ws.Nodes.Count > 1 ? "nodes: " + string.Join(", ", ws.Nodes) : null,
                audit: new AuditRecord(AuditAction.Connect, profile.Name,
                                       $"성공 · Redis {version}" + topo + (profile.ReadOnly ? " (읽기전용)" : ""), route));
            await ws.LoadDatabasesAsync();
            await ws.ReloadKeysAsync();
        }
        catch (Exception e)
        {
            var message = LogError(e, LogSource.Connection, Tr("%@ 연결 실패", profile.Name),
                                   audit: new AuditRecord(AuditAction.Connect, profile.Name, Statement: route));
            UpdateConnection(id, c => c.State = ViewModels.ConnectionState.Failed(message));
            if (ErrorText.IsAuthFailure(e))
            {
                SessionPasswords.Remove(id);
                PasswordPrompt = new PasswordPrompt(profile, Tr("비밀번호가 틀렸어요. 다시 입력해 주세요."));
            }
        }
    }

    public async Task DisconnectRedisAsync(Guid id)
    {
        if (!RedisWorkspaces.TryGetValue(id, out var ws)) return;
        await ws.CloseAsync();
        await Bg(() => ws.Session.CloseAsync());
        RedisWorkspaces.Remove(id);
        OnPropertyChanged(nameof(SelectedRedis));
        Log(LogLevel.Info, LogSource.Connection, Tr("%@ 연결 끊음", ws.Name), audit: new AuditRecord(AuditAction.Disconnect, ws.Name));
    }
}
