using static GodDB.Core.L10n;

namespace GodDB.Core.Modeling;

/// 모델링 화면 상태: 저장된 모델 목록, 고른 모델, 자동 저장, 되돌리기
/// (Mac: Sources/GodDB/ModelingState.swift 의 화면과 무관한 부분. 파일 고르기 창은 화면 쪽에서 띄우고 경로만 넘긴다)
/// UI 스레드에서만 부른다. 자동 저장 타이머는 Dispatcher 로 UI 스레드에 돌아와 저장한다.
public sealed class ModelWorkspace
{
    public List<DataModel> Models { get; private set; } = [];
    public Guid? SelectedId { get; set; }
    public bool Loaded { get; private set; }
    /// 모델별 되돌리기 기록 (구조 변경 전 스냅샷)
    private readonly Dictionary<Guid, List<DataModel>> undo = [];
    private readonly Dictionary<Guid, CancellationTokenSource> saveTasks = [];
    public ModelStore Store { get; }
    /// 저장 실패 등을 앱 로그로 보낸다 (문장, 오류)
    public Action<string, Exception>? OnError { get; set; }
    /// 자동 저장 타이머가 끝났을 때 UI 스레드로 돌아가는 방법 (없으면 타이머 스레드에서 바로)
    public Action<Action>? Dispatcher { get; set; }
    /// 자동 저장 지연 (Mac 600ms)
    public TimeSpan SaveDelay { get; set; } = TimeSpan.FromMilliseconds(600);

    public ModelWorkspace(ModelStore? store = null)
    {
        Store = store ?? ModelStore.Shared;
    }

    public void LoadIfNeeded()
    {
        if (Loaded) return;
        Models = Store.List();
        SelectedId ??= Models.FirstOrDefault()?.Id;
        Loaded = true;
    }

    /// 개발용 화면 확인: 저장소 대신 메모리의 모델만 쓴다
    public void UseForPreview(IEnumerable<DataModel> list)
    {
        Models = [.. list];
        SelectedId = Models.FirstOrDefault()?.Id;
        Loaded = true;
    }

    public DataModel? Selected => SelectedId is { } id ? Find(id) : null;

    public DataModel? Find(Guid id) => Models.FirstOrDefault(m => m.Id == id);

    /// 편집기가 바뀐 모델 전체를 돌려줄 때 (Mac 의 바인딩 set): 달라졌으면 바꾸고 자동 저장
    public void Replace(DataModel changed)
    {
        int i = Models.FindIndex(m => m.Id == changed.Id);
        if (i < 0) return;
        if (Models[i] != changed)
        {
            Models[i] = changed;
            ScheduleSave(changed.Id);
        }
    }

    /// 구조를 바꾸기 전에 호출 (테이블·관계 추가/삭제, 이동 등)
    public void Checkpoint(Guid id)
    {
        if (Find(id) is not { } m) return;
        var stack = undo.TryGetValue(id, out var s) ? s : [];
        if (stack.Count == 0 || stack[^1] != m) stack.Add(m.Clone());
        if (stack.Count > 60) stack.RemoveRange(0, stack.Count - 60);
        undo[id] = stack;
    }

    public bool CanUndo(Guid id) => undo.TryGetValue(id, out var s) && s.Count > 0;

    public void UndoLast(Guid id)
    {
        if (!undo.TryGetValue(id, out var stack) || stack.Count == 0) return;
        int i = Models.FindIndex(m => m.Id == id);
        if (i < 0) return;
        var prev = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        Models[i] = prev;
        ScheduleSave(id);
    }

    /// 모델을 고친다 (checkpoint 면 먼저 되돌리기 기록). 고친 뒤 수정 시각을 바꾸고 자동 저장.
    public void Edit(Guid id, Action<DataModel> change, bool checkpoint = true)
    {
        int i = Models.FindIndex(m => m.Id == id);
        if (i < 0) return;
        if (checkpoint) Checkpoint(id);
        change(Models[i]);
        Models[i].Touch();
        ScheduleSave(id);
    }

    /// 이름 바꾸기 (Mac: 목록의 이름 바꾸기 — 앞뒤 공백을 빼고, 비면 그대로. 되돌리기 기록은 남기지 않음)
    public void Rename(Guid id, string name)
    {
        var n = DataModel.TrimSpaces(name);
        if (n.Length > 0) Edit(id, m => m.Name = n, checkpoint: false);
    }

    public void ScheduleSave(Guid id)
    {
        if (saveTasks.Remove(id, out var old)) old.Cancel();
        var cts = new CancellationTokenSource();
        saveTasks[id] = cts;
        _ = Task.Delay(SaveDelay, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            void Run()
            {
                if (cts.IsCancellationRequested) return;
                if (saveTasks.TryGetValue(id, out var cur) && cur == cts) saveTasks.Remove(id);
                if (Find(id) is { } m) SaveNow(m);
            }
            if (Dispatcher is { } d) d(Run); else Run();
        }, TaskScheduler.Default);
    }

    /// 기다리는 자동 저장을 바로 한다 (앱을 닫을 때)
    public void FlushSaves()
    {
        foreach (var (id, cts) in saveTasks.ToList())
        {
            cts.Cancel();
            saveTasks.Remove(id);
            if (Find(id) is { } m) SaveNow(m);
        }
    }

    public void SaveNow(DataModel m)
    {
        // Mac 은 복사본의 updated 만 바꿔 저장한다 (메모리의 모델은 그대로)
        var copy = m.Clone();
        copy.Updated = DateTime.UtcNow;
        try { Store.Save(copy); }
        catch (Exception e) { OnError?.Invoke(Tr("모델 저장 실패 · %@", m.Name), e); }
    }

    public void Add(DataModel model)
    {
        Models.Insert(0, model);
        SelectedId = model.Id;
        SaveNow(model);
    }

    public DataModel Create(DatabaseKind kind)
    {
        var m = new DataModel(name: Tr("새 모델 %@", Models.Count + 1), kind: kind == DatabaseKind.Redis ? DatabaseKind.Mysql : kind);
        m.AddTable(logicalName: Tr("회원"));
        Add(m);
        return m;
    }

    public DataModel? Duplicate(Guid id)
    {
        if (Find(id) is not { } src) return null;
        var m = src.Clone();
        m.Id = Guid.NewGuid();
        m.Name += Tr(" 복사본");
        m.Created = DateTime.UtcNow;
        Add(m);
        return m;
    }

    public void Delete(Guid id)
    {
        if (saveTasks.Remove(id, out var cts)) cts.Cancel();
        Store.Delete(id);
        Models.RemoveAll(m => m.Id == id);
        undo.Remove(id);
        if (SelectedId == id) SelectedId = Models.FirstOrDefault()?.Id;
    }

    // MARK: - 파일

    /// 내보내기 파일 이름 제안: "<모델 이름>.goddbmodel"
    public static string ExportFileName(DataModel m) => $"{m.Name}.{DataModel.FileExtension}";

    /// 파일로 내보내기 (경로는 화면의 저장 창에서)
    public void ExportFile(Guid id, string path)
    {
        if (Find(id) is not { } m) return;
        ModelStore.WriteFile(path, ModelStore.Encode(m));
    }

    /// 파일 가져오기 (여러 개). 실패한 파일은 (경로, 오류 문장) 으로 돌려준다.
    public List<(string Path, string Error)> ImportFiles(IEnumerable<string> paths)
    {
        var failed = new List<(string, string)>();
        foreach (var path in paths)
        {
            try
            {
                var m = ModelStore.Decode(File.ReadAllBytes(path));
                // 같은 모델을 두 번 가져와도 따로 보관
                if (Models.Any(x => x.Id == m.Id)) m.Id = Guid.NewGuid();
                Add(m);
            }
            catch (Exception e)
            {
                failed.Add((path, e.Message));
            }
        }
        return failed;
    }
}
