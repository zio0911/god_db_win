using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using static GodDB.Core.L10n;

namespace GodDB.Core.Ai;

/// 로컬 Ollama 상태
public abstract record OllamaStatus
{
    /// 실행 중, 설치된 모델 목록
    public sealed record Running(IReadOnlyList<OllamaModel> Models) : OllamaStatus;
    /// 설치는 됐지만 서버가 꺼져 있음
    public sealed record InstalledNotRunning : OllamaStatus;
    /// 설치 안 됨
    public sealed record NotInstalled : OllamaStatus;

    public bool IsRunning => this is Running;
    /// 실행 중일 때의 모델 목록 (아니면 빈 목록)
    public IReadOnlyList<OllamaModel> RunningModels => this is Running r ? r.Models : [];
}

/// Ollama 대화 메시지 한 개 (Mac 판 OllamaClient.Message). 쿼리 튜닝(Tuning.cs)을 빼면서 여기로 옮겼다.
public sealed record OllamaMessage(string Role, string Content);

public sealed record OllamaModel(string Name, double SizeGB, string ParameterSize)
{
    public string Id => Name;
}

public enum OllamaErrorKind
{
    NotRunning,
    NoModel,
    Http,
    /// 이 PC 밖의 주소 — 구조 · SQL · 샘플 값이 다른 서버로 가므로 요청하지 않는다 (Body = 호스트)
    RemoteBlocked,
}

/// Mac 판 OllamaError
public sealed class OllamaException : Exception
{
    public OllamaErrorKind Kind { get; }
    public int StatusCode { get; }
    public string Body { get; }

    public OllamaException(OllamaErrorKind kind, int statusCode = 0, string body = "") : base(Describe(kind, statusCode, body))
    {
        Kind = kind;
        StatusCode = statusCode;
        Body = body;
    }

    private static string Describe(OllamaErrorKind kind, int code, string body) => kind switch
    {
        OllamaErrorKind.NotRunning => Tr("Ollama 가 실행 중이 아니에요. 설정 > AI 에서 확인해 주세요."),
        OllamaErrorKind.NoModel => Tr("AI 에 쓸 모델을 설정 > AI 에서 골라 주세요."),
        OllamaErrorKind.RemoteBlocked => Tr("AI 는 이 PC 의 Ollama 만 쓸 수 있어요 (%@ 는 다른 서버예요). 설정 > AI 에서 주소를 localhost 로 바꿔 주세요.", body),
        _ => Tr("Ollama 오류 (HTTP %@): %@", code.ToString(CultureInfo.InvariantCulture), body),
    };
}

/// 대화 응답 조각. 생각(thinking) 조각과 본문(content) 조각을 구분한다.
public abstract record ChatChunk
{
    public sealed record Thinking(string Text) : ChatChunk;
    public sealed record Content(string Text) : ChatChunk;
}

/// 로컬 Ollama HTTP API (기본 http://localhost:11434). 데이터는 이 PC 밖으로 나가지 않는다.
/// 보내는 것은 스키마·요약 문맥뿐 — 데이터 행, 비밀번호, 접속 정보는 보내지 않는다 (만드는 쪽에서 지킨다).
public sealed class OllamaClient
{
    public const string DefaultUrl = "http://localhost:11434";
    public const string DownloadUrl = "https://ollama.com/download";

    // 응답을 스트리밍으로 오래 받으므로 HttpClient 자체 제한은 끄고 요청마다 제한을 건다.
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _http;

    public Uri BaseUrl { get; }

    /// 모델 목록 요청 제한 (Mac: timeoutInterval 3)
    internal TimeSpan TagsTimeout { get; init; } = TimeSpan.FromSeconds(3);
    /// 대화 응답이 이 시간 동안 한 줄도 오지 않으면 실패 (Mac URLSession timeoutInterval 600 과 같은 "쉬는 시간" 제한)
    internal TimeSpan ChatIdleTimeout { get; init; } = TimeSpan.FromSeconds(600);

    /// handler 는 테스트용 (가짜 응답)
    public OllamaClient(Uri? baseUrl = null, HttpMessageHandler? handler = null)
    {
        BaseUrl = baseUrl ?? new Uri(DefaultUrl);
        _http = handler == null ? SharedHttp : new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    // MARK: 설치 · 실행

    /// Ollama 실행 파일 후보 (Windows 공식 설치 위치, 그다음 PATH)
    /// Mac 은 Homebrew · /Applications/Ollama.app 경로를 본다.
    public static IEnumerable<string> BinaryPaths
    {
        get
        {
            yield return Path.Combine(InstallDirectory, OperatingSystem.IsWindows() ? "ollama.exe" : "ollama");
            var names = OperatingSystem.IsWindows() ? new[] { "ollama.exe" } : ["ollama"];
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                foreach (var n in names)
                    yield return Path.Combine(dir.Trim('"'), n);
        }
    }

    /// 공식 설치 프로그램이 까는 폴더 (%LOCALAPPDATA%\Programs\Ollama)
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama");

    /// 트레이 앱 (있으면 이것을 띄운다 — Mac 의 Ollama.app 에 해당)
    private static string TrayAppPath => Path.Combine(InstallDirectory, "ollama app.exe");

    public static bool IsInstalled => FindBinary() != null;

    private static string? FindBinary()
    {
        foreach (var p in BinaryPaths)
        {
            try { if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    /// Ollama 서버를 띄운다. (공식 트레이 앱이 있으면 앱을, 없으면 `ollama serve` 를 창 없이)
    public static void Start()
    {
        if (OperatingSystem.IsWindows() && File.Exists(TrayAppPath))
        {
            Process.Start(new ProcessStartInfo(TrayAppPath) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
            return;
        }
        var path = FindBinary() ?? throw new OllamaException(OllamaErrorKind.NotRunning);
        var psi = new ProcessStartInfo(path, "serve")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process.Start(psi)?.Dispose();
    }

    // MARK: 상태 · 모델

    public async Task<OllamaStatus> StatusAsync(CancellationToken ct = default)
    {
        try { return new OllamaStatus.Running(await ModelsAsync(ct)); }
        catch when (!ct.IsCancellationRequested) { }
        return IsInstalled ? new OllamaStatus.InstalledNotRunning() : new OllamaStatus.NotInstalled();
    }

    /// 이 PC 밖이면 요청 전에 막는다 (설정을 거치지 않고 주소가 들어와도)
    internal void EnsureLocal()
    {
        if (!AISettings.IsLocalUrl(BaseUrl.ToString()))
            throw new OllamaException(OllamaErrorKind.RemoteBlocked, body: BaseUrl.Host.Length > 0 ? BaseUrl.Host : BaseUrl.ToString());
    }

    public async Task<List<OllamaModel>> ModelsAsync(CancellationToken ct = default)
    {
        EnsureLocal();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TagsTimeout);
        using var response = await _http.GetAsync(Endpoint("api/tags"), cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        if ((int)response.StatusCode != 200) throw new OllamaException(OllamaErrorKind.Http, (int)response.StatusCode, body);
        return ParseTags(body);
    }

    internal static List<OllamaModel> ParseTags(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<OllamaModel>();
        foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
        {
            var name = m.GetProperty("name").GetString() ?? throw new JsonException("name");
            long size = m.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
            var param = m.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object
                        && d.TryGetProperty("parameter_size", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            list.Add(new OllamaModel(name, size / 1_000_000_000.0, param));
        }
        return list;
    }

    // MARK: 대화

    /// 스트리밍 대화 (/api/chat, 줄마다 JSON 하나). `think` 옵션은 보내지 않는다
    /// (일부 모델은 false 를 주면 생각이 본문에 섞인다) — 생각/본문을 따로 받는다.
    public async IAsyncEnumerable<ChatChunk> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, double temperature = 0.2,
                                                       [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureLocal();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(ChatIdleTimeout);
        var body = JsonSerializer.Serialize(new
        {
            model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            stream = true,
            options = new Dictionary<string, double> { ["temperature"] = temperature },
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("api/chat"))
        {
            Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        using var response = await Guard(() => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token), ct);
        await using var stream = await response.Content.ReadAsStreamAsync(idle.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if ((int)response.StatusCode != 200)
        {
            // Mac 은 응답 줄들을 줄바꿈 없이 이어 붙인다
            var text = await Guard(() => reader.ReadToEndAsync(idle.Token), ct);
            throw new OllamaException(OllamaErrorKind.Http, (int)response.StatusCode, text.Replace("\r", "").Replace("\n", ""));
        }
        while (true)
        {
            var line = await Guard(() => reader.ReadLineAsync(idle.Token).AsTask(), ct);
            if (line == null) break;
            idle.CancelAfter(ChatIdleTimeout);
            if (ParseLine(line) is not { } parsed) continue;
            if (parsed.Error != null) throw new OllamaException(OllamaErrorKind.Http, 500, parsed.Error);
            if (!string.IsNullOrEmpty(parsed.Thinking)) yield return new ChatChunk.Thinking(parsed.Thinking);
            if (!string.IsNullOrEmpty(parsed.Content)) yield return new ChatChunk.Content(parsed.Content);
            if (parsed.Done) break;
        }
    }

    /// 대화 전체를 받아 본문만 돌려준다 (생각 조각은 버림)
    public async Task<string> ChatTextAsync(string model, IReadOnlyList<OllamaMessage> messages, double temperature = 0.2,
                                            CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in ChatAsync(model, messages, temperature, ct))
            if (chunk is ChatChunk.Content c) sb.Append(c.Text);
        return sb.ToString();
    }

    /// 모델 확인용 짧은 요청: "Reply with OK" (스트리밍 없이, 최대 256 토큰). 실패하거나 답이 비면 OllamaException 을 던진다.
    /// 토큰을 너무 적게 받으면 고장 난 모델도 오류 전에 끝나 통과하므로(빈 답) 조금 넉넉히 받고, 빈 답은 실패로 본다.
    public async Task PingAsync(string model, double timeoutSeconds, CancellationToken ct = default)
    {
        EnsureLocal();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var body = JsonSerializer.Serialize(new
        {
            model,
            messages = new[] { new { role = "user", content = ModelProbe.Prompt } },
            stream = false,
            options = new Dictionary<string, double> { ["num_predict"] = ModelProbe.MaxTokens, ["temperature"] = 0 },
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("api/chat"))
        {
            Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        using var response = await Guard(() => _http.SendAsync(request, cts.Token), ct);
        var text = await Guard(() => response.Content.ReadAsStringAsync(cts.Token), ct);
        if ((int)response.StatusCode != 200) throw new OllamaException(OllamaErrorKind.Http, (int)response.StatusCode, text);
        string? error = null, content = null, thinking = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) error = e.GetString();
                if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object)
                {
                    if (m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String) content = c.GetString();
                    if (m.TryGetProperty("thinking", out var t) && t.ValueKind == JsonValueKind.String) thinking = t.GetString();
                }
            }
        }
        catch (JsonException) { }
        if (error != null) throw new OllamaException(OllamaErrorKind.Http, 500, error);
        if (((content ?? "") + (thinking ?? "")).Trim().Length == 0)
            throw new OllamaException(OllamaErrorKind.Http, 200, Tr("빈 답을 돌려줬어요"));
    }

    internal sealed record Line(string? Content, string? Thinking, bool Done, string? Error);

    /// NDJSON 한 줄. 읽을 수 없는 줄은 null (건너뜀)
    internal static Line? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? content = null, thinking = null, error = null;
            var done = false;
            if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
            {
                content = Str(msg, "content");
                thinking = Str(msg, "thinking");
            }
            if (root.TryGetProperty("done", out var d) && d.ValueKind is JsonValueKind.True or JsonValueKind.False) done = d.GetBoolean();
            error = Str(root, "error");
            return new Line(content, thinking, done, error);
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private Uri Endpoint(string path) => new(new Uri(BaseUrl.ToString().TrimEnd('/') + "/"), path);

    /// 쉬는 시간 제한으로 끊긴 것은 시간 초과로, 사용자가 취소한 것은 그대로 취소로
    private static async Task<T> Guard<T>(Func<Task<T>> op, CancellationToken user)
    {
        try { return await op(); }
        catch (OperationCanceledException) when (!user.IsCancellationRequested)
        {
            throw new TimeoutException("Ollama did not respond in time.");
        }
    }
}

/// AI(Ollama) 설정 창구. 주소 · 모델은 AISettings 와 같은 값을 쓴다 (저장 키는 예전 이름 그대로라 기존 설정 유지).
/// Mac 1.4.0 은 쿼리 튜닝을 빼고 TuningSettings 를 AISettings 로 바꿨다 — Windows 도 쿼리 튜닝과 켜기/끄기 설정을 뺐다.
public static class TuningSettings
{
    public const string UrlKey = AISettings.UrlKey;
    public const string ModelKey = AISettings.ModelKey;
    /// AI 어시스턴트 켜기 (Mac: AppSettings.assistantKey)
    public const string AssistantEnabledKey = "GodDB.assistant.enabled";

    public static string Url
    {
        get => AISettings.Url;
        set => AISettings.Url = value;
    }

    /// 비어 있으면 AISettings.DefaultModel 로 고른다
    public static string Model
    {
        get => AISettings.Model;
        set => AISettings.Model = value;
    }

    public static bool AssistantEnabled
    {
        get => Prefs.Get(AssistantEnabledKey, true);
        set => Prefs.Set(AssistantEnabledKey, value);
    }

    public static OllamaClient Client => AISettings.Client;

    /// 이 PC 안의 주소인지 (아니면 스키마 정보가 다른 서버로 간다)
    public static bool IsLocal => AISettings.IsLocal;

    public static bool IsLocalUrl(string url) => AISettings.IsLocalUrl(url);

    /// 주소가 이 PC 밖일 때 설정 화면에 보일 경고 (Mac 1.4.0: 다른 서버 주소는 저장하지 않는다)
    public static string NonLocalWarning => Tr("AI 는 이 PC 의 Ollama 만 쓸 수 있어요. 다른 서버로는 구조 · SQL · 샘플 값이 나가므로 막아 두었어요. 이 주소는 저장되지 않아요.");

    public static IReadOnlyList<string> RecommendedModels => AISettings.RecommendedModels;

    /// 쓸 모델: 설정에 고른 모델, 비어 있으면 설치된 추천 모델 → 설치된 첫 모델 (없으면 "")
    public static string ResolveModel(IReadOnlyList<OllamaModel> installed, string? chosen = null)
    {
        var m = chosen ?? Model;
        return m.Length == 0 ? AISettings.DefaultModel(installed.Select(x => x.Name).ToList()) ?? "" : m;
    }

    /// 상태 한 줄 (설정 화면용)
    public static string StatusText(OllamaStatus? status) => status switch
    {
        OllamaStatus.Running r => Tr("실행 중 · 모델 %@개", r.Models.Count),
        OllamaStatus.InstalledNotRunning => Tr("설치됐지만 꺼져 있어요"),
        OllamaStatus.NotInstalled => Tr("설치되지 않았어요"),
        _ => Tr("확인 중…"),
    };
}
