using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using GodDB.App.Infrastructure;
using GodDB.App.Services;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 테이블 명세서(엑셀) 저장 전: 시스템명 · 작성자 · 작성일을 묻는다 (Mac TableSpecExportSheet)
public sealed class TableSpecExportWindow : Window
{
    public const string AuthorKey = "tableSpec.author";

    private readonly AppState _app;
    private readonly DataModel _model;

    public TextBox SystemNameBox { get; }
    public TextBox AuthorBox { get; }
    public TextBox DateBox { get; }
    /// 저장 창까지 끝난 작업 (테스트용)
    public Task? Saving { get; private set; }

    public static string Today() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// 작성자 기본값: 지난번에 쓴 이름, 없으면 Windows 사용자 이름 (Mac 은 NSFullUserName — Windows 는 로그인 이름)
    public static string DefaultAuthor()
    {
        var saved = Prefs.GetString(AuthorKey) ?? "";
        return saved.Length > 0 ? saved : Environment.UserName;
    }

    public TableSpecExportWindow(AppState app, DataModel model)
    {
        _app = app;
        _model = model;
        Title = Tr("테이블 명세서로 저장하기…");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        SystemNameBox = new TextBox { Text = model.Name };
        AuthorBox = new TextBox { Text = DefaultAuthor() };
        DateBox = new TextBox { Text = Today() };

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnSpacing = 10, RowSpacing = 8 };
        void Row(int r, string label, TextBox box)
        {
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetRow(l, r);
            Grid.SetRow(box, r);
            Grid.SetColumn(box, 1);
            form.Children.Add(l);
            form.Children.Add(box);
        }
        Row(0, Tr("시스템명"), SystemNameBox);
        Row(1, Tr("작성자"), AuthorBox);
        Row(2, Tr("작성일"), DateBox);

        var buttons = Ui.ButtonRow([Ui.Button(Tr("취소"), Close, isCancel: true), Ui.Button(Tr("저장…"), Save, isDefault: true)]);
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = Ui.Stack(12,
                Ui.Headline("▦ " + Tr("테이블 명세서로 저장하기…")),
                form,
                Ui.Caption(Tr("목차 시트와 테이블마다 한 시트(컬럼 · 인덱스 · 관계)로 된 엑셀 파일을 만들어요.")),
                buttons),
        };
    }

    public TableSpecInfo Info => new((SystemNameBox.Text ?? "").Trim(), (AuthorBox.Text ?? "").Trim(), (DateBox.Text ?? "").Trim());

    /// [저장…]: 작성자를 기억하고 창을 닫은 뒤 저장 위치를 묻는다
    public void Save()
    {
        var info = Info;
        Prefs.Set(AuthorKey, info.Author);
        Close();
        var model = _model;
        var tcs = new TaskCompletionSource();
        Saving = tcs.Task;
        // 창이 닫힌 뒤에 저장 창을 띄운다
        Dispatcher.UIThread.Post(async () =>
        {
            try { await SaveFileAsync(_app, model, info); }
            finally { tcs.TrySetResult(); }
        });
    }

    public static async Task SaveFileAsync(AppState app, DataModel model, TableSpecInfo info)
    {
        var path = await app.Dialogs.SaveFileAsync(Tr("테이블 명세서로 저장하기…"), $"{model.Name}_테이블명세서.xlsx",
                                                   [new FileFilter("Excel", ["*.xlsx"])]);
        if (path == null) return;
        try
        {
            // 원자적으로 쓰기: 임시 파일에 다 쓴 뒤 바꾼다 (Mac write(options: .atomic))
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, TableSpecExport.Data(model, info));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e)
        {
            await app.Dialogs.AlertAsync(Tr("테이블 명세서로 저장하기…"), e.Message);
        }
    }
}
