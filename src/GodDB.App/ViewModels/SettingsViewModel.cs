using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodDB.App.Infrastructure;
using GodDB.App.Services;

namespace GodDB.App.ViewModels;

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// 설정 창 (Mac AppSettings + SettingsView: 일반 · AI · 업데이트 · 라이선스)
public sealed partial class SettingsViewModel : ObservableObject
{
    public AppState? App { get; }
    private readonly IAppDialogs _dialogs;

    public SettingsViewModel(AppState? app, IAppDialogs? dialogs = null)
    {
        App = app;
        _dialogs = dialogs ?? app?.Dialogs ?? new NullDialogs();
        _language = L10n.Language;
        _queryTimeoutIndex = Math.Max(0, Array.IndexOf(TimeoutValues, QueryTimeLimit.Seconds));
        _confirmDangerous = RunConfirm.ConfirmDangerous;
        _prodIdleIndex = Math.Max(0, Array.IndexOf(AppState.ProdIdleChoices, Prefs.Get(AppState.ProdIdleMinutesKey, 30)));
        _logFileEnabled = LogFile.Enabled;
        _retentionIndex = Math.Max(0, LogFile.RetentionChoices.ToList().IndexOf(LogFile.RetentionDays));
        _ollamaUrl = TuningSettings.Url;
        _tuningModel = TuningSettings.Model;
        _assistantEnabled = TuningSettings.AssistantEnabled;
        _updateMode = UpdateService.Mode;
        _selectedLicense = "GodDB";
    }

    // MARK: - 일반

    public IReadOnlyList<AppLanguage> Languages { get; } = [AppLanguage.System, AppLanguage.Ko, AppLanguage.En];
    [ObservableProperty] private AppLanguage _language;

    partial void OnLanguageChanged(AppLanguage value)
    {
        AppStartup.SetLanguage(value);
        OnPropertyChanged(nameof(SystemLanguageNote));
        OnPropertyChanged(nameof(ShowSystemLanguageNote));
        OnPropertyChanged(nameof(RetentionNote));
        OnPropertyChanged(nameof(HistoryKeepNote));
    }

    /// 언어가 바뀌면 VM 이 만든 문장도 다시 읽게 한다
    public void RefreshTexts() => OnPropertyChanged(string.Empty);

    public bool ShowSystemLanguageNote => Language == AppLanguage.System;
    public string SystemLanguageNote => Tr("지금 시스템 언어: %@", L10n.Resolved == AppLanguage.Ko ? "한국어" : "English");

    private static readonly int[] TimeoutValues = [0, 30, 60, 300, 600, 1800];
    public IReadOnlyList<string> TimeoutLabels => [Tr("없음"), Tr("30초"), Tr("1분"), Tr("5분"), Tr("10분"), Tr("30분")];
    [ObservableProperty] private int _queryTimeoutIndex;
    partial void OnQueryTimeoutIndexChanged(int value)
    {
        if (value >= 0 && value < TimeoutValues.Length) Prefs.Set(AppState.QueryTimeoutKey, TimeoutValues[value]);
    }

    /// 편집기 > 쿼리 오류에 빨간 밑줄 · DB 에 검사 요청 (기본 켬, Mac 1.4.2). 다음 입력부터 적용된다 (Mac 과 같음).
    [ObservableProperty] private bool _lintEnabled = EditorLint.Enabled;
    partial void OnLintEnabledChanged(bool value) => EditorLint.Enabled = value;
    [ObservableProperty] private bool _lintServer = EditorLint.ServerEnabled;
    partial void OnLintServerChanged(bool value) => EditorLint.ServerEnabled = value;

    /// 안전 > 위험한 명령 실행 전 확인 (기본 켬). 켜고 끈 기록은 상태창 · 로그 파일 · 감사 로그(설정)에 남긴다. (Mac #103)
    [ObservableProperty] private bool _confirmDangerous;
    partial void OnConfirmDangerousChanged(bool value)
    {
        Prefs.Set(RunConfirm.ConfirmDangerousKey, value);
        App?.Log(value ? LogLevel.Info : LogLevel.Warning, LogSource.App,
                 value ? Tr("위험한 명령 실행 전 확인을 켰어요") : Tr("위험한 명령 실행 전 확인을 껐어요"),
                 audit: new AuditRecord(AuditAction.Settings, "GodDB", value ? "위험한 명령 확인 켬" : "위험한 명령 확인 끔"));
    }

    // MARK: 히스토리 보관 기간 (Mac 1.4.1: 7 · 30 · 90일 · 영구, 기본 30일)

    public IReadOnlyList<string> HistoryKeepDaysLabels => [Tr("7일"), Tr("30일"), Tr("90일"), Tr("영구 보관")];
    [ObservableProperty] private int _historyKeepDaysIndex = Math.Max(0, QueryHistory.KeepDaysChoices.ToList().IndexOf(QueryHistory.KeepDays));
    partial void OnHistoryKeepDaysIndexChanged(int value)
    {
        if (value < 0 || value >= QueryHistory.KeepDaysChoices.Count) return;
        QueryHistory.KeepDays = QueryHistory.KeepDaysChoices[value];
        OnPropertyChanged(nameof(HistoryKeepNote));
        // 바꾸면 바로 정리한다 (화면을 막지 않게 뒤에서)
        var history = App?.History ?? QueryHistory.Shared;
        _ = Task.Run(history.ApplyRetentionNow);
    }

    /// Mac 문장의 "이 Mac 의 키체인 키" → Windows: DPAPI(현재 Windows 계정)로 잠근 이 PC 의 키
    public string HistoryKeepNote => QueryHistory.KeepDays == 0
        ? Tr("실행한 쿼리 기록을 지우지 않고 남겨요. 기록과 열어 둔 쿼리 탭 내용은 이 PC 의 Windows 계정 키(DPAPI)로 암호화해서 저장해요.")
        : Tr("%@일이 지난 쿼리 기록은 자동으로 지워요. 기록과 열어 둔 쿼리 탭 내용은 이 PC 의 Windows 계정 키(DPAPI)로 암호화해서 저장해요.", QueryHistory.KeepDays);

    /// 안전 > 운영 접속 자동 끊기 (끔 · 10분 · 30분(기본) · 60분, 1.4.0)
    public IReadOnlyList<string> ProdIdleLabels => [.. AppState.ProdIdleChoices.Select(m => m == 0 ? Tr("끔") : Tr("%@분", m))];
    [ObservableProperty] private int _prodIdleIndex;
    partial void OnProdIdleIndexChanged(int value)
    {
        if (value < 0 || value >= AppState.ProdIdleChoices.Length) return;
        var m = AppState.ProdIdleChoices[value];
        Prefs.Set(AppState.ProdIdleMinutesKey, m);
        App?.Log(LogLevel.Info, LogSource.App, m == 0 ? Tr("운영 접속 자동 끊기를 껐어요") : Tr("운영 접속 자동 끊기: %@분", m),
                 audit: new AuditRecord(AuditAction.Settings, "GodDB", m == 0 ? "운영 자동 끊기 끔" : $"운영 자동 끊기 {m}분"));
    }

    [ObservableProperty] private bool _logFileEnabled;
    partial void OnLogFileEnabledChanged(bool value) => AppStartup.SetLogFileEnabled(value);

    public IReadOnlyList<string> RetentionLabels => [Tr("영구 보관"), Tr("3개월 (90일)"), Tr("6개월 (180일)"), Tr("1년 (365일)")];
    [ObservableProperty] private int _retentionIndex;
    partial void OnRetentionIndexChanged(int value)
    {
        if (value >= 0 && value < LogFile.RetentionChoices.Count) AppStartup.SetRetentionDays(LogFile.RetentionChoices[value]);
        OnPropertyChanged(nameof(RetentionNote));
    }

    public string RetentionNote => LogFile.RetentionDays == 0
        ? Tr("감사 로그(접속, 실행한 SQL, 데이터 변경, 오류)를 날짜별 파일로 남겨요. 조회 결과 행은 남기지 않아요. 파일은 지우지 않고 영구 보관해요.")
        : Tr("감사 로그를 날짜별 파일로 남기고, %@일이 지난 파일은 자동으로 지워요. 조회 결과 행은 남기지 않아요.", LogFile.RetentionDays);

    public string LogDirectory => LogFile.Shared.Directory;

    [RelayCommand]
    private void OpenLogFolder()
    {
        try { Directory.CreateDirectory(LogFile.Shared.Directory); } catch { }
        _dialogs.OpenExternal(LogFile.Shared.Directory);
    }

    public string SourceUrl => Licenses.GodDBUrl;
    public string SourceText => "github.com/zio0911/god_db_win";

    [RelayCommand]
    private void OpenSource() => _dialogs.OpenExternal(Licenses.GodDBUrl);

    // MARK: - 정보 (버전)

    [ObservableProperty] private int _versionTaps;
    [ObservableProperty] private bool _showDotZero;
    [ObservableProperty] private bool _auditToolUnlocked;
    private DispatcherTimer? _tapTimer;

    public string VersionText => AppState.Version + (ShowDotZero ? ".0" : "");
    partial void OnShowDotZeroChanged(bool value) => OnPropertyChanged(nameof(VersionText));

    public void ResetVersionTaps()
    {
        VersionTaps = 0;
        ShowDotZero = false;
        _tapTimer?.Stop();
    }

    [RelayCommand]
    public void VersionTapped()
    {
        if (ShowDotZero)
        {
            AuditToolUnlocked = true;
            ShowDotZero = false;
            _tapTimer?.Stop();
            VersionTaps = 0;
            return;
        }
        VersionTaps++;
        ShowDotZero = false;
        _tapTimer?.Stop();
        if (VersionTaps >= 40) { VersionTaps = 0; return; }
        After(TimeSpan.FromSeconds(2), () =>
        {
            if (VersionTaps is < 30 or >= 40) return;
            ShowDotZero = true;
            After(TimeSpan.FromSeconds(10), () => { ShowDotZero = false; VersionTaps = 0; });
        });
    }

    private void After(TimeSpan delay, Action action)
    {
        _tapTimer?.Stop();
        var t = new DispatcherTimer { Interval = delay };
        t.Tick += (_, _) => { t.Stop(); action(); };
        _tapTimer = t;
        t.Start();
    }

    // 로그 파일 검사 (폴더를 골라 파일만으로)
    [ObservableProperty] private string? _auditFolder;
    [ObservableProperty] private bool _auditChecking;
    public ObservableCollection<string> AuditReports { get; } = [];
    public bool HasAuditReports => AuditReports.Count > 0;

    public string AuditFolderText => AuditFolder ?? Tr("검사할 감사 로그 폴더를 고르세요 (다른 사람에게 받은 로그도 가능)");
    partial void OnAuditFolderChanged(string? value) { OnPropertyChanged(nameof(AuditFolderText)); CheckAuditCommand.NotifyCanExecuteChanged(); }
    partial void OnAuditCheckingChanged(bool value) => CheckAuditCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private async Task ChooseAuditFolderAsync()
    {
        if (await _dialogs.PickFolderAsync(Tr("폴더 선택…"), LogFile.Shared.Directory) is { } f)
        {
            AuditFolder = f;
            AuditReports.Clear();
        }
    }

    private bool CanCheckAudit() => AuditFolder != null && !AuditChecking;

    [RelayCommand(CanExecute = nameof(CanCheckAudit))]
    private async Task CheckAuditAsync()
    {
        if (AuditFolder is not { } folder) return;
        AuditChecking = true;
        LogFile.Shared.Flush();
        var reports = await Task.Run(() => AuditChain.Verify(folder));
        AuditReports.Clear();
        foreach (var r in reports) AuditReports.Add(AuditChain.Describe(r));
        OnPropertyChanged(nameof(HasAuditReports));
        AuditChecking = false;
    }

    [RelayCommand]
    private Task CopyAuditAsync() => _dialogs.SetClipboardTextAsync(string.Join("\n", AuditReports));

    // MARK: - AI

    [ObservableProperty] private bool _assistantEnabled;
    partial void OnAssistantEnabledChanged(bool value)
    {
        TuningSettings.AssistantEnabled = value;
        App?.NotifyAssistantEnabled();
    }


    /// 입력 중인 주소. 이 PC(localhost) 주소일 때만 저장한다 (다른 서버로 구조 · SQL · 샘플 값이 나가지 않게, Mac 1.4.0)
    [ObservableProperty] private string _ollamaUrl;
    partial void OnOllamaUrlChanged(string value)
    {
        if (AISettings.IsLocalUrl(value)) AISettings.Url = value;
        OnPropertyChanged(nameof(IsNonLocal));
    }

    public bool IsNonLocal => !AISettings.IsLocalUrl(OllamaUrl) || !AISettings.IsLocal;
    public string NonLocalWarning => TuningSettings.NonLocalWarning;

    /// [기본값(localhost)으로]
    [RelayCommand]
    private async Task ResetOllamaUrlAsync()
    {
        OllamaUrl = AISettings.DefaultUrl;
        AISettings.Url = AISettings.DefaultUrl;
        await RefreshAsync();
    }

    [ObservableProperty] private string _tuningModel;
    partial void OnTuningModelChanged(string value)
    {
        AISettings.Model = value ?? "";
        Probe = null;
    }

    /// [모델 확인] 결과 (null = 아직 안 함)
    [ObservableProperty] private ModelHealth? _probe;
    [ObservableProperty] private bool _probing;
    partial void OnProbeChanged(ModelHealth? value) { OnPropertyChanged(nameof(ProbeText)); OnPropertyChanged(nameof(ProbeOk)); OnPropertyChanged(nameof(ProbeFailed)); }
    public string ProbeText => Probe == null ? "" : Probe.IsOK ? "✅ OK" : "⛔ " + Probe.Reason;
    public bool ProbeOk => Probe?.IsOK == true;
    public bool ProbeFailed => Probe is { IsOK: false };

    /// 고른 모델(자동이면 쓸 모델)에 아주 짧은 요청을 보내 답하는지 확인 (AI 데이터 찾기와 같은 확인, 기억한 결과는 새로)
    [RelayCommand]
    private async Task CheckModelAsync()
    {
        var models = (Status?.RunningModels ?? []).Select(m => m.Name).ToList();
        if (AISettings.EffectiveModel(models) is not { } model) return;
        Probing = true;
        Probe = await ModelProbe.CheckAsync(model, AISettings.Client, useCache: false);
        Probing = false;
    }

    public bool CanCheckModel => !Probing && (Status?.RunningModels.Count ?? 0) > 0;
    partial void OnProbingChanged(bool value) => OnPropertyChanged(nameof(CanCheckModel));

    [ObservableProperty] private OllamaStatus? _status;
    [ObservableProperty] private bool _checking;
    [ObservableProperty] private string? _startError;

    partial void OnStatusChanged(OllamaStatus? value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanStartOllama));
        OnPropertyChanged(nameof(ShowDownload));
        OnPropertyChanged(nameof(ModelChoices));
        OnPropertyChanged(nameof(CanCheckModel));
    }

    public string StatusText => Status switch
    {
        OllamaStatus.Running r => "✅ " + Tr("실행 중 · 모델 %@개", r.Models.Count),
        OllamaStatus.InstalledNotRunning => "⏸ " + Tr("설치됐지만 꺼져 있어요"),
        OllamaStatus.NotInstalled => "✖ " + Tr("설치되지 않았어요"),
        _ => "⏳ " + Tr("확인 중…"),
    };

    public bool CanStartOllama => Status is OllamaStatus.InstalledNotRunning;
    public bool ShowDownload => Status is OllamaStatus.NotInstalled;

    /// 모델 고르기: "" = 자동 (첫 번째 모델)
    public IReadOnlyList<Choice<string>> ModelChoices
    {
        get
        {
            var list = new List<Choice<string>> { new("", Tr("자동 (추천 모델 먼저)")) };
            var models = Status?.RunningModels ?? [];
            list.AddRange(models.Select(m => new Choice<string>(m.Name, $"{m.Name}  ·  {m.ParameterSize}  ·  {m.SizeGB:0.0}GB")));
            if (TuningModel.Length > 0 && models.All(m => m.Name != TuningModel)) list.Add(new(TuningModel, TuningModel));
            return list;
        }
    }

    public Choice<string>? SelectedModel
    {
        get => ModelChoices.FirstOrDefault(c => c.Value == TuningModel);
        set { if (value != null) TuningModel = value.Value; }
    }

    /// Mac: "M5 Pro 메모리 48GB 이상이면 qwen3.8:27b-mlx, 24GB 면 qwen3:14b". Windows 는 Apple 칩 이름 대신 메모리(그래픽 메모리 포함)로
    /// 안내하고, MLX 대신 일반 Ollama 태그를 쓴다 (AISettings.RecommendedModels 참고).
    public string RecommendNote => Tr("추천: 메모리 48GB 이상이면 %@, 24GB 면 %@ 를 추천해요. 모델을 \"자동\"으로 두면 설치된 추천 모델을 먼저 써요.",
                                      AISettings.DefaultRecommendedModel, AISettings.SmallMemoryModel);
    public static string PullCommand => "ollama pull " + AISettings.DefaultRecommendedModel;
    public static string PullSmallCommand => "ollama pull " + AISettings.SmallMemoryModel;
    public string PullCommandText => PullCommand;
    public string PullSmallCommandText => PullSmallCommand;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        // AI 어시스턴트의 [‹모델› 로 바꾸기] 로 바뀐 모델을 반영
        if (TuningModel != AISettings.Model) TuningModel = AISettings.Model;
        Checking = true;
        try { Status = await Task.Run(() => TuningSettings.Client.StatusAsync()); }
        catch { Status = new OllamaStatus.NotInstalled(); }
        Checking = false;
        OnPropertyChanged(nameof(SelectedModel));
    }

    [RelayCommand]
    private async Task StartOllamaAsync()
    {
        try
        {
            OllamaClient.Start();
            await Task.Delay(2000);
            await RefreshAsync();
        }
        catch (Exception e)
        {
            StartError = ErrorText.Describe(e).Message;
        }
    }

    [RelayCommand]
    private void OpenOllamaDownload() => _dialogs.OpenExternal(OllamaClient.DownloadUrl);

    [RelayCommand]
    private Task CopyPullAsync() => _dialogs.SetClipboardTextAsync(PullCommand);

    [RelayCommand]
    private Task CopyPullSmallAsync() => _dialogs.SetClipboardTextAsync(PullSmallCommand);

    // MARK: - 업데이트

    public IReadOnlyList<UpdateMode> UpdateModes { get; } = [UpdateMode.Automatic, UpdateMode.Notify, UpdateMode.Manual];
    [ObservableProperty] private UpdateMode _updateMode;
    partial void OnUpdateModeChanged(UpdateMode value)
    {
        UpdateService.Mode = value;
        App?.RestartDailyUpdateCheck();
    }

    [RelayCommand]
    private void CheckUpdates()
    {
        if (App is { } app) app.Fire(() => app.CheckForUpdatesAsync(userInitiated: true));
    }

    [RelayCommand]
    private void ShowReleaseNotes() => App?.OpenReleaseNotes();

    // MARK: - 라이선스

    public IReadOnlyList<ThirdPartyLicense> ThirdParty => Licenses.ThirdParty;
    public string OpenSourceHeader => Tr("오픈소스 %@개", Licenses.ThirdParty.Count);
    public IReadOnlyList<string> LicenseItems => ["GodDB", .. Licenses.ThirdParty.Select(l => l.Name)];
    [ObservableProperty] private string _selectedLicense;

    partial void OnSelectedLicenseChanged(string value)
    {
        foreach (var n in new[] { nameof(LicenseTitle), nameof(LicenseSubtitle), nameof(LicenseCopyright), nameof(LicenseNote),
                                  nameof(LicenseUrl), nameof(LicenseText), nameof(HasLicenseText), nameof(IsGodDB), nameof(HasLicenseNote) })
            OnPropertyChanged(n);
    }

    private ThirdPartyLicense? Selected => Licenses.ThirdParty.FirstOrDefault(l => l.Name == SelectedLicense);
    public bool IsGodDB => Selected == null;
    public string LicenseTitle => Selected?.Name ?? Tr("GodDB");
    public string LicenseSubtitle => Selected is { } l ? $"{l.License} · {l.Version}" : $"MIT License · {Licenses.GodDBCopyright}";
    public string LicenseCopyright => Selected?.Copyright ?? Tr("GodDB는 오픈소스예요. MIT 라이선스에 따라 자유롭게 쓰고, 고치고, 배포할 수 있어요. 저작권 표시와 라이선스 문구만 함께 남겨 주세요.");
    public string LicenseNote => Selected is { Note.Length: > 0 } l ? Tr(l.Note) : "";
    public bool HasLicenseNote => LicenseNote.Length > 0;
    public string LicenseUrl => Selected?.Url ?? Licenses.GodDBUrl;
    /// 전체 문구 (없으면 링크 안내 — Mac: Simple Icons 처럼 text 가 nil 인 항목)
    public string LicenseText => Selected == null ? Licenses.GodDB : Selected.Text ?? "";
    public bool HasLicenseText => LicenseText.Length > 0;

    [RelayCommand]
    private void OpenLicenseUrl() => _dialogs.OpenExternal(LicenseUrl);
}
