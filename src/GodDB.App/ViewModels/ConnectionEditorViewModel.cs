using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GodDB.App.ViewModels;

/// 접속 편집 창 (Mac ConnectionEditorView)
public sealed partial class ConnectionEditorViewModel : ObservableObject
{
    public AppState App { get; }
    public bool IsNew { get; }
    private readonly ConnectionProfile _original;

    public ConnectionEditorViewModel(AppState app, ConnectionProfile profile, bool isNew)
    {
        App = app;
        IsNew = isNew;
        _original = profile;
        _kind = profile.Kind;
        _name = profile.Name;
        _group = profile.Group;
        _host = profile.Host;
        _port = profile.Port;
        _username = profile.Username;
        _database = profile.Database;
        _useTls = profile.UseTLS;
        _tlsVerify = profile.TlsVerify;
        _tlsPin = string.IsNullOrEmpty(profile.TlsPinSHA256) ? null : profile.TlsPinSHA256;
        _readOnly = profile.ReadOnly;
        _savePassword = profile.SavePassword;
        _redisMode = profile.RedisMode;
        _sentinelMaster = profile.SentinelMaster;
        _environment = profile.Environment;
        _windowsAuth = profile.WindowsAuth;
        _useSsh = profile.Ssh != null;
        var ssh = profile.Ssh ?? new SshConfig();
        _sshHost = ssh.Host;
        _sshPort = ssh.Port;
        _sshUser = ssh.Username;
        _sshKeyPath = ssh.KeyPath;
        var saved = app.Secrets.GetPassword(profile.Id);
        _loading = true;
        Password = saved ?? "";
        _loading = false;
        _hasSavedPassword = saved != null;
        _showsSavedPassword = saved != null;
    }

    private readonly bool _loading;

    public IReadOnlyList<DatabaseKind> Kinds { get; } = Enum.GetValues<DatabaseKind>();
    public IReadOnlyList<RedisMode> RedisModes { get; } = Enum.GetValues<RedisMode>();
    public IReadOnlyList<ConnectionEnvironment> Environments { get; } = Enum.GetValues<ConnectionEnvironment>();

    [ObservableProperty] private DatabaseKind _kind;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _group;
    [ObservableProperty] private string _host;
    [ObservableProperty] private int _port;
    [ObservableProperty] private string _username;
    [ObservableProperty] private string _database;
    [ObservableProperty] private bool _useTls;
    /// TLS 서버 인증서 확인 (F9-3, 기본 켬)
    [ObservableProperty] private bool _tlsVerify;
    /// 고정한 인증서 지문 (없으면 null). 연결 테스트에서 신뢰하면 여기에 들어가고 저장할 때 반영된다
    [ObservableProperty] private string? _tlsPin;
    [ObservableProperty] private bool _readOnly;
    [ObservableProperty] private bool _savePassword;
    [ObservableProperty] private RedisMode _redisMode;
    [ObservableProperty] private string _sentinelMaster;
    [ObservableProperty] private ConnectionEnvironment _environment;
    /// SQL Server Windows 인증 (Windows 판에서만: 사용자·비밀번호 칸을 숨기고 Integrated Security)
    [ObservableProperty] private bool _windowsAuth;
    [ObservableProperty] private bool _useSsh;
    [ObservableProperty] private string _sshHost;
    [ObservableProperty] private int _sshPort;
    [ObservableProperty] private string _sshUser;
    [ObservableProperty] private string _sshKeyPath;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _passwordChanged;
    [ObservableProperty] private bool _hasSavedPassword;
    /// 칸에 들어 있는 값이 아직 저장된 비밀번호에서 온 것인지 (칸을 한 번 완전히 비우면 false).
    /// 한 글자만 지우고 보기를 눌러 본인 확인을 건너뛰지 못하게 한다.
    [ObservableProperty] private bool _showsSavedPassword;
    [ObservableProperty] private bool _testing;
    [ObservableProperty] private string? _testMessage;
    [ObservableProperty] private bool _testOk;

    partial void OnKindChanging(DatabaseKind oldValue, DatabaseKind newValue)
    {
        // 포트가 이전 종류의 기본값이면 새 종류의 기본값으로 바꾼다.
        if (Port == oldValue.DefaultPort()) Port = newValue.DefaultPort();
    }

    partial void OnKindChanged(DatabaseKind value) => NotifyLayout();

    partial void OnRedisModeChanging(RedisMode oldValue, RedisMode newValue)
    {
        // 센티널 기본 포트 26379 ↔ 6379
        if (newValue == RedisMode.Sentinel && Port == 6379) Port = 26379;
        if (newValue != RedisMode.Sentinel && Port == 26379) Port = 6379;
    }

    partial void OnRedisModeChanged(RedisMode value) => NotifyLayout();
    partial void OnReadOnlyChanged(bool value) => NotifyLayout();
    partial void OnEnvironmentChanged(ConnectionEnvironment value) => NotifyLayout();
    partial void OnWindowsAuthChanged(bool value) => NotifyLayout();
    partial void OnUseTlsChanged(bool value) => NotifyTls();
    partial void OnTlsVerifyChanged(bool value) => NotifyTls();
    partial void OnTlsPinChanged(string? value) => NotifyTls();
    partial void OnUseSshChanged(bool value) => NotifyTls();
    partial void OnNameChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

    private void NotifyTls()
    {
        foreach (var n in new[] { nameof(TlsVerifyHelp), nameof(ShowTlsUncheckedWarning), nameof(ShowTlsVerifyHelp), nameof(HasTlsPin) })
            OnPropertyChanged(n);
    }

    /// "서버 인증서 확인" 아래 설명 (Mac ConnectionEditorView.tlsVerifyRows)
    public string TlsVerifyHelp => UseSsh
        ? Tr("SSH 터널 안쪽은 127.0.0.1 이라 이름은 확인하지 않고, 고정한 인증서가 있으면 지문만 확인해요.")
        : Tr("시스템 신뢰 저장소로 인증서와 호스트 이름을 확인해요. 사설 인증서면 처음 연결할 때 지문을 보여 주고 고정할지 물어봐요.");
    public bool ShowTlsVerifyHelp => UseTls && TlsVerify;
    public bool ShowTlsUncheckedWarning => UseTls && !TlsVerify;
    public bool HasTlsPin => UseTls && !string.IsNullOrEmpty(TlsPin);

    /// 연결 테스트에서 인증서 · SSH 호스트 키 확인 창이 필요할 때 (창이 띄우고 [신뢰] · [지우기] 를 누르면 true)
    public Func<SecurityPrompt, Task<bool>>? AskSecurity { get; set; }

    [RelayCommand]
    private void ClearTlsPin() => TlsPin = null;
    partial void OnHostChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnPasswordChanged(string value)
    {
        if (_loading) return;
        PasswordChanged = true;
        if (value.Length == 0) ShowsSavedPassword = false;
    }

    private void NotifyLayout()
    {
        foreach (var n in new[] { nameof(IsRedis), nameof(IsSentinel), nameof(IsMssql), nameof(ShowCredentials), nameof(ShowAuthPicker),
                                  nameof(KindTitle), nameof(ReadOnlyTitle), nameof(HostTitle), nameof(HostWatermark), nameof(UserTitle),
                                  nameof(UserWatermark), nameof(DatabaseTitle), nameof(DatabaseWatermark), nameof(ShowDatabase),
                                  nameof(DatabaseHelp), nameof(RedisModeHelp), nameof(ShowProductionNote), nameof(ShowMssqlReadOnlyNote),
                                  nameof(AuthIndex), nameof(PasswordReason) })
            OnPropertyChanged(n);
    }

    public bool IsRedis => Kind == DatabaseKind.Redis;
    public bool IsSentinel => IsRedis && RedisMode == RedisMode.Sentinel;
    public bool IsMssql => Kind == DatabaseKind.Mssql;
    /// 인증 고르기는 Windows 판 SQL Server 에서만 (Mac 에는 없음)
    public bool ShowAuthPicker => IsMssql && OperatingSystem.IsWindows();
    public bool ShowCredentials => !(IsMssql && WindowsAuth);
    public int AuthIndex { get => WindowsAuth ? 1 : 0; set => WindowsAuth = value == 1; }
    public string KindTitle => Kind.Title();
    public string ReadOnlyTitle => IsRedis ? Tr("읽기전용 (조회 명령만 허용)") : Tr("읽기전용 (SELECT / SHOW / DESC / EXPLAIN 만 허용)");
    public string HostTitle => IsSentinel ? Tr("센티널 호스트") : Tr("호스트");
    public string HostWatermark => IsSentinel ? "s1:26379, s2:26379" : "xxx.rds.amazonaws.com";
    public string UserTitle => IsRedis ? Tr("ACL 사용자 (선택)") : Tr("사용자");
    public string? UserWatermark => IsRedis ? Tr("비우면 비밀번호만으로 AUTH") : null;
    public bool ShowProductionNote => Environment == ConnectionEnvironment.Production;
    /// SQL Server 는 세션 읽기전용이 없어 서버에서 강제할 수 없다 (MySQL · PostgreSQL 은 세션 READ ONLY 로 이중 차단, 1.4.0 안내 강화)
    public bool ShowMssqlReadOnlyNote => ReadOnly && IsMssql;
    public string KeyWatermark => Tr(@"%USERPROFILE%\.ssh\bastion.pem (비우면 ssh-agent)");
    public string PasswordReason => Tr("'%@' 접속의 저장된 비밀번호를 보려고 해요", Name);

    public bool ShowDatabase => !(IsRedis && RedisMode == RedisMode.Cluster);
    public string DatabaseTitle => Kind switch
    {
        DatabaseKind.Postgres => Tr("접속 DB"),
        DatabaseKind.Redis => Tr("DB 번호"),
        _ => Tr("기본 DB"),
    };
    public string DatabaseWatermark => Kind switch
    {
        DatabaseKind.Postgres => Tr("비우면 postgres"),
        DatabaseKind.Mssql => Tr("비우면 첫 번째 사용자 DB"),
        DatabaseKind.Mysql => Tr("비우면 첫 번째 DB"),
        _ => "0",
    };
    public string? DatabaseHelp => Kind switch
    {
        DatabaseKind.Postgres => Tr("PostgreSQL 은 DB 마다 따로 접속해요. 사이드바에서는 이 DB 의 스키마를 골라요."),
        DatabaseKind.Redis when RedisMode != RedisMode.Cluster => Tr("0~15 (서버 설정에 따라 다름). 연결한 뒤 사이드바에서 바꿀 수 있어요. ElastiCache 전송 중 암호화를 쓰면 TLS 를 켜세요."),
        _ => null,
    };

    public string RedisModeHelp => RedisMode switch
    {
        RedisMode.Standalone => Tr("서버 하나에 접속해요. 서버가 Cluster 면 자동으로 Cluster 로 동작해요."),
        RedisMode.Cluster => Tr("노드 하나(또는 ElastiCache 구성 엔드포인트)만 적으면 나머지 노드를 찾아 키마다 맞는 노드로 보내요. MOVED/ASK 는 자동 처리. DB 는 0 만 써요. SSH 를 쓰면 노드마다 터널을 열어요."),
        _ => Tr("센티널 주소(쉼표로 여러 개)와 마스터 이름을 넣으면 현재 마스터를 찾아 접속해요. 장애 조치 뒤에는 다시 연결하면 새 마스터로 붙어요."),
    };

    public string SavedPasswordText => HasSavedPassword
        ? Tr("자격 증명 관리자에 저장된 비밀번호가 있어요.")
        : Tr("저장된 비밀번호가 없어요. 입력 후 저장하면 자격 증명 관리자에 보관돼요.");

    partial void OnHasSavedPasswordChanged(bool value) => OnPropertyChanged(nameof(SavedPasswordText));

    /// 저장할 값
    public ConnectionProfile Draft => _original with
    {
        Kind = Kind,
        Name = Name.Trim(),
        Group = Group,
        Host = Host.Trim(),
        Port = Port,
        Username = Username,
        Database = Database,
        UseTLS = UseTls,
        TlsVerify = TlsVerify,
        TlsPinSHA256 = string.IsNullOrEmpty(TlsPin) ? null : TlsPin,
        ReadOnly = ReadOnly,
        SavePassword = SavePassword,
        RedisMode = RedisMode,
        SentinelMaster = SentinelMaster,
        Environment = Environment,
        WindowsAuth = Kind == DatabaseKind.Mssql && WindowsAuth,
        Ssh = UseSsh ? new SshConfig { Host = SshHost, Port = SshPort, Username = SshUser, KeyPath = SshKeyPath } : null,
    };

    private bool CanSave() => Name.Length > 0 && Host.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => App.SaveProfile(Draft, (IsNew || PasswordChanged) ? Password : null);

    [RelayCommand]
    private void Cancel() => App.EditingProfile = null;

    [RelayCommand]
    private void ClearSavedPassword()
    {
        App.Secrets.DeletePassword(_original.Id);
        Password = "";
        HasSavedPassword = false;
        ShowsSavedPassword = false;
    }

    /// 저장된 비밀번호를 본인 확인 후 보였을 때 (값은 남기지 않는다)
    public void OnAuthenticatedReveal() =>
        App.Log(LogLevel.Info, LogSource.App, Tr("%@ 저장된 비밀번호 보기 (본인 확인)", Name),
                audit: new AuditRecord(AuditAction.Settings, Name, "저장된 비밀번호 보기"));

    [RelayCommand]
    private async Task TestAsync()
    {
        SecurityPrompt? prompt = null;
        Testing = true;
        TestMessage = null;
        var draft = Draft;
        try
        {
            if (draft.Kind == DatabaseKind.Redis)
            {
                var session = await Task.Run(() => RedisSession.ConnectAsync(draft, Password));
                var version = "Redis";
                try
                {
                    var info = await Task.Run(() => session.InfoAsync("server"));
                    if (info.FirstOrDefault()?.Items.FirstOrDefault(p => p.Key == "redis_version")?.Value is { } v) version = $"Redis {v}";
                }
                catch { }
                version += " · " + session.TlsStatus.Text();
                await session.CloseAsync();
                TestOk = true;
                TestMessage = Tr("연결 성공 · %@", version);
                App.Log(LogLevel.Success, LogSource.Connection, Tr("%@ 연결 테스트 성공 · %@", draft.Name, version));
                return;
            }
            var s = await Task.Run(() => DbSession.ConnectAsync(draft, draft.NeedsPassword ? Password : null));
            // 연결은 이미 성공. 버전 조회가 안 되더라도(권한 등) 실패로 보지 않는다.
            string ver;
            try { ver = await Task.Run(() => s.ServerVersionAsync()); } catch { ver = draft.Kind.Title(); }
            ver += " · " + s.TlsStatus.Text();
            await s.CloseAsync();
            TestOk = true;
            TestMessage = Tr("연결 성공 · %@", ver);
            App.Log(LogLevel.Success, LogSource.Connection, Tr("%@ 연결 테스트 성공 · %@", draft.Name, ver));
        }
        catch (Exception e)
        {
            TestOk = false;
            TestMessage = App.LogError(e, LogSource.Connection, Tr("%@ 연결 테스트 실패", draft.Name));
            prompt = SecurityPrompt.From(e);
        }
        finally
        {
            Testing = false;
        }
        // 인증서 · SSH 호스트 키 확인 창: [신뢰] · [지우기] 면 고정 지문은 편집 중인 값에 넣고(저장할 때 감사 로그), 다시 테스트
        if (prompt != null && AskSecurity is { } ask && await ask(prompt))
        {
            switch (App.ApplySecurityAccept(prompt, draft.Name))
            {
                case PinChange.Set set: TlsPin = set.Pin; break;
                case PinChange.Clear: TlsPin = null; break;
            }
            await TestAsync();
        }
    }
}
