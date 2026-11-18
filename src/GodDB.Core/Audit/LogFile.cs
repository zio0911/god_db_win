using System.Globalization;
using System.Text;
using GodDB.Core.Platform;

namespace GodDB.Core.Audit;

/// 감사 로그를 날짜별 파일로 남긴다. (나중에 누가 언제 어느 DB 에 무엇을 했는지 분석하는 용도)
///   %LOCALAPPDATA%\GodDB\Logs\goddb-audit-2026-11-13.tsv  (Mac: ~/Library/Logs/GodDB)
/// 한 줄 = 한 행위, 탭으로 나눈 열: 시각 · 사용자@PC · 구분 · 대상 · 결과 · 내용 · 무결성 · 서명 (엑셀로 바로 열 수 있다)
/// 감사 대상(AuditRecord)이 있는 항목만 쓴다. 화면 이동·AI 내용 같은 건 상태창에만 남는다.
/// 파일 쓰기는 별도 작업 줄에서 하므로 화면을 막지 않는다. 사내 감사 요구로 기본은 영구 보관 (지우지 않음).
/// 파일은 본인 계정만 읽을 수 있다 (Windows: ACL, 그 밖: 권한 600).
public sealed class LogFile
{
    private static readonly Lazy<LogFile> SharedLazy = new(() => new LogFile(DefaultDirectory));
    public static LogFile Shared => SharedLazy.Value;

    public static string DefaultDirectory => AppPaths.LogDirectory;

    public string Directory { get; }
    /// 보관 일수. null 이면 설정 값 (기본 영구 보관)
    public int? KeepDays { get; }

    private readonly Lock _gate = new();
    private Task _tail = Task.CompletedTask;
    private bool _cleaned;
    /// 파일별 마지막 해시 (체인 이어 쓰기용)
    private readonly Dictionary<string, string> _lastHash = [];
    /// 기기 서명 키 (기본: 플랫폼 키 또는 소프트웨어 키). 테스트는 고정 키를 넘긴다.
    private readonly Func<IDeviceSigner?> _signer;
    /// 보관 기간 설정을 읽는 곳 (기본: 사용자 설정). 테스트는 고정 값을 넘긴다.
    private readonly Func<int> _retention;

    public LogFile(string directory, int? keepDays = null, Func<int>? retention = null, Func<IDeviceSigner?>? signer = null)
    {
        Directory = directory;
        KeepDays = keepDays;
        _retention = retention ?? (() => RetentionDays);
        _signer = signer ?? AuditChain.DeviceSigner;
    }

    /// 그 날짜의 로그 파일 경로
    public string PathFor(DateTime date) => Path.Combine(Directory, $"goddb-audit-{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.tsv");

    // MARK: 설정 (앱의 설정 저장소가 시작할 때 값을 넣는다. Mac: UserDefaults)

    /// 설정 > 일반 > 로그 > 보관 기간 (일). 0 이면 영구 보관 (기본). 최소 90일.
    public static readonly IReadOnlyList<int> RetentionChoices = [0, 90, 180, 365];
    private static int _retentionDays;
    public static int RetentionDays
    {
        get => Volatile.Read(ref _retentionDays);
        set => Volatile.Write(ref _retentionDays, value == 0 ? 0 : Math.Max(90, value));
    }

    /// 설정 > 일반 > 로그 파일 남기기 (기본 켜짐)
    public static bool Enabled { get; set; } = true;

    /// 개발용 화면 그리기 실행과 화면 자동 테스트에서는 실제 감사 로그에 남기지 않는다
    public static readonly bool Suspended =
        Environment.GetCommandLineArgs().Any(a => a.StartsWith("--render-", StringComparison.Ordinal)) || AppPaths.UiTest;

    /// 보관 기간이 바뀌었을 때 바로 정리
    public void CleanNow() => Enqueue(RemoveOld);

    public void Append(LogEntry entry)
    {
        if (!Enabled || Suspended || entry.Audit == null) return;
        var text = Format(entry);
        var identity = ClientInfo.Identity;
        Enqueue(() => Write(text, entry.Date, identity));
    }

    /// 쌓인 쓰기를 모두 끝낼 때까지 기다린다. (테스트·종료용)
    public void Flush()
    {
        Task t;
        lock (_gate) t = _tail;
        t.Wait();
    }

    private void Enqueue(Action work)
    {
        lock (_gate)
        {
            _tail = _tail.ContinueWith(_ =>
            {
                try { work(); } catch { }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// 한 항목을 파일 한 줄로 (탭 구분). 실패한 행위는 결과 칸이 "실패: …".
    public static string Format(LogEntry e, string? identity = null)
    {
        if (e.Audit is not { } a) return "";
        string[] cols = [FormatTime(e.Date), identity ?? ClientInfo.Identity, a.Action.RawValue(), a.Target, a.Result, a.Statement];
        return string.Join('\t', cols.Select(c => AuditRecord.Cell(c))) + "\n";
    }

    internal static string FormatTime(DateTime d) => d.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static readonly UTF8Encoding Utf8 = new(false);

    private void Write(string text, DateTime date, string identity)
    {
        AppPaths.Ensure(Directory);
        if (!_cleaned) { _cleaned = true; RemoveOld(); }
        var path = PathFor(date);
        var name = Path.GetFileName(path);
        var signer = _signer();
        if (signer == null)
        {
            // 기기 키를 못 쓰면 무결성 칸 없이 쓴다 (검사에서 예전 형식으로 보인다)
            AppendBytes(Utf8.GetBytes(text), path, header: true);
            return;
        }
        var output = new StringBuilder();
        if (!File.Exists(path))
        {
            // 새 날짜 파일: 머리글 + 공개 키·이전 파일 연결 "체인" 줄. 이전 파일 마지막 값은 시스템 로그에도 봉인
            var (prevName, prevHash) = PreviousFileTail(name);
            if (prevName != null) AuditChain.SealToSystemLog(prevName, prevHash);
            output.Append('﻿').Append(string.Join('\t', AuditRecord.Header.Concat(AuditChain.Columns))).Append('\n');
            var link = AuditChain.LinkLine(FormatTime(date), identity, prevName, prevHash, signer);
            var sealedLink = AuditChain.Seal(AuditChain.Seed(name), link, signer);
            output.Append(link).Append('\t').Append(sealedLink.Suffix).Append('\n');
            _lastHash[name] = sealedLink.Hash;
        }
        else if (!_lastHash.ContainsKey(name))
        {
            // 무결성 칸이 없는 예전 형식 파일(업데이트한 날)은 그날은 예전 형식 그대로 이어 쓴다
            if (!HasIntegrityColumns(path))
            {
                AppendBytes(Utf8.GetBytes(text), path, header: false);
                return;
            }
            _lastHash[name] = LastHash(path) ?? AuditChain.Seed(name);
        }
        var prev = _lastHash[name];
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var s = AuditChain.Seal(prev, line, signer);
            output.Append(line).Append('\t').Append(s.Suffix).Append('\n');
            prev = s.Hash;
        }
        _lastHash[name] = prev;
        AppendBytes(Utf8.GetBytes(output.ToString()), path, header: false);
    }

    private static void AppendBytes(byte[] data, string path, bool header)
    {
        bool isNew = !File.Exists(path);
        if (header && isNew)
            data = [.. Utf8.GetBytes("﻿" + string.Join('\t', AuditRecord.Header) + "\n"), .. data];
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        // 다른 사용자가 못 읽게 (새 파일일 때)
        if (isNew) UserOnlyFile.Restrict(path);
        fs.Write(data);
    }

    internal static bool HasIntegrityColumns(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[512];
            int n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            var head = Encoding.UTF8.GetString(buf, 0, n);
            return head.Split('\n')[0].EndsWith("\t" + string.Join('\t', AuditChain.Columns), StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// 파일 마지막 줄의 무결성 값 (끝에서 두 번째 칸)
    internal static string? LastHash(string path)
    {
        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); } catch { return null; }
        var last = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        var cols = last.Split('\t');
        if (cols.Length < 2) return null;
        var h = cols[^2];
        return h.Length == 64 && h.All(char.IsAsciiHexDigit) ? h : null;
    }

    /// 이 파일보다 앞 날짜의 가장 최근 감사 로그와 그 마지막 해시
    private (string? Name, string Hash) PreviousFileTail(string name)
    {
        var prev = System.IO.Directory.GetFiles(Directory)
            .Select(Path.GetFileName)
            .Where(f => f!.StartsWith("goddb-audit-", StringComparison.Ordinal) && f.EndsWith(".tsv", StringComparison.Ordinal)
                        && string.CompareOrdinal(f, name) < 0)
            .OrderBy(f => f, StringComparer.Ordinal)
            .LastOrDefault();
        if (prev == null) return (null, AuditChain.Zero);
        return (prev, LastHash(Path.Combine(Directory, prev)) ?? AuditChain.Zero);
    }

    private void RemoveOld()
    {
        // 생성할 때 정한 값(테스트) 이 없으면 설정 값. 0 이면 영구 보관
        var setting = _retention();
        int? keepDays = KeepDays ?? (setting > 0 ? setting : null);
        if (keepDays is not { } days || !System.IO.Directory.Exists(Directory)) return;
        var cutoff = DateTime.Now.AddDays(-days);
        foreach (var f in System.IO.Directory.GetFiles(Directory))
        {
            var file = Path.GetFileName(f);
            var ext = Path.GetExtension(f);
            if (!file.StartsWith("goddb-", StringComparison.Ordinal) || ext is not (".log" or ".tsv")) continue;
            var stem = Path.GetFileNameWithoutExtension(f);
            var day = stem.StartsWith("goddb-audit-", StringComparison.Ordinal) ? stem["goddb-audit-".Length..] : stem["goddb-".Length..];
            if (DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) && d < cutoff)
            {
                try { File.Delete(f); } catch { }
            }
        }
    }
}
