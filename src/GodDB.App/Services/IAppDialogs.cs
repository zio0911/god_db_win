namespace GodDB.App.Services;

/// 파일 고르기 창의 형식 하나 (예: "SQL", ["*.sql"])
public sealed record FileFilter(string Name, IReadOnlyList<string> Patterns);

/// OS 창(파일 열기·저장, 알림, 클립보드, 탐색기)을 AppState 가 직접 모르게 한다.
/// Mac 은 NSOpenPanel / NSSavePanel / NSAlert / NSPasteboard / NSWorkspace 를 바로 부른다.
/// 메인 창이 실제 구현(<see cref="WindowDialogs"/>)을 넣고, 테스트는 가짜를 넣는다.
public interface IAppDialogs
{
    /// 열 파일 고르기. 취소하면 빈 목록.
    Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<FileFilter> filters, bool multiple = false, string? startFolder = null);
    /// 저장 위치 고르기. 취소하면 null.
    Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileFilter> filters);
    /// 폴더 고르기. 취소하면 null.
    Task<string?> PickFolderAsync(string title, string? startFolder = null);
    /// 알림 (확인 버튼 하나)
    Task AlertAsync(string title, string message);
    /// 확인 / 취소 (destructive 면 확인 버튼을 빨갛게). 확인이면 true.
    Task<bool> ConfirmAsync(string title, string message, string confirm, string cancel, bool destructive = false);
    /// 버튼 여러 개 중 하나 (누른 버튼 번호, 닫으면 -1)
    Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> buttons);
    Task<string?> GetClipboardTextAsync();
    Task SetClipboardTextAsync(string text);
    /// 탐색기에서 파일을 선택해 보여준다 (Mac: activateFileViewerSelecting)
    void RevealInFileManager(string path);
    /// 폴더 / 파일 / 주소를 OS 기본 프로그램으로 연다
    void OpenExternal(string pathOrUrl);
}

/// 화면이 없을 때 (시작 전 · 테스트): 아무것도 고르지 않고 알림은 상태창에만 남는다
public class NullDialogs : IAppDialogs
{
    public string? Clipboard { get; set; }
    public List<string> Opened { get; } = [];
    public List<(string Title, string Message)> Alerts { get; } = [];

    public virtual Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<FileFilter> filters, bool multiple = false, string? startFolder = null) =>
        Task.FromResult<IReadOnlyList<string>>([]);
    public virtual Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileFilter> filters) => Task.FromResult<string?>(null);
    public virtual Task<string?> PickFolderAsync(string title, string? startFolder = null) => Task.FromResult<string?>(null);
    public virtual Task AlertAsync(string title, string message) { Alerts.Add((title, message)); return Task.CompletedTask; }
    public virtual Task<bool> ConfirmAsync(string title, string message, string confirm, string cancel, bool destructive = false) => Task.FromResult(false);
    public virtual Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> buttons) => Task.FromResult(-1);
    public Task<string?> GetClipboardTextAsync() => Task.FromResult(Clipboard);
    public Task SetClipboardTextAsync(string text) { Clipboard = text; return Task.CompletedTask; }
    public void RevealInFileManager(string path) => Opened.Add(path);
    public void OpenExternal(string pathOrUrl) => Opened.Add(pathOrUrl);
}
