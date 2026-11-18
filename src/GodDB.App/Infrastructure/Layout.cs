using Avalonia.Controls;
using Avalonia.VisualTree;
using GodDB.App.ViewModels;

namespace GodDB.App.Infrastructure;

/// 끌어서 바꾼 화면 경계 크기 (Mac Layout — UserDefaults 대신 Prefs 에 같은 키로 저장. 탭을 새로 열거나 다시 켜도 그대로)
public static class Layout
{
    public const string QueryEditorHeight = "GodDB.layout.queryEditorHeight";
    public const string RowDetailWidth = "GodDB.layout.rowDetailWidth";
    public const string LogHeight = "GodDB.layout.logHeight";
    public const string LogDetailWidth = "GodDB.layout.logDetailWidth";
    public const string AssistantWidth = "GodDB.layout.assistantWidth";
    public const string DiagramAIWidth = "GodDB.layout.diagramAIWidth";
    public const string ModelListWidth = "GodDB.layout.modelListWidth";
    public const string ModelEditorHeight = "GodDB.layout.modelEditorHeight";
    public const string RedisWorkbenchHeight = "GodDB.layout.redisWorkbenchHeight";

    /// 처음 크기 (Mac 과 같음)
    public static readonly IReadOnlyDictionary<string, double> Defaults = new Dictionary<string, double>
    {
        [QueryEditorHeight] = 220,
        [RowDetailWidth] = 380,
        [LogHeight] = 220,
        [LogDetailWidth] = 380,
        [AssistantWidth] = 400,
        [DiagramAIWidth] = 380,
        [ModelListWidth] = 220,
        [ModelEditorHeight] = 280,
        [RedisWorkbenchHeight] = 160,
    };

    public static IReadOnlyList<string> All => [.. Defaults.Keys];

    public static double Get(string key)
    {
        var v = Prefs.Get(key, Defaults[key]);
        return double.IsFinite(v) && v > 0 ? v : Defaults[key];
    }

    public static void Set(string key, double value)
    {
        if (double.IsFinite(value) && value > 0) Prefs.Set(key, Math.Round(value, 1));
    }

    /// 초기화한 횟수 (화면에 없던 동안 초기화됐는지 알아보는 데 쓴다)
    public static int Generation { get; private set; }
    private static event Action? Changed;

    /// 메뉴 "레이아웃 초기화": 저장한 크기를 지워 처음 크기로
    public static void Reset(AppState app)
    {
        foreach (var k in Defaults.Keys) Prefs.Remove(k);
        app.Assistant.Width = Defaults[AssistantWidth];
        Generation++;
        Changed?.Invoke();
        app.Log(LogLevel.Info, LogSource.App, Tr("화면 레이아웃을 처음 크기로 되돌렸어요"));
    }

    /// 초기화되면 apply 를 부른다. owner 가 화면에 붙어 있는 동안만 듣고, 떨어져 있던 동안 초기화됐으면 다시 붙을 때 부른다.
    public static void OnReset(Control owner, Action apply)
    {
        var seen = Generation;
        void Handler()
        {
            seen = Generation;
            apply();
        }
        owner.AttachedToVisualTree += (_, _) =>
        {
            Changed -= Handler;
            Changed += Handler;
            if (seen != Generation) Handler();
        };
        owner.DetachedFromVisualTree += (_, _) => Changed -= Handler;
        if (owner.IsAttachedToVisualTree()) Changed += Handler;
    }

    /// 경계선 끌기가 끝나면 def 의 실제 크기를 저장한다
    public static void SaveOnDrag(GridSplitter splitter, DefinitionBase def, string key) =>
        splitter.DragCompleted += (_, _) => Set(key, Size(def));

    public static double Size(DefinitionBase def) => def switch
    {
        RowDefinition r => r.ActualHeight,
        ColumnDefinition c => c.ActualWidth,
        _ => 0,
    };
}
