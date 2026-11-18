using CommunityToolkit.Mvvm.ComponentModel;
using GodDB.Core;
using GodDB.Core.Drivers;
using GodDB.Core.Profiles;
using GodDB.Core.Results;
using GodDB.Core.Schema;
using GodDB.Core.Sql;
using static GodDB.Core.L10n;

namespace GodDB.App.ViewModels;

/// DDL·정의를 연 탭이 어디서 왔는지 (생성문 새로고침용)
public abstract record TabSource
{
    public sealed record Table(string Name) : TabSource;
    public sealed record Routine(RoutineMeta Meta) : TabSource;
}

/// 테이블 이름 변경 / 삭제 요청 (확인 창)
public sealed record TableActionRequest(TableActionKind Kind, Guid ProfileId, string Table)
{
    public Guid Id { get; } = Guid.NewGuid();
}

public enum TableActionKind { Rename, Drop }

public enum MainMode { Query, Compare, Modeling }

public static class MainModeExtensions
{
    /// Mac MainMode.rawValue (한국어)
    public static string RawValue(this MainMode m) => m switch
    {
        MainMode.Query => "쿼리",
        MainMode.Compare => "스키마 비교",
        _ => "모델링",
    };

    public static string Title(this MainMode m) => Tr(m.RawValue());
}

public enum ConnectionStateKind { Disconnected, Connecting, Connected, Failed }

/// 접속 하나의 연결 상태 (Mac: enum ConnectionState { disconnected, connecting, connected, failed(String) })
public sealed record ConnectionState(ConnectionStateKind Kind, string? Message = null)
{
    public static readonly ConnectionState Disconnected = new(ConnectionStateKind.Disconnected);
    public static readonly ConnectionState Connecting = new(ConnectionStateKind.Connecting);
    public static readonly ConnectionState Connected = new(ConnectionStateKind.Connected);
    public static ConnectionState Failed(string message) => new(ConnectionStateKind.Failed, message);

    public bool IsConnected => Kind == ConnectionStateKind.Connected;
    public bool IsFailed => Kind == ConnectionStateKind.Failed;
}

/// 접속 하나의 연결 상태와 스키마 정보. 여러 접속을 동시에 열어 둘 수 있다.
/// (Mac 은 값 타입이라 통째로 바꾸지만, 여기서는 AppState.UpdateConnection 으로만 고치고 알림을 보낸다)
public sealed class ConnectionInfo
{
    public ConnectionState State { get; set; } = ConnectionState.Disconnected;
    public DbSession? Session { get; set; }
    public List<string> Databases { get; set; } = [];
    public string CurrentDatabase { get; set; } = "";
    public List<TableMeta> Tables { get; set; } = [];
    public List<RoutineMeta> Routines { get; set; } = [];
    public bool SchemaLoading { get; set; }
    /// 자동 커밋 / 열린 트랜잭션 상태 (세션에서 읽어 둔 값)
    public TransactionState Tx { get; set; } = new();
}

/// 비밀번호 입력 요청 (저장 안 함 설정이거나, 저장된 비밀번호가 틀렸을 때)
public sealed record PasswordPrompt(ConnectionProfile Profile, string? Message)
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// 커밋하지 않은 트랜잭션이 있을 때 묻는 이유
public enum TxPromptReason { EnableAutoCommit, Disconnect, Reconnect, Delete }

public static class TxPromptReasonExtensions
{
    /// "%@ 전에 커밋하거나 롤백해 주세요" 에 들어갈 말
    public static string ActionText(this TxPromptReason r) => r switch
    {
        TxPromptReason.EnableAutoCommit => Tr("자동 커밋을 켜기"),
        TxPromptReason.Disconnect => Tr("연결을 끊기"),
        TxPromptReason.Reconnect => Tr("바뀐 설정으로 다시 연결하기"),
        _ => Tr("접속을 삭제하기"),
    };
}

public sealed record TxPrompt(Guid ProfileId, TxPromptReason Reason)
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// 접속 그룹 이름 입력창 (새 그룹으로 옮기기 / 그룹 이름 바꾸기)
public abstract record GroupPromptAction
{
    public sealed record Move(Guid ProfileId) : GroupPromptAction;
    public sealed record Rename(string OldName) : GroupPromptAction;
}

public sealed record GroupPrompt(GroupPromptAction Action)
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Initial => Action is GroupPromptAction.Rename r ? r.OldName : "";
}

/// 가져오기 창 요청 (파일을 읽은 뒤)
public sealed record ImportRequest(string FileName, ConnectionBundle Bundle)
{
    public Guid Id { get; } = Guid.NewGuid();
    /// 접속별 안내 (DataGrip 에서 가져올 때)
    public Dictionary<Guid, IReadOnlyList<string>> Notes { get; init; } = [];
}

/// 적용하지 않은 결과 편집이 사라지는 일(탭 닫기 · 페이지 이동 · 결과 바꾸기 · 정렬) 전에 묻는다
public sealed record DiscardEditsPrompt(int Count, Action Action)
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// 결과 편집 적용 확인 창 요청
public sealed record EditApplyRequest(Guid TabId, IReadOnlyList<string> Statements)
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// 결과 내보내기 설정 창 요청 (Mac ExportRequest 의 tabID + Core ExportRequest)
public sealed record TabExportRequest(Guid TabId, ExportRequest Request)
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// 쿼리 탭. 탭마다 어느 접속 / 어느 DB(스키마)에서 실행할지 정해져 있다.
public sealed partial class QueryTab : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();
    /// 이 탭이 쓰는 접속
    public Guid ProfileId { get; }

    public QueryTab(Guid profileId, string database, int number, string? customTitle = null, string text = "")
    {
        ProfileId = profileId;
        _database = database;
        _number = number;
        _customTitle = customTitle;
        _text = text;
    }

    /// 이 탭이 쓰는 DB (PostgreSQL 은 스키마)
    [ObservableProperty] private string _database;
    /// 접속별 쿼리 번호 (쿼리 1, 쿼리 2 …)
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] private int _number;
    /// DDL·정의 보기처럼 특별한 탭의 이름 (없으면 "쿼리 N")
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] private string? _customTitle;
    /// DDL·정의 탭이면 원본 (생성문 새로고침)
    [ObservableProperty] private TabSource? _source;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDirty))] private string _text;
    /// 화면에 보이는 결과 (Results 중 하나)
    [ObservableProperty] private QueryResult? _result;
    /// 여러 문장을 실행했을 때 문장별 결과
    [ObservableProperty] private IReadOnlyList<QueryResult> _results = [];
    [ObservableProperty] private int _resultIndex;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _running;
    /// 실행을 시작한 시각 (경과 시간 · 실행 시간 제한)
    [ObservableProperty] private DateTime? _runStarted;
    /// 페이지 이동 / 전체 행 수 세기 중 (결과 표는 그대로 보여준다)
    [ObservableProperty] private bool _pageLoading;
    /// "제한 없음" 결과가 메모리 상한에 닿음
    [ObservableProperty] private bool _appendCapped;
    /// 파일로 내보내는 중
    [ObservableProperty] private ExportProgress? _exportProgress;
    /// 결과 편집 대기 중인 변경 (보고 있는 결과 기준). 안을 고친 뒤에는 NotifyEditsChanged() 를 부른다.
    [ObservableProperty] private PendingEdits _edits = new();
    [ObservableProperty] private string? _lastSql;
    /// 실행 계획(EXPLAIN) 결과인지
    [ObservableProperty] private bool _isPlan;
    [ObservableProperty] private string? _planTree;
    /// 연 .sql 파일 (저장하면 이 파일에 쓴다)
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDirty))] private string? _filePath;
    /// 파일에 마지막으로 저장한 내용 (바뀌었는지 표시)
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDirty))] private string? _savedText;
    /// 연 파일의 원래 줄바꿈. 편집기 안은 LF 로 두고, 저장할 때 이 줄바꿈으로 쓴다 (새 파일은 LF — Mac 은 글 그대로 저장)
    public string FileLineEnding { get; set; } = "\n";

    public string Title => CustomTitle ?? Tr("쿼리 %@", Number);
    public bool IsDirty => FilePath != null && Text != SavedText;

    /// "제한 없음" 결과에 행을 이어 붙였을 때 (start = 붙인 첫 행 번호, count = 붙인 행 수)
    public event Action<QueryTab, int, int>? RowsAppended;

    /// Result 안의 값(TotalCount, Rows 등)을 그 자리에서 바꾼 뒤 화면에 알린다
    public void NotifyResultChanged() => OnPropertyChanged(nameof(Result));
    public void NotifyEditsChanged() => OnPropertyChanged(nameof(Edits));
    internal void RaiseRowsAppended(int start, int count) => RowsAppended?.Invoke(this, start, count);
}

/// 실행 전에 확인할 것: 편집기 전체 실행 · 되돌리기 어려운 문장 · 적용하지 않은 결과 편집 · 운영 접속의 변경 문
public sealed record RunConfirm
{
    public Guid Id { get; } = Guid.NewGuid();
    public required IReadOnlyList<string> Statements { get; init; }
    public bool Explain { get; init; }
    public bool WholeScript { get; init; }
    public IReadOnlyList<string> Dangerous { get; init; } = [];
    public int DiscardedEdits { get; init; }
    /// 운영 접속: 데이터를 바꾸는 문장이 있으면 항상 확인
    public bool Production { get; init; }

    private List<string> Writes => Statements.Where(s => !SqlText.IsReadOnly(s)).ToList();
    private bool ProductionWrites => Production && !Explain && Writes.Count > 0;

    public bool NeedsConfirm => WholeScript || Dangerous.Count > 0 || DiscardedEdits > 0 || ProductionWrites;

    /// 편집기에서 [실행] (선택 영역이 있으면 selected 에 그 SQL, 없으면 null 과 편집기 전체 text)
    public static RunConfirm? ForRun(string? selected, string text, int discardedEdits, bool production)
    {
        var source = selected ?? text;
        var statements = SqlText.Statements(source).Select(s => s.Sql).ToList();
        if (statements.Count == 0) return null;
        return new RunConfirm
        {
            Statements = statements,
            WholeScript = selected == null && statements.Count > 1 && statements.Any(s => !SqlText.IsReadOnly(s)),
            Dangerous = statements.Where(s => SqlText.Danger(s) != null).ToList(),
            DiscardedEdits = discardedEdits,
            Production = production,
        };
    }

    /// 실행 계획은 선택 영역 또는 커서가 있는 문장 하나
    public static RunConfirm ForExplain(string sql, int discardedEdits) =>
        new() { Statements = [sql], Explain = true, DiscardedEdits = discardedEdits };

    public string Title
    {
        get
        {
            if (Dangerous.Count > 0) return Tr("되돌리기 어려운 문장이 있어요");
            if (ProductionWrites) return Tr("운영 접속에서 데이터를 바꿔요");
            if (WholeScript) return Tr("전체 %@개 문장을 실행할까요?", Statements.Count);
            return Tr("적용하지 않은 편집이 있어요");
        }
    }

    public string ConfirmButton => WholeScript ? Tr("전체 실행") : Tr("실행");

    public string Message
    {
        get
        {
            var parts = new List<string>();
            var writes = Writes;
            if (ProductionWrites)
                parts.Add(Tr("운영 접속이에요. 데이터를 바꾸는 문장 %@개를 실행해요.", writes.Count)
                          + (Dangerous.Count == 0 ? "\n" + string.Join("\n", writes.Take(3).Select(w => "• " + AppState.OneLine(w, 50))) : ""));
            if (Dangerous.Count > 0)
            {
                var lines = Dangerous.Take(3).Select(d => "• " + Label(SqlText.Danger(d)) + " — " + AppState.OneLine(d, 50));
                parts.Add(string.Join("\n", lines) + (Dangerous.Count > 3 ? "\n…" : ""));
            }
            if (WholeScript)
                parts.Add(Tr("선택 영역이 없어서 편집기의 모든 문장(%@개, 변경 문 %@개)을 위에서부터 실행해요. 일부만 실행하려면 그 부분을 선택하세요.",
                             Statements.Count, writes.Count));
            if (DiscardedEdits > 0)
                parts.Add(Tr("결과 표에서 고치고 적용하지 않은 %@건은 사라져요.", DiscardedEdits));
            return string.Join("\n\n", parts);
        }
    }

    public static string Label(SqlDanger? d) => d switch
    {
        SqlDanger.DeleteAll => Tr("WHERE 없는 DELETE (모든 행 삭제)"),
        SqlDanger.UpdateAll => Tr("WHERE 없는 UPDATE (모든 행 변경)"),
        SqlDanger.Truncate => Tr("TRUNCATE (모든 행 삭제)"),
        SqlDanger.Drop => Tr("DROP (객체 삭제)"),
        _ => "",
    };
}
