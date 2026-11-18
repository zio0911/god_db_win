using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GodDB.App.ViewModels;

/// Redis 화면 탭 (Mac RedisWorkspace.Tab)
public enum RedisTab { Keys, Console, PubSub, Streams, Info }

/// 선택한 키의 값 (Mac RedisWorkspace.Value)
public abstract record RedisValue
{
    public sealed record Str(string Value) : RedisValue;
    public sealed record Hash(IReadOnlyList<RedisPair> Items) : RedisValue;
    public sealed record List(IReadOnlyList<string> Items, long Total) : RedisValue;
    public sealed record Set(IReadOnlyList<string> Items) : RedisValue;
    /// (멤버, 점수) 점수 높은 순
    public sealed record Zset(IReadOnlyList<RedisPair> Items, long Total) : RedisValue;
    public sealed record Stream(IReadOnlyList<RedisStreamEntry> Entries) : RedisValue;
    public sealed record Other(string Type) : RedisValue;
}

/// 사이드바 DB 선택 한 줄
public sealed record RedisDbItem(int Index, long Keys)
{
    public string Title => Keys > 0 ? $"db{Index} ({Keys})" : $"db{Index}";
    public override string ToString() => Title;
}

/// Workbench 결과 한 덩어리
public sealed record RedisConsoleEntry(string Command, string Output, bool IsError, int Millis, int Db)
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Prompt => $"db{Db}> ";
    public string MillisText => Millis > 0 ? $"{Millis}ms" : "";
}

/// 목록에 보일 키의 타입·TTL (보일 때 묶어서 가져온다)
public sealed record RedisKeyMetaInfo(string Type, long TtlMillis, DateTime Fetched);

/// 사이드바 키 트리/목록 한 줄 (폴더 또는 키)
public sealed partial class RedisKeyRowItem : ObservableObject
{
    public RedisKeyRowItem(RedisKeyNode? folder, string? key, string label, int depth, int count)
    {
        Folder = folder;
        Key = key;
        Label = label.Length == 0 ? "(empty)" : label;
        IsEmptyLabel = label.Length == 0;
        Depth = depth;
        Count = count;
    }

    public RedisKeyNode? Folder { get; }
    public string? Key { get; }
    public bool IsFolder => Folder != null;
    public bool IsKey => Folder == null;
    public string Label { get; }
    public bool IsEmptyLabel { get; }
    public int Depth { get; }
    public int Count { get; }
    public string CountText => Count.ToString(CultureInfo.InvariantCulture);
    /// 들여쓰기 (폴더 14pt 단위, 키는 한 칸 더)
    public Avalonia.Thickness Indent => new(Depth * 14 + (IsFolder ? 0 : 14), 0, 0, 0);
    public string Tip => IsFolder ? Folder!.Path + _delimiter + "*" : Key!;
    internal string _delimiter = ":";

    [ObservableProperty] private bool _expanded;
    [ObservableProperty] private bool _selected;
    [ObservableProperty] private string _typeLetter = "";
    [ObservableProperty] private string _typeName = "";
    [ObservableProperty] private bool _hasMeta;
    [ObservableProperty] private string _ttlText = "";
    [ObservableProperty] private string _ttlTip = "";
    [ObservableProperty] private bool _ttlSoon;
    [ObservableProperty] private bool _expired;
    [ObservableProperty] private bool _missing;

    public string Chevron => Expanded ? "▾" : "▸";
    partial void OnExpandedChanged(bool value) => OnPropertyChanged(nameof(Chevron));
    public IBrush TypeBrush => RedisWorkspace.TypeBrush(TypeName);
    public IBrush TypeBackground => RedisWorkspace.TypeBackground(TypeName);
    partial void OnTypeNameChanged(string value)
    {
        OnPropertyChanged(nameof(TypeBrush));
        OnPropertyChanged(nameof(TypeBackground));
    }

    /// 타입·남은 시간 표시를 새로 (now 기준)
    internal void Apply(RedisKeyMetaInfo? m, DateTime now)
    {
        if (m == null)
        {
            HasMeta = false; TypeLetter = ""; TypeName = ""; TtlText = ""; Expired = false; Missing = false;
            return;
        }
        HasMeta = true;
        TypeName = m.Type;
        TypeLetter = RedisWorkspace.TypeLetter(m.Type);
        Missing = m.Type == "none";
        var ttl = RedisFormat.RemainingTtl(m.TtlMillis, m.Fetched, now);
        Expired = ttl == -2;
        if (ttl >= 0)
        {
            TtlText = "⏱ " + RedisFormat.ShortTtl(ttl);
            TtlSoon = ttl < 60_000;
            TtlTip = Tr("남은 시간 %@", RedisFormat.TtlText(ttl));
        }
        else
        {
            TtlText = "";
            TtlSoon = false;
            TtlTip = "";
        }
    }
}

/// Redis 접속 하나의 화면 상태 (키 목록, 선택한 키, 콘솔, Pub/Sub, Streams, 서버 정보) — Mac RedisModel.swift
public partial class RedisWorkspace : ObservableObject
{
    public Guid ProfileId { get; }
    public RedisSession Session { get; }
    public AppState? App { get; }

    public RedisWorkspace(Guid profileId, RedisSession session, AppState? app)
    {
        ProfileId = profileId;
        Session = session;
        App = app;
        _treeMode = Prefs.Get(TreeModeKey, true);
        _delimiter = Prefs.GetString(DelimiterKey) ?? ":";
        _autoRefreshSeconds = Math.Max(1, Prefs.Get(AutoRefreshSecondsKey, 5));
        _workbench = Prefs.GetString(WorkbenchKey) ?? DefaultWorkbench;
    }

    public string Name => Session.Profile.Name;
    public bool ReadOnly => Session.Profile.ReadOnly;
    public bool CanWrite => !ReadOnly;
    /// 감사 로그 대상 칸
    public string AuditTarget => $"{Name} db{Db}";
    public string HeaderText => $"{Name} · db{Db}";

    [ObservableProperty] private RedisTab _tab = RedisTab.Keys;
    /// 새 키 창
    [ObservableProperty] private bool _showNewKey;

    // MARK: - 표시 도우미

    public static string TypeLetter(string t) => t switch
    {
        "string" => "S",
        "hash" => "H",
        "list" => "L",
        "set" => "St",
        "zset" => "Z",
        "stream" => "X",
        "none" => "–",
        _ => "?",
    };

    private static Color TypeColor(string t) => t switch
    {
        "string" => Color.Parse("#0A84FF"),
        "hash" => Color.Parse("#AF52DE"),
        "list" => Color.Parse("#FF9500"),
        "set" => Color.Parse("#34C759"),
        "zset" => Color.Parse("#FF2D55"),
        "stream" => Color.Parse("#30B0C7"),
        _ => Color.Parse("#8A8A8E"),
    };

    public static IBrush TypeBrush(string t) => new SolidColorBrush(TypeColor(t));
    public static IBrush TypeBackground(string t) => new SolidColorBrush(TypeColor(t), 0.14);

    /// 바이트 크기 (Mac ByteCountFormatter 와 비슷하게)
    public static string ByteText(long v)
    {
        if (v < 1000) return $"{v} bytes";
        string[] units = ["KB", "MB", "GB", "TB"];
        double x = v;
        var i = -1;
        do { x /= 1000; i++; } while (x >= 1000 && i < units.Length - 1);
        return x.ToString(i == 0 ? "F0" : "F1", CultureInfo.InvariantCulture) + " " + units[i];
    }

    /// 스트림 ID 앞부분(밀리초)을 시각으로
    public static string StreamTime(string id)
    {
        var head = id.Split('-')[0];
        if (!double.TryParse(head, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) return "";
        try { return DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime.ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return ""; }
    }

    /// JSON 이면 보기 좋게 펼친 글 (키 정렬), 아니면 null
    public static string? PrettyJson(string s)
    {
        var t = s.Trim(' ');
        if (!(t.StartsWith('{') || t.StartsWith('['))) return null;
        try
        {
            var node = JsonNode.Parse(t);
            return Sorted(node)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch (JsonException) { return null; }

        static JsonNode? Sorted(JsonNode? n) => n switch
        {
            JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
            JsonArray a => new JsonArray(a.Select(Sorted).ToArray()),
            null => null,
            _ => JsonNode.Parse(n.ToJsonString()),
        };
    }

    /// "a=1 b=hello" → [(a, 1), (b, hello)]
    public static List<RedisPair>? ParsePairs(string s)
    {
        if (RedisText.SplitArgs(s) is not { Count: > 0 } args) return null;
        var output = new List<RedisPair>();
        foreach (var a in args)
        {
            var i = a.IndexOf('=');
            if (i <= 0) return null;
            output.Add(new RedisPair(a[..i], a[(i + 1)..]));
        }
        return output;
    }

    // MARK: - DB

    [ObservableProperty] private IReadOnlyList<RedisDbItem> _databases = [];
    [ObservableProperty] private int _db;

    partial void OnDbChanged(int value)
    {
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(AuditTarget));
        OnPropertyChanged(nameof(SelectedDatabase));
    }

    partial void OnDatabasesChanged(IReadOnlyList<RedisDbItem> value) => OnPropertyChanged(nameof(SelectedDatabase));

    /// DB 선택 칸 (바꾸면 SELECT)
    public RedisDbItem? SelectedDatabase
    {
        get => Databases.FirstOrDefault(d => d.Index == Db);
        set
        {
            if (value != null && value.Index != Db) Fire(() => SelectDbAsync(value.Index));
        }
    }

    public virtual async Task LoadDatabasesAsync()
    {
        try
        {
            var list = await AppState.Bg(() => Session.DatabasesAsync());
            var items = list.Select(d => new RedisDbItem(d.Index, d.Keys)).ToList();
            var db = Session.Db;
            if (!items.Any(d => d.Index == db)) items.Add(new RedisDbItem(db, 0));
            Databases = items;
            Db = db;
        }
        catch (Exception e)
        {
            App?.LogError(e, LogSource.Schema, Tr("%@ DB 목록 조회 실패", Name));
        }
    }

    public async Task SelectDbAsync(int index)
    {
        if (index == Db) return;
        try
        {
            await AppState.Bg(() => Session.SelectAsync(index));
            Db = index;
            SelectedKey = null;
            KeyInfo = null;
            Value = null;
            Stream = null;
            StopTail();
            await ReloadKeysAsync();
            if (Tab == RedisTab.Streams) await LoadStreamKeysAsync();
        }
        catch (Exception e)
        {
            App?.LogError(e, LogSource.Schema, Tr("%@ DB 전환 실패", Name));
            OnPropertyChanged(nameof(SelectedDatabase));
        }
    }

    // MARK: - 키 목록

    public const string TreeModeKey = "GodDB.redis.treeMode";
    public const string DelimiterKey = "GodDB.redis.delimiter";
    public static readonly IReadOnlyList<string> Delimiters = [":", "/", ".", "_", "-", "|"];
    public static readonly IReadOnlyList<string> KeyTypes = ["string", "hash", "list", "set", "zset", "stream"];

    [ObservableProperty] private string _pattern = "*";
    /// "" = 모든 타입
    [ObservableProperty] private string _typeFilter = "";
    [ObservableProperty] private bool _treeMode;
    [ObservableProperty] private string _delimiter;
    [ObservableProperty] private string? _nextCursor;
    [ObservableProperty] private bool _loadingKeys;

    private IReadOnlyList<string> _keys = [];
    /// 불러온 키 (정렬됨). 바꾸면 트리를 다시 만든다.
    public IReadOnlyList<string> Keys
    {
        get => _keys;
        set
        {
            _keys = value;
            OnPropertyChanged();
            RebuildTree();
        }
    }

    public IReadOnlyList<RedisKeyNode> Tree { get; private set; } = [];
    /// 펼친 폴더 id
    public HashSet<string> Expanded { get; } = [];
    /// 사이드바에 보이는 줄 (트리면 펼친 폴더의 하위만)
    [ObservableProperty] private IReadOnlyList<RedisKeyRowItem> _rows = [];

    public string KeyCountText => Keys.Count.ToString(CultureInfo.InvariantCulture) + (NextCursor != null ? "+" : "");
    public bool HasMoreKeys => NextCursor != null;
    public bool ShowAllCount => NextCursor == null && Keys.Count > 0;
    public bool ShowNoKeys => NextCursor == null && Keys.Count == 0 && !LoadingKeys;
    public bool ShowLoadingKeys => LoadingKeys && Keys.Count == 0;
    public string AllCountText => Tr("키 %@개 (전부)", Keys.Count.ToString(CultureInfo.InvariantCulture));
    public string MoreKeysText => LoadingKeys ? Tr("불러오는 중…") : Tr("키 더 불러오기");

    private void NotifyListFooter()
    {
        foreach (var n in new[] { nameof(KeyCountText), nameof(HasMoreKeys), nameof(ShowAllCount), nameof(ShowNoKeys),
                                  nameof(ShowLoadingKeys), nameof(AllCountText), nameof(MoreKeysText) })
            OnPropertyChanged(n);
    }

    partial void OnNextCursorChanged(string? value) => NotifyListFooter();
    partial void OnLoadingKeysChanged(bool value) => NotifyListFooter();

    partial void OnTreeModeChanged(bool value)
    {
        Prefs.Set(TreeModeKey, value);
        RebuildRows();
    }

    partial void OnDelimiterChanged(string value)
    {
        Prefs.Set(DelimiterKey, value);
        RebuildTree();
    }

    private void RebuildTree()
    {
        Tree = RedisKeyTree.Build(_keys, Delimiter);
        RebuildRows();
        NotifyListFooter();
    }

    /// 보이는 줄을 다시 만든다 (펼침 · 선택 · 메타 유지)
    public void RebuildRows()
    {
        var now = DateTime.Now;
        var rows = new List<RedisKeyRowItem>();
        if (TreeMode)
        {
            foreach (var r in RedisKeyTree.VisibleRows(Tree, Expanded))
            {
                var n = r.Node;
                var item = n.IsFolder
                    ? new RedisKeyRowItem(n, null, n.Name, r.Depth, n.Count) { Expanded = Expanded.Contains(n.Id) }
                    : new RedisKeyRowItem(null, n.Key, n.Name, r.Depth, 1);
                rows.Add(item);
            }
        }
        else
        {
            rows.AddRange(_keys.Select(k => new RedisKeyRowItem(null, k, k, 0, 1)));
        }
        foreach (var item in rows)
        {
            item._delimiter = Delimiter;
            if (item.Key is { } k)
            {
                item.Selected = k == SelectedKey;
                item.Apply(Meta.GetValueOrDefault(k), now);
            }
        }
        Rows = rows;
    }

    /// 폴더 펼치기/접기
    public void Toggle(RedisKeyRowItem folder)
    {
        if (folder.Folder is not { } n) return;
        if (!Expanded.Remove(n.Id)) Expanded.Add(n.Id);
        RebuildRows();
    }

    public static IEnumerable<string> FolderIds(IReadOnlyList<RedisKeyNode> nodes) =>
        nodes.Where(n => n.IsFolder).SelectMany(n => new[] { n.Id }.Concat(FolderIds(n.Children ?? [])));

    public void ExpandAll()
    {
        foreach (var id in FolderIds(Tree)) Expanded.Add(id);
        RebuildRows();
    }

    public void CollapseAll()
    {
        Expanded.Clear();
        RebuildRows();
    }

    /// 폴더 아래 키만 다시 검색
    public async Task SearchFolderAsync(string path)
    {
        Pattern = RedisKeyTree.Pattern(path, Delimiter);
        await ReloadKeysAsync();
        // 검색한 폴더까지 펼쳐 둔다
        var prefix = "";
        foreach (var part in path.Split(Delimiter))
        {
            prefix = prefix.Length == 0 ? part : prefix + Delimiter + part;
            Expanded.Add("f:" + prefix);
        }
        RebuildRows();
    }

    public virtual async Task ReloadKeysAsync()
    {
        Meta.Clear();
        NextCursor = null;
        Keys = [];
        await LoadMoreKeysAsync("0");
    }

    public async Task LoadMoreKeysAsync(string? cursor = null)
    {
        if ((cursor ?? NextCursor) is not { } cur || LoadingKeys) return;
        LoadingKeys = true;
        try
        {
            var start = DateTime.Now;
            string pattern = Pattern, type = TypeFilter;
            var r = await AppState.Bg(() => Session.ScanKeysAsync(pattern, type.Length == 0 ? null : type, cur, 1000));
            var seen = new HashSet<string>(_keys);
            var merged = _keys.Concat(r.Keys.Where(seen.Add)).ToList();
            merged.Sort(StringComparer.Ordinal);
            NextCursor = r.Next;
            Keys = merged;
            App?.Log(LogLevel.Info, LogSource.Schema,
                Tr("[%@ db%@] 키 %@개 · 패턴 %@ · %@ms", Name, Db.ToString(CultureInfo.InvariantCulture), merged.Count.ToString(CultureInfo.InvariantCulture),
                   Pattern, ((int)(DateTime.Now - start).TotalMilliseconds).ToString(CultureInfo.InvariantCulture))
                + (r.Next != null ? Tr(" · 더 있음") : ""));
        }
        catch (Exception e)
        {
            App?.LogError(e, LogSource.Schema, Tr("[%@] 키 조회 실패", Name));
        }
        finally
        {
            LoadingKeys = false;
        }
    }

    // MARK: - 키 타입·TTL (화면에 보이는 키만)

    public Dictionary<string, RedisKeyMetaInfo> Meta { get; } = [];
    private readonly HashSet<string> _metaWanted = [];
    private Task? _metaTask;
    /// 메타 조회가 끝날 때 (테스트용)
    public Task MetaTask => _metaTask ?? Task.CompletedTask;

    /// 행이 화면에 나타날 때 호출. 잠깐 모았다가 한 번에 조회한다.
    public void WantMeta(string key)
    {
        if (Meta.ContainsKey(key)) return;
        _metaWanted.Add(key);
        if (_metaTask != null) return;
        _metaTask = FetchMeta();
    }

    private async Task FetchMeta()
    {
        try
        {
            await Task.Delay(60);
            while (_metaWanted.Count > 0)
            {
                var batch = _metaWanted.Take(300).ToList();
                foreach (var k in batch) _metaWanted.Remove(k);
                Dictionary<string, RedisKeyMeta> r;
                try { r = await AppState.Bg(() => Session.KeyMetaAsync(batch)); }
                catch { break; }
                var now = DateTime.Now;
                foreach (var (k, v) in r) Meta[k] = new RedisKeyMetaInfo(v.Type, v.TtlMillis, now);
                ApplyMetaToRows(r.Keys.ToHashSet());
            }
        }
        finally
        {
            _metaWanted.Clear();
            _metaTask = null;
        }
    }

    private void ApplyMetaToRows(IReadOnlySet<string>? only = null)
    {
        var now = DateTime.Now;
        foreach (var row in Rows)
            if (row.Key is { } k && (only == null || only.Contains(k)))
                row.Apply(Meta.GetValueOrDefault(k), now);
    }

    /// 남은 TTL 글자만 새로 (1초마다 화면이 부른다)
    public void TickTtl()
    {
        var now = DateTime.Now;
        foreach (var row in Rows)
            if (row.Key is { } k && Meta.TryGetValue(k, out var m) && m.TtlMillis >= 0)
                row.Apply(m, now);
    }

    /// 지금 시점의 남은 TTL (밀리초, -1 만료 없음, -2 없음)
    public long? RemainingTtl(string key) =>
        Meta.TryGetValue(key, out var m) ? RedisFormat.RemainingTtl(m.TtlMillis, m.Fetched, DateTime.Now) : null;

    // MARK: - 자동 새로고침

    public const string AutoRefreshSecondsKey = "GodDB.redis.autoRefreshSeconds";
    [ObservableProperty] private bool _autoRefresh;
    [ObservableProperty] private int _autoRefreshSeconds;
    private CancellationTokenSource? _autoCts;
    private bool _refreshing;

    public void SetAutoRefresh(bool on)
    {
        AutoRefresh = on;
        _autoCts?.Cancel();
        _autoCts = null;
        if (!on) return;
        var cts = new CancellationTokenSource();
        _autoCts = cts;
        _ = AutoLoop(cts.Token);
    }

    private async Task AutoLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(AutoRefreshSeconds), ct); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;
            await RefreshAllAsync(quiet: true);
        }
    }

    public void SetAutoRefreshSeconds(int s)
    {
        AutoRefreshSeconds = Math.Clamp(s, 1, 3600);
        Prefs.Set(AutoRefreshSecondsKey, AutoRefreshSeconds);
        if (AutoRefresh) SetAutoRefresh(true);   // 새 간격으로 다시 시작
    }

    /// 키 목록·TTL·열린 값을 다시 읽는다 (펼친 폴더와 선택은 유지)
    public async Task RefreshAllAsync(bool quiet = false)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            if (!quiet) await LoadDatabasesAsync();
            var loaded = Math.Max(Keys.Count, 1000);
            try
            {
                string pattern = Pattern, type = TypeFilter;
                var r = await AppState.Bg(() => Session.ScanKeysAsync(pattern, type.Length == 0 ? null : type, "0", loaded));
                Meta.Clear();
                NextCursor = r.Next;
                Keys = r.Keys.Order(StringComparer.Ordinal).ToList();
            }
            catch (Exception e)
            {
                if (!quiet) App?.LogError(e, LogSource.Schema, Tr("[%@] 키 조회 실패", Name));
                if (quiet) SetAutoRefresh(false);   // 연결이 끊겼으면 자동 새로고침을 멈춘다
                return;
            }
            if (SelectedKey != null && Tab == RedisTab.Keys) await ReloadValueAsync();
        }
        finally
        {
            _refreshing = false;
        }
    }

    // MARK: - 선택한 키

    [ObservableProperty] private string? _selectedKey;
    [ObservableProperty] private RedisKeyInfo? _keyInfo;
    [ObservableProperty] private RedisValue? _value;
    [ObservableProperty] private bool _loadingValue;
    [ObservableProperty] private string? _valueError;
    /// 문자열 값이 커서 앞부분만 받았으면 전체 바이트 수 (편집 막음)
    [ObservableProperty] private long? _valueTruncatedTotal;
    public int ListPageSize { get; set; } = 500;

    partial void OnSelectedKeyChanged(string? value)
    {
        foreach (var row in Rows) if (row.Key != null) row.Selected = row.Key == value;
    }

    public async Task OpenAsync(string key)
    {
        SelectedKey = key;
        Tab = RedisTab.Keys;
        await ReloadValueAsync();
        // 감사 로그: 키 값을 열어 본 것 (자동 새로고침으로 다시 읽는 건 남기지 않음)
        if (ValueError == null)
            App?.AuditOnly(new AuditRecord(AuditAction.Read, AuditTarget, $"키 열기 · {KeyInfo?.Type ?? "?"}", "GET " + key));
    }

    public async Task ReloadValueAsync()
    {
        if (SelectedKey is not { } key) return;
        LoadingValue = true;
        ValueError = null;
        ValueTruncatedTotal = null;
        try
        {
            var info = await AppState.Bg(() => Session.KeyInfoAsync(key));
            KeyInfo = info;
            Meta[key] = new RedisKeyMetaInfo(info.Type, info.TtlMillis, DateTime.Now);
            ApplyMetaToRows(new HashSet<string> { key });
            var page = ListPageSize;
            switch (info.Type)
            {
                case "string":
                    var r = await AppState.Bg(() => Session.StringPrefixAsync(key));
                    ValueTruncatedTotal = r.Truncated ? r.Total : null;
                    Value = new RedisValue.Str(r.Value);
                    break;
                case "hash":
                    Value = new RedisValue.Hash((await AppState.Bg(() => Session.HashAsync(key))).OrderBy(p => p.Key, StringComparer.Ordinal).ToList());
                    break;
                case "list":
                    Value = new RedisValue.List(await AppState.Bg(() => Session.ListAsync(key, 0, page)), info.Length ?? 0);
                    break;
                case "set":
                    Value = new RedisValue.Set((await AppState.Bg(() => Session.SetAsync(key))).Order(StringComparer.Ordinal).ToList());
                    break;
                case "zset":
                    Value = new RedisValue.Zset(await AppState.Bg(() => Session.ZsetAsync(key, 0, page, reverse: true)), info.Length ?? 0);
                    break;
                case "stream":
                    Value = new RedisValue.Stream(await AppState.Bg(() => Session.StreamLatestAsync(key, 200)));
                    break;
                case "none":
                    Value = null;
                    ValueError = Tr("키가 없어요. (지워졌거나 만료됨)");
                    break;
                default:
                    Value = new RedisValue.Other(info.Type);
                    break;
            }
        }
        catch (Exception e)
        {
            Value = null;
            ValueError = App?.LogError(e, LogSource.Query, Tr("[%@] 값 조회 실패", Name)) ?? ErrorText.Describe(e).Message;
        }
        finally
        {
            LoadingValue = false;
        }
    }

    /// 쓰기 명령: 실행하고 상태창(로그 파일)에 남긴다. 실패하면 메시지를 돌려준다.
    public async Task<string?> WriteAsync(IReadOnlyList<string> args, string label)
    {
        var statement = RedisFormat.Display(args);
        try
        {
            var start = DateTime.Now;
            await AppState.Bg(() => Session.CommandAsync(args));
            var ms = (int)(DateTime.Now - start).TotalMilliseconds;
            App?.Log(LogLevel.Success, LogSource.Query, $"[{Name} db{Db}] {label} · {ms}ms", detail: statement,
                     audit: new AuditRecord(AuditRecord.Classify(args), AuditTarget, $"{label} · {ms}ms", statement));
            return null;
        }
        catch (Exception e)
        {
            return App?.LogError(e, LogSource.Query, $"[{Name}] {label}", sql: statement,
                                 audit: new AuditRecord(AuditRecord.Classify(args), AuditTarget, Statement: statement))
                   ?? ErrorText.Describe(e).Message;
        }
    }

    /// 쓰기 후 값 다시 읽기
    public async Task<string?> ApplyAsync(IReadOnlyList<string> args, string label)
    {
        var e = await WriteAsync(args, label);
        await ReloadValueAsync();
        return e;
    }

    public async Task DeleteKeyAsync(string key)
    {
        if (await WriteAsync(["DEL", key], Tr("키 삭제 %@", key)) != null) return;
        Keys = Keys.Where(k => k != key).ToList();
        if (SelectedKey == key) { SelectedKey = null; KeyInfo = null; Value = null; }
    }

    public async Task<string?> RenameKeyAsync(string key, string newName)
    {
        if (await WriteAsync(["RENAME", key, newName], Tr("키 이름 변경 %@ → %@", key, newName)) is { } e) return e;
        if (Keys.Contains(key))
        {
            var list = Keys.Select(k => k == key ? newName : k).ToList();
            list.Sort(StringComparer.Ordinal);
            Keys = list;
        }
        SelectedKey = newName;
        await ReloadValueAsync();
        return null;
    }

    /// seconds 가 null 이면 만료 없앰
    public async Task<string?> SetTtlAsync(string key, long? seconds)
    {
        if (seconds is { } s)
            return await ApplyAsync(["EXPIRE", key, s.ToString(CultureInfo.InvariantCulture)],
                                    Tr("만료 설정 %@ · %@초", key, s.ToString(CultureInfo.InvariantCulture)));
        return await ApplyAsync(["PERSIST", key], Tr("만료 없앰 %@", key));
    }

    /// 새 키 만들기
    public async Task<string?> CreateAsync(string type, string key, string field, string value, long? ttl)
    {
        var args = RedisFormat.CreateKeyArgs(type, key, field, value);
        if (await WriteAsync(args, Tr("새 키 %@ (%@)", key, type)) is { } e) return e;
        if (ttl is > 0)
            await WriteAsync(["EXPIRE", key, ttl.Value.ToString(CultureInfo.InvariantCulture)],
                             Tr("만료 설정 %@ · %@초", key, ttl.Value.ToString(CultureInfo.InvariantCulture)));
        if (!Keys.Contains(key))
        {
            var list = Keys.Append(key).ToList();
            list.Sort(StringComparer.Ordinal);
            Keys = list;
        }
        await OpenAsync(key);
        return null;
    }

    public async Task LoadMoreListAsync()
    {
        if (SelectedKey is not { } key || Value is not RedisValue.List { } l || l.Items.Count >= l.Total) return;
        try
        {
            var start = l.Items.Count;
            var more = await AppState.Bg(() => Session.ListAsync(key, start, ListPageSize));
            Value = new RedisValue.List(l.Items.Concat(more).ToList(), l.Total);
        }
        catch (Exception e)
        {
            ValueError = App?.LogError(e, LogSource.Query, Tr("[%@] 값 조회 실패", Name));
        }
    }

    public async Task LoadMoreZsetAsync()
    {
        if (SelectedKey is not { } key || Value is not RedisValue.Zset { } z || z.Items.Count >= z.Total) return;
        try
        {
            var start = z.Items.Count;
            var more = await AppState.Bg(() => Session.ZsetAsync(key, start, ListPageSize, reverse: true));
            Value = new RedisValue.Zset(z.Items.Concat(more).ToList(), z.Total);
        }
        catch (Exception e)
        {
            ValueError = App?.LogError(e, LogSource.Query, Tr("[%@] 값 조회 실패", Name));
        }
    }

    // MARK: - Workbench (콘솔)

    public const string DefaultWorkbench = "// 한 줄에 명령 하나. Ctrl+Enter 실행 (선택 영역이 있으면 그 줄만)\n// 예: RPUSH queue:mail m1 m2\nPING\n";
    private string WorkbenchKey => "GodDB.redis.workbench." + ProfileId.ToString().ToUpperInvariant();

    public ObservableCollection<RedisConsoleEntry> Console { get; } = [];
    public List<string> History { get; } = [];
    [ObservableProperty] private bool _consoleRunning;
    /// 위험한 명령 확인 대기 (한 줄)
    [ObservableProperty] private IReadOnlyList<string>? _pendingDangerous;
    /// 위험한 명령이 섞인 실행 확인 대기 (여러 줄)
    [ObservableProperty] private IReadOnlyList<string>? _pendingScript;
    /// Workbench 편집기 글 (접속마다 기억)
    [ObservableProperty] private string _workbench;

    partial void OnWorkbenchChanged(string value) => Prefs.Set(WorkbenchKey, value);

    /// 편집기 끝에 한 줄 넣기 (이전 명령 · 결과에서 다시 넣기)
    public void InsertIntoWorkbench(string command) =>
        Workbench += (Workbench.EndsWith('\n') || Workbench.Length == 0 ? "" : "\n") + command + "\n";

    /// 커서가 있는 줄의 명령 안내 (명령이면 인자 안내, 입력 중이면 이어질 명령들)
    public static string? HintFor(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var start = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var end = text.IndexOf('\n', caret);
        if (end < 0) end = text.Length;
        var line = start <= end ? text[start..end] : "";
        var trimmed = line.Trim(' ', '\t', '\r');
        var first = trimmed.Split(' ', 2)[0];
        if (first.Length == 0 || first.StartsWith("//", StringComparison.Ordinal) || first.StartsWith('#')) return null;
        var upper = first.ToUpperInvariant();
        if (RedisCommands.Hints.TryGetValue(upper, out var h)) return upper + " " + h;
        var c = RedisCommands.Complete(first);
        return c.Count == 0 ? null : string.Join("  ", c.Take(8));
    }

    /// 선택한 글이 있으면 그 줄들, 없으면 전체 (Mac textToRun)
    public static string TextToRun(string text, int selectionStart, int selectionEnd)
    {
        var a = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, text.Length);
        var b = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, text.Length);
        return b > a ? text[a..b] : text;
    }

    /// 위험한 명령 실행 확인 (확인이면 true)
    private async Task<bool> ConfirmDangerousAsync(IEnumerable<string> lines)
    {
        if (App == null) return false;
        return await App.Dialogs.ConfirmAsync(Tr("위험할 수 있는 명령이에요"),
            Tr("%@\n\n전체 데이터를 지우거나 서버를 멈추거나(KEYS 는 운영 서버를 잠시 멈출 수 있어요) 설정을 바꾸는 명령이에요. 정말 실행할까요?",
               string.Join("\n", lines)),
            Tr("실행"), Tr("취소"), destructive: true);
    }

    /// Workbench: 여러 줄을 차례로 실행. 위험한 명령이 있으면 먼저 확인한다.
    public async Task RunScriptAsync(string text, bool confirmed = false)
    {
        var lines = RedisText.ScriptLines(text);
        if (lines.Count == 0) return;
        if (!confirmed && lines.Any(l => RedisText.SplitArgs(l) is { } a && RedisCommands.IsDangerous(a)))
        {
            PendingScript = lines;
            var danger = lines.Where(l => RedisText.SplitArgs(l) is { } a && RedisCommands.IsDangerous(a));
            var ok = await ConfirmDangerousAsync(danger);
            PendingScript = null;
            if (!ok) return;
        }
        foreach (var line in lines) await RunConsoleAsync(line, confirmed: true);
    }

    public async Task RunConsoleAsync(string line, bool confirmed = false)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return;
        if (RedisText.SplitArgs(trimmed) is not { Count: > 0 } args)
        {
            Console.Add(new RedisConsoleEntry(trimmed, Tr("따옴표가 닫히지 않았어요."), true, 0, Db));
            return;
        }
        if (History.Count == 0 || History[^1] != trimmed) History.Add(trimmed);
        if (History.Count > 500) History.RemoveRange(0, History.Count - 500);
        if (!confirmed && RedisCommands.IsDangerous(args))
        {
            PendingDangerous = args;
            var ok = await ConfirmDangerousAsync([RedisFormat.Display(args)]);
            PendingDangerous = null;
            if (!ok) return;
        }
        ConsoleRunning = true;
        var start = DateTime.Now;
        var usedDb = Db;
        try
        {
            var v = await AppState.Bg(() => Session.ConsoleAsync(args));
            var ms = (int)(DateTime.Now - start).TotalMilliseconds;
            var isError = v.IsError;
            var output = v.Formatted();
            Console.Add(new RedisConsoleEntry(trimmed, output, isError, ms, usedDb));
            Db = Session.Db;
            var record = new AuditRecord(AuditRecord.Classify(args), $"{Name} db{usedDb}",
                                         isError ? "실패: " + (output.Length > 200 ? output[..200] : output) : $"Workbench · {ms}ms", trimmed);
            var shortCmd = trimmed.Length > 120 ? trimmed[..120] : trimmed;
            if (isError)
                App?.Log(LogLevel.Warning, LogSource.Query, $"[{Name} db{usedDb}] {shortCmd}", detail: output, audit: record);
            else
                App?.Log(LogLevel.Success, LogSource.Query, $"[{Name} db{usedDb}] {shortCmd} · {ms}ms", detail: trimmed, audit: record);
            if (!RedisCommands.IsReadOnly(args) && SelectedKey is { } key && args.Contains(key)) await ReloadValueAsync();
        }
        catch (Exception e)
        {
            var msg = App?.LogError(e, LogSource.Query, $"[{Name}] {args[0].ToUpperInvariant()}", sql: trimmed,
                                    audit: new AuditRecord(AuditRecord.Classify(args), $"{Name} db{usedDb}", Statement: trimmed))
                      ?? ErrorText.Describe(e).Message;
            Console.Add(new RedisConsoleEntry(trimmed, msg, true, 0, usedDb));
        }
        finally
        {
            ConsoleRunning = false;
        }
        while (Console.Count > 1000) Console.RemoveAt(0);
    }

    // MARK: - Pub/Sub

    [ObservableProperty] private RedisSubscriber? _subscriber;
    private CancellationTokenSource? _subscriberCts;
    public ObservableCollection<string> Channels { get; } = [];
    public ObservableCollection<string> Patterns { get; } = [];
    public ObservableCollection<RedisPubSubMessage> Messages { get; } = [];
    [ObservableProperty] private bool _pubsubPaused;
    [ObservableProperty] private IReadOnlyList<RedisChannelCount> _activeChannels = [];

    private async Task<RedisSubscriber> EnsureSubscriberAsync()
    {
        if (Subscriber is { } existing) return existing;
        var s = await AppState.Bg(() => Session.SubscriberAsync());
        Subscriber = s;
        var cts = new CancellationTokenSource();
        _subscriberCts = cts;
        _ = ReadMessages(s, cts.Token);
        return s;
    }

    private async Task ReadMessages(RedisSubscriber s, CancellationToken ct)
    {
        try
        {
            await foreach (var m in s.Messages.ReadAllAsync(ct))
            {
                if (PubsubPaused && (m.Kind == "message" || m.Kind == "pmessage")) continue;
                Messages.Add(m);
                while (Messages.Count > 5000) Messages.RemoveAt(0);
            }
        }
        catch (OperationCanceledException) { return; }
        catch { }
        // 연결이 끊김
        if (ReferenceEquals(Subscriber, s))
        {
            Subscriber = null;
            Channels.Clear();
            Patterns.Clear();
        }
    }

    /// "news, evt:*" → 채널과 패턴으로 나눠 구독
    public async Task SubscribeAsync(string text)
    {
        var (chans, pats) = RedisFormat.SplitSubscribeTargets(text);
        var items = chans.Concat(pats).ToList();
        if (items.Count == 0) return;
        try
        {
            var s = await EnsureSubscriberAsync();
            var newChans = chans.Where(c => !Channels.Contains(c)).Distinct().ToList();
            var newPats = pats.Where(p => !Patterns.Contains(p)).Distinct().ToList();
            s.Subscribe(newChans);
            s.PSubscribe(newPats);
            foreach (var c in newChans) Channels.Add(c);
            foreach (var p in newPats) Patterns.Add(p);
            App?.Log(LogLevel.Info, LogSource.Query, Tr("[%@] 구독 · %@", Name, string.Join(", ", items)),
                     audit: new AuditRecord(AuditAction.Read, AuditTarget, "구독", "SUBSCRIBE " + string.Join(" ", items)));
        }
        catch (Exception e)
        {
            App?.LogError(e, LogSource.Query, Tr("[%@] 구독 실패", Name));
        }
    }

    public void Unsubscribe(string item)
    {
        if (Channels.Contains(item))
        {
            Subscriber?.Unsubscribe([item]);
            Channels.Remove(item);
        }
        else
        {
            Subscriber?.PUnsubscribe([item]);
            Patterns.Remove(item);
        }
    }

    public async Task UnsubscribeAllAsync()
    {
        _subscriberCts?.Cancel();
        _subscriberCts = null;
        var s = Subscriber;
        Subscriber = null;
        Channels.Clear();
        Patterns.Clear();
        if (s != null) await AppState.Bg(() => s.CloseAsync());
    }

    public async Task<string?> PublishAsync(string channel, string message)
    {
        var statement = RedisFormat.Display(["PUBLISH", channel, message]);
        try
        {
            var n = await AppState.Bg(() => Session.PublishAsync(channel, message));
            var ns = n.ToString(CultureInfo.InvariantCulture);
            App?.Log(LogLevel.Success, LogSource.Query, Tr("[%@] PUBLISH %@ · 받은 구독자 %@명", Name, channel, ns), detail: statement,
                     audit: new AuditRecord(AuditAction.Execute, AuditTarget, $"구독자 {ns}명", statement));
            return null;
        }
        catch (Exception e)
        {
            return App?.LogError(e, LogSource.Query, Tr("[%@] PUBLISH 실패", Name), sql: statement) ?? ErrorText.Describe(e).Message;
        }
    }

    public async Task LoadActiveChannelsAsync()
    {
        try { ActiveChannels = await AppState.Bg(() => Session.ActiveChannelsAsync()); }
        catch (Exception e) { App?.LogError(e, LogSource.Query, Tr("[%@] 활성 채널 조회 실패", Name)); }
    }

    // MARK: - Streams

    [ObservableProperty] private IReadOnlyList<string> _streamKeys = [];
    [ObservableProperty] private string? _stream;
    public ObservableCollection<RedisStreamEntry> StreamEntries { get; } = [];
    [ObservableProperty] private bool _streamHasMore;
    [ObservableProperty] private IReadOnlyList<RedisPair> _streamInfo = [];
    [ObservableProperty] private IReadOnlyList<RedisStreamGroup> _groups = [];
    [ObservableProperty] private string? _group;
    [ObservableProperty] private IReadOnlyList<RedisStreamConsumer> _consumers = [];
    [ObservableProperty] private IReadOnlyList<RedisPendingEntry> _pending = [];
    [ObservableProperty] private bool _tailing;
    [ObservableProperty] private string? _streamError;
    private RedisStreamTail? _tail;
    private CancellationTokenSource? _tailCts;

    public async Task LoadStreamKeysAsync()
    {
        try
        {
            StreamKeys = (await AppState.Bg(() => Session.ScanKeysAsync("*", "stream", "0", 2000))).Keys.Order(StringComparer.Ordinal).ToList();
            if (Stream == null && StreamKeys.Count > 0) await OpenStreamAsync(StreamKeys[0]);
        }
        catch (Exception e)
        {
            StreamError = App?.LogError(e, LogSource.Query, Tr("[%@] 스트림 목록 조회 실패", Name));
        }
    }

    public async Task OpenStreamAsync(string key)
    {
        if (Stream != key) StopTail();
        Stream = key;
        Tab = RedisTab.Streams;
        await ReloadStreamAsync();
    }

    private void SetEntries(IEnumerable<RedisStreamEntry> entries)
    {
        StreamEntries.Clear();
        foreach (var e in entries) StreamEntries.Add(e);
    }

    public async Task ReloadStreamAsync()
    {
        if (Stream is not { } key) return;
        StreamError = null;
        try
        {
            var entries = await AppState.Bg(() => Session.StreamLatestAsync(key, 200));
            SetEntries(entries);
            StreamHasMore = entries.Count == 200;
            StreamInfo = await AppState.Bg(() => Session.StreamInfoAsync(key));
            Groups = await AppState.Bg(() => Session.StreamGroupsAsync(key));
            if (Group is { } g && !Groups.Any(x => x.Name == g)) Group = null;
            Group ??= Groups.FirstOrDefault()?.Name;
            await ReloadGroupAsync();
        }
        catch (Exception e)
        {
            StreamError = App?.LogError(e, LogSource.Query, Tr("[%@] 스트림 조회 실패", Name));
        }
    }

    public async Task LoadOlderStreamAsync()
    {
        if (Stream is not { } key || StreamEntries.Count == 0) return;
        var last = StreamEntries[^1].Id;
        try
        {
            var more = await AppState.Bg(() => Session.StreamLatestAsync(key, 200, last));
            foreach (var e in more) StreamEntries.Add(e);
            StreamHasMore = more.Count == 200;
        }
        catch (Exception e)
        {
            StreamError = App?.LogError(e, LogSource.Query, Tr("[%@] 스트림 조회 실패", Name));
        }
    }

    public async Task ReloadGroupAsync()
    {
        if (Stream is not { } key || Group is not { } g)
        {
            Consumers = [];
            Pending = [];
            return;
        }
        try
        {
            Consumers = await AppState.Bg(() => Session.StreamConsumersAsync(key, g));
            Pending = await AppState.Bg(() => Session.StreamPendingAsync(key, g));
        }
        catch (Exception e)
        {
            StreamError = App?.LogError(e, LogSource.Query, Tr("[%@] 소비자 그룹 조회 실패", Name));
        }
    }

    public async Task<string?> StreamWriteAsync(IReadOnlyList<string> args, string label)
    {
        var e = await WriteAsync(args, label);
        await ReloadStreamAsync();
        return e;
    }

    public void ToggleTail()
    {
        if (Tailing) { StopTail(); return; }
        if (Stream is not { } key) return;
        Tailing = true;
        var cts = new CancellationTokenSource();
        _tailCts = cts;
        _ = TailLoop(key, cts);
    }

    private async Task TailLoop(string key, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        RedisStreamTail? tail = null;
        try
        {
            tail = await AppState.Bg(() => Session.StreamTailAsync(key));
            if (ct.IsCancellationRequested) { tail.Dispose(); return; }
            _tail = tail;
            await foreach (var e in tail.Entries.ReadAllAsync(ct))
            {
                if (StreamEntries.Any(x => x.Id == e.Id)) continue;
                StreamEntries.Insert(0, e);
                while (StreamEntries.Count > 5000) StreamEntries.RemoveAt(StreamEntries.Count - 1);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (!ct.IsCancellationRequested) StreamError = App?.LogError(e, LogSource.Query, Tr("[%@] 실시간 보기 실패", Name));
        }
        finally
        {
            tail?.Dispose();
            if (ReferenceEquals(_tail, tail)) _tail = null;
            // 연결이 끊겨 끝났으면 꺼진 상태로 (멈춘 뒤 새로 켠 실시간 보기는 건드리지 않는다)
            if (ReferenceEquals(_tailCts, cts)) { _tailCts = null; Tailing = false; }
        }
    }

    public void StopTail()
    {
        _tailCts?.Cancel();
        _tailCts = null;
        _tail?.Dispose();
        _tail = null;
        Tailing = false;
    }

    // MARK: - 서버 정보

    /// Cluster / Sentinel 표시 ("Cluster · 마스터 3개")
    [ObservableProperty] private string _topologyText = "";
    /// Cluster 마스터 노드들 (서버 정보에서 노드 고르기)
    [ObservableProperty] private IReadOnlyList<string> _nodes = [];
    [ObservableProperty] private string? _infoNode;
    [ObservableProperty] private IReadOnlyList<RedisInfoSection> _info = [];

    public bool HasTopology => TopologyText.Length > 0;
    public string NodesTip => string.Join("\n", Nodes);
    public bool MultipleNodes => Nodes.Count > 1;
    partial void OnTopologyTextChanged(string value) => OnPropertyChanged(nameof(HasTopology));
    partial void OnNodesChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(NodesTip));
        OnPropertyChanged(nameof(MultipleNodes));
    }

    public virtual Task LoadTopologyAsync()
    {
        TopologyText = Session.TopologyText;
        Nodes = Session.MasterNodes;
        return Task.CompletedTask;
    }

    public async Task LoadInfoAsync()
    {
        try
        {
            var node = Nodes.Count > 1 ? (InfoNode ?? Nodes.FirstOrDefault()) : null;
            Info = await AppState.Bg(() => Session.InfoAsync(node: node));
        }
        catch (Exception e)
        {
            App?.LogError(e, LogSource.Schema, Tr("[%@] INFO 조회 실패", Name));
        }
    }

    /// INFO 에서 값 하나
    public string? InfoValue(string key)
    {
        foreach (var s in Info)
            if (s.Items.FirstOrDefault(p => p.Key == key) is { } p) return p.Value;
        return null;
    }

    // MARK: - 닫기 · 공통

    /// 자동 새로고침 · Pub/Sub 구독 · 실시간 보기를 멈춘다
    public virtual async Task CloseAsync()
    {
        SetAutoRefresh(false);
        StopTail();
        await UnsubscribeAllAsync();
    }

    /// Mac 의 Task { … }
    public void Fire(Func<Task> work)
    {
        if (App != null) { App.Fire(work); return; }
        _ = work();
    }
}
