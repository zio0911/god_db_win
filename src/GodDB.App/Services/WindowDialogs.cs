using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace GodDB.App.Services;

/// 창 하나에 붙은 실제 OS 창 구현 (파일 열기·저장, 알림, 클립보드, 탐색기)
public sealed class WindowDialogs(Window owner) : IAppDialogs
{
    private static List<FilePickerFileType> Types(IReadOnlyList<FileFilter> filters) =>
        [.. filters.Select(f => new FilePickerFileType(f.Name) { Patterns = [.. f.Patterns] })];

    private async Task<IStorageFolder?> Folder(string? path) =>
        path != null && Directory.Exists(path) ? await owner.StorageProvider.TryGetFolderFromPathAsync(path) : null;

    public async Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<FileFilter> filters, bool multiple = false, string? startFolder = null)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = filters.Count == 0 ? null : Types(filters),
            SuggestedStartLocation = await Folder(startFolder),
        });
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<FileFilter> filters)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = filters.Count == 0 ? null : Types(filters),
            DefaultExtension = filters.FirstOrDefault()?.Patterns.FirstOrDefault()?.TrimStart('*', '.'),
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title, string? startFolder = null)
    {
        var list = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await Folder(startFolder),
        });
        return list.FirstOrDefault()?.TryGetLocalPath();
    }

    public Task AlertAsync(string title, string message) => MessageDialog.Show(owner, title, message, [Tr("확인")]);

    public async Task<bool> ConfirmAsync(string title, string message, string confirm, string cancel, bool destructive = false) =>
        await MessageDialog.Show(owner, title, message, [confirm, cancel], destructiveIndex: destructive ? 0 : -1, cancelIndex: 1) == 0;

    public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> buttons) =>
        MessageDialog.Show(owner, title, message, buttons);

    public async Task<string?> GetClipboardTextAsync() =>
        owner.Clipboard is { } c ? await c.TryGetTextAsync() : null;

    public async Task SetClipboardTextAsync(string text)
    {
        if (owner.Clipboard is { } c) await c.SetTextAsync(text);
    }

    public void RevealInFileManager(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", ["-R", path]);
            else OpenExternal(Path.GetDirectoryName(path) ?? path);
        }
        catch { }
    }

    public void OpenExternal(string pathOrUrl)
    {
        try { Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true }); }
        catch
        {
            try { if (OperatingSystem.IsMacOS()) Process.Start("open", [pathOrUrl]); } catch { }
        }
    }
}

/// 간단한 알림 / 확인 창 (Mac NSAlert · SwiftUI .alert)
public sealed class MessageDialog : Window
{
    private int _result = -1;

    private MessageDialog(string title, string message, IReadOnlyList<string> buttons, int destructiveIndex, int cancelIndex)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        if (message.Length > 0)
            panel.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxHeight = 420 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        // Windows 관례: 기본 버튼이 왼쪽, 취소가 오른쪽
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var b = new Button { Content = buttons[i], MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (i == 0) { b.IsDefault = true; b.Classes.Add("accent"); }
            if (i == destructiveIndex) b.Classes.Add("danger");
            if (i == cancelIndex || (cancelIndex < 0 && buttons.Count == 1)) b.IsCancel = true;
            b.Click += (_, _) => { _result = index; Close(); };
            row.Children.Add(b);
        }
        panel.Children.Add(row);
        Content = panel;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    public static async Task<int> Show(Window? owner, string title, string message, IReadOnlyList<string> buttons,
                                       int destructiveIndex = -1, int cancelIndex = -1)
    {
        var d = new MessageDialog(title, message, buttons, destructiveIndex, cancelIndex);
        if (owner != null && owner.IsVisible) await d.ShowDialog(owner);
        else
        {
            var tcs = new TaskCompletionSource();
            d.Closed += (_, _) => tcs.TrySetResult();
            d.Show();
            await tcs.Task;
        }
        return d._result;
    }
}
