using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using static GodDB.Core.L10n;

namespace GodDB.Core.Ai;

/// 로컬 AI(Ollama) 설정 (Mac 1.4.x AISettings). 저장 키는 예전 쿼리 튜닝 시절 이름을 그대로 써서 기존 설정이 유지된다.
public static class AISettings
{
    public const string UrlKey = "GodDB.tuning.url";
    public const string ModelKey = "GodDB.tuning.model";
    public const string DefaultUrl = OllamaClient.DefaultUrl;

    public static string Url
    {
        get => Prefs.GetString(UrlKey) ?? DefaultUrl;
        set => Prefs.Set(UrlKey, value);
    }

    /// 비어 있으면 DefaultModel(installed) 로 고른다
    public static string Model
    {
        get => Prefs.GetString(ModelKey) ?? "";
        set => Prefs.Set(ModelKey, value);
    }

    public static OllamaClient Client =>
        new(Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u : new Uri(DefaultUrl));

    /// 지금 저장된 주소가 이 PC 인지
    public static bool IsLocal => IsLocalUrl(Url);

    /// 이 PC(루프백) 주소인지: localhost, 127.0.0.0/8, ::1. 스킴은 http(s) 만.
    public static bool IsLocalUrl(string text)
    {
        var t = (text ?? "").Trim();
        // "localhost:11434" 처럼 스킴이 없으면 URL 로 보지 않는다 (Mac URL(string:) 은 scheme 을 localhost 로 읽어 거절)
        if (!t.Contains("://", StringComparison.Ordinal)) return false;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u)) return false;
        if (u.Scheme is not ("http" or "https")) return false;
        var host = u.Host.ToLowerInvariant();
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (host is "localhost" or "::1" or "0:0:0:0:0:0:0:1") return true;
        // .NET 은 "127.1" 같은 짧은 IPv4 를 고쳐 읽으므로 원문 호스트 조각으로 다시 본다
        var raw = RawHost(t);
        var parts = raw.Split('.');
        return parts.Length == 4 && parts[0] == "127"
               && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit) && int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 255);
    }

    /// "http://user@host:port/path" 의 host 원문 (소문자)
    private static string RawHost(string url)
    {
        var rest = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        if (end >= 0) rest = rest[..end];
        var at = rest.LastIndexOf('@');
        if (at >= 0) rest = rest[(at + 1)..];
        var colon = rest.LastIndexOf(':');
        if (colon >= 0) rest = rest[..colon];
        return rest.ToLowerInvariant();
    }

    /// 추천 모델 (앞이 먼저, "자동" 이면 설치된 것 중 앞의 것).
    /// Mac 기본 추천은 qwen3.8:27b-mlx 인데 MLX 는 Apple Silicon 전용 형식이라 Windows 에서는 돌지 않는다.
    /// Mac 코드에 플랫폼별 분기가 없으므로 같은 모델의 일반(GGUF) Ollama 태그인 `-mlx` 를 뗀 qwen3.8:27b 를 쓴다.
    /// 메모리 48GB 이상 qwen3.8:27b, 24GB qwen3:14b. 예전 추천(qwen3:30b · qwen2.5-coder)은 뒤에 남긴다.
    public static readonly IReadOnlyList<string> RecommendedModels = ["qwen3.8:27b", "qwen3:14b", "qwen3:30b", "qwen2.5-coder:14b", "qwen2.5-coder:32b"];

    /// 기본 추천 모델 (받기 안내에 쓴다)
    public static string DefaultRecommendedModel => RecommendedModels[0];

    /// 메모리 24GB 에 맞는 추천 모델
    public const string SmallMemoryModel = "qwen3:14b";

    /// 모델을 고르지 않았을 때 쓸 모델: 설치된 추천 모델(앞 순서) → 설치된 첫 모델
    public static string? DefaultModel(IReadOnlyList<string> installed) =>
        RecommendedModels.FirstOrDefault(installed.Contains) ?? installed.FirstOrDefault();

    /// 쓸 모델: 고른 모델, 없으면 기본 모델
    public static string? EffectiveModel(IReadOnlyList<string> installed)
    {
        var m = Model;
        return m.Length == 0 ? DefaultModel(installed) : m;
    }

    /// 추천 모델을 앞으로 (바꿀 모델 버튼 순서)
    public static List<string> RecommendedFirst(IReadOnlyList<string> names)
    {
        var rec = RecommendedModels.Where(names.Contains).ToList();
        return [.. rec, .. names.Where(n => !rec.Contains(n))];
    }
}

// MARK: - 모델 확인 (F7)

/// 모델 확인 결과
public abstract record ModelHealth
{
    public sealed record Ok : ModelHealth;
    /// 짧은 이유 (예: "prediction aborted, token repeat limit reached")
    public sealed record Failed(string Why) : ModelHealth;
    /// 제한 시간 안에 답이 없음 (모델을 처음 올리는 중일 수 있어 기억하지 않는다)
    public sealed record TimedOut : ModelHealth;

    public static readonly ModelHealth OK = new Ok();

    public bool IsOK => this is Ok;

    /// 화면에 보일 짧은 이유
    public string Reason => this switch
    {
        Ok => "OK",
        Failed f => f.Why,
        _ => Tr("%@초 안에 답이 없어요", ((int)ModelProbe.Timeout).ToString(CultureInfo.InvariantCulture)),
    };
}

/// 고른 모델이 실제로 답하는지 아주 짧은 요청으로 확인한다. 결과는 앱을 켜 둔 동안 모델별로 기억한다.
/// (어떤 모델은 "hi" 에도 HTTP 500 "token repeat limit reached" 로 실패한다 — 그런 모델로 찾기를 하면 틀린 답만 나온다)
public static class ModelProbe
{
    public const string Prompt = "Reply with OK";
    public const int MaxTokens = 256;
    public const double Timeout = 20;

    /// 확인 하나. send 는 실제로는 OllamaClient.PingAsync (시험에서는 가짜).
    public static async Task<ModelHealth> CheckAsync(Func<CancellationToken, Task> send, double timeout = Timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var work = Task.Run(() => send(cts.Token), CancellationToken.None);
        var timer = Task.Delay(TimeSpan.FromSeconds(Math.Max(0, timeout)), ct);
        Task first;
        try { first = await Task.WhenAny(work, timer); }
        catch (OperationCanceledException) { return new ModelHealth.Failed(Tr("취소했어요")); }
        if (first != work)
        {
            // 시간 초과 (또는 호출한 쪽 취소): 작업은 기다리지 않는다
            cts.Cancel();
            _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            return ct.IsCancellationRequested ? new ModelHealth.Failed(Tr("취소했어요")) : new ModelHealth.TimedOut();
        }
        try
        {
            await work;
            return ModelHealth.OK;
        }
        catch (TimeoutException) { return new ModelHealth.TimedOut(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new ModelHealth.TimedOut(); }
        catch (OperationCanceledException) { return new ModelHealth.Failed(Tr("취소했어요")); }
        catch (Exception e) { return new ModelHealth.Failed(ShortReason(e)); }
    }

    /// Ollama 오류에서 짧은 이유만: {"error":"…"} 이면 그 글, 아니면 오류 설명 첫 줄 (120자까지)
    public static string ShortReason(Exception error)
    {
        string text;
        if (error is OllamaException { Kind: OllamaErrorKind.Http } oe)
        {
            text = oe.Body.Length == 0 ? $"HTTP {oe.StatusCode}" : oe.Body;
            try
            {
                using var doc = JsonDocument.Parse(oe.Body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                    text = e.GetString() ?? text;
            }
            catch (JsonException) { }
        }
        else
        {
            text = error.Message;
        }
        text = text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
        text = text.Trim();
        return text.Length > 120 ? text[..120] + "…" : text;
    }

    /// 로컬 Ollama 로 확인 (기억한 결과가 있으면 그것). 시간 초과는 기억하지 않는다.
    public static async Task<ModelHealth> CheckAsync(string model, OllamaClient client, ModelHealthCache? cache = null,
                                                     bool useCache = true, CancellationToken ct = default)
    {
        cache ??= ModelHealthCache.Shared;
        var key = client.BaseUrl + "|" + model;
        if (useCache && cache.Get(key) is { } known) return known;
        var health = await CheckAsync(c => client.PingAsync(model, Timeout + 5, c), Timeout, ct);
        if (health is not ModelHealth.TimedOut) cache.Set(key, health);
        return health;
    }
}

/// 모델 확인 결과 기억 (앱을 켜 둔 동안)
public sealed class ModelHealthCache
{
    public static ModelHealthCache Shared { get; } = new();
    private readonly ConcurrentDictionary<string, ModelHealth> _items = new();

    public ModelHealth? Get(string key) => _items.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, ModelHealth value) => _items[key] = value;
}
