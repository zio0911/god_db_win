using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GodDB.App.Infrastructure;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

/// 결과 편집 적용 확인 창: 실행할 SQL 미리보기 → 한 트랜잭션으로 적용 (Mac EditApplySheet)
[SheetFor(nameof(AppState.EditApply))]
public sealed class EditApplySheet : Window
{
    private readonly AppState _app;
    private readonly EditApplyRequest _request;
    private readonly SelectableTextBlock _error = new() { Classes = { "error" }, IsVisible = false };
    private readonly ProgressBar _working = new() { IsIndeterminate = true, Width = 60, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    public Button ApplyButton { get; }

    public EditApplySheet(AppState app, object value)
    {
        _app = app;
        _request = (EditApplyRequest)value;
        Title = Tr("데이터 변경 적용");
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        CanResize = false;

        var tab = app.Tab(_request.TabId);
        var body = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
        body.Children.Add(Ui.Headline("✎ " + Tr("데이터 변경 적용")));
        if (tab != null) body.Children.Add(Ui.Text($"{app.Profile(tab.ProfileId)?.Name ?? ""} / {tab.Database}", "secondary"));
        body.Children.Add(Ui.Text(Tr("아래 %@개 문장을 한 트랜잭션으로 실행해요. 하나라도 실패하면 모두 되돌려요.", _request.Statements.Count)));
        if (tab != null && app.Profile(tab.ProfileId)?.Environment == ConnectionEnvironment.Production)
            body.Children.Add(new TextBlock
            {
                Text = "⚠ " + Tr("운영 접속이에요. 적용하면 운영 데이터가 바뀌어요."),
                Foreground = Q.Red, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            });
        var sql = string.Join("\n", _request.Statements);
        body.Children.Add(new Border
        {
            Classes = { "section" },
            Padding = new Thickness(8),
            MinHeight = 120,
            MaxHeight = 300,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = new SelectableTextBlock { Text = sql, FontFamily = Q.Mono },
            },
        });
        body.Children.Add(_error);
        ApplyButton = Ui.Button(Tr("적용"), Apply, isDefault: true);
        var copy = Ui.Button("⧉ " + Tr("SQL 복사"), () => app.Fire(() => app.Dialogs.SetClipboardTextAsync(sql)));
        body.Children.Add(Ui.ButtonRow(
            [Ui.Button(Tr("취소"), Dismiss, isCancel: true), ApplyButton],
            [copy, _working]));
        Content = body;
    }

    private void Dismiss()
    {
        // 요청을 지우면 메인 창이 이 창을 닫는다 (메인 창 없이 띄웠으면 직접 닫는다)
        if (ReferenceEquals(_app.EditApply, _request)) _app.EditApply = null;
        if (IsVisible) Close();
    }

    private void Apply()
    {
        ApplyButton.IsEnabled = false;
        _working.IsVisible = true;
        _error.IsVisible = false;
        _app.Fire(async () =>
        {
            try
            {
                await _app.ApplyEditsAsync(_request);
                Dismiss();
            }
            catch (Exception e)
            {
                _error.Text = ErrorText.Describe(e).Message;
                _error.IsVisible = true;
            }
            finally
            {
                _working.IsVisible = false;
                ApplyButton.IsEnabled = true;
            }
        });
    }
}

/// 테이블 이름 변경 / 삭제 확인 창. 삭제는 되돌릴 수 없어서 테이블 이름을 직접 입력해야 버튼이 켜진다. (Mac TableActionSheet)
[SheetFor(nameof(AppState.TableAction))]
public sealed class TableActionSheet : Window
{
    private readonly AppState _app;
    private readonly TableActionRequest _request;
    private readonly ISqlDialect _dialect;
    public TextBox Input { get; } = new();
    public SelectableTextBlock SqlPreview { get; } = new() { FontFamily = Q.Mono, TextWrapping = TextWrapping.Wrap };
    public Button RunButton { get; }
    private readonly SelectableTextBlock _error = new() { Classes = { "error" }, IsVisible = false };
    private readonly ProgressBar _workingBar = new() { IsIndeterminate = true, Width = 60, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private bool _working;

    public TableActionSheet(AppState app, object value)
    {
        _app = app;
        _request = (TableActionRequest)value;
        var profile = app.Profile(_request.ProfileId);
        _dialect = Dialects.Of(profile?.Kind ?? DatabaseKind.Mysql);
        var database = app.Connections.TryGetValue(_request.ProfileId, out var c) ? c.CurrentDatabase : "";
        var rename = _request.Kind == TableActionKind.Rename;
        Title = rename ? Tr("테이블 이름 변경") : Tr("테이블 삭제");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        CanResize = false;

        var body = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
        var where = Ui.Text($"{profile?.Name ?? ""} / {database} / {_request.Table}", "secondary");
        if (rename)
        {
            body.Children.Add(Ui.Headline("✎ " + Tr("테이블 이름 변경")));
            body.Children.Add(where);
            Input.Bind(TextBox.PlaceholderTextProperty, Loc.Bind("새 이름"));
            Input.Text = _request.Table;
            body.Children.Add(Input);
            body.Children.Add(Ui.Caption(Tr("이 테이블을 쓰는 뷰·프로시저·애플리케이션 코드는 자동으로 바뀌지 않아요.")));
        }
        else
        {
            var head = Ui.Headline("🗑 " + Tr("테이블 삭제"));
            head.Foreground = Q.Red;
            body.Children.Add(head);
            body.Children.Add(where);
            body.Children.Add(new TextBlock { Text = Tr("테이블과 모든 데이터가 영구히 삭제돼요. 되돌릴 수 없어요."), Foreground = Q.Red, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(Ui.Text(Tr("삭제하려면 테이블 이름 %@ 을(를) 입력하세요.", _request.Table)));
            Input.PlaceholderText = _request.Table;
            body.Children.Add(Input);
        }
        body.Children.Add(new Border { Classes = { "section" }, Padding = new Thickness(8), Child = SqlPreview });
        body.Children.Add(_error);
        RunButton = Ui.Button(rename ? Tr("이름 변경") : Tr("삭제"), Run, isDefault: true);
        if (!rename) RunButton.Classes.Add("danger");
        body.Children.Add(Ui.ButtonRow(
            [Ui.Button(Tr("취소"), Dismiss, isCancel: true), RunButton],
            [_workingBar]));
        Content = body;
        Input.TextChanged += (_, _) => Update();
        Input.KeyDown += (_, e) => { if (e.Key == Key.Enter && CanRun) { Run(); e.Handled = true; } };
        Opened += (_, _) => { Input.Focus(); Input.SelectAll(); };
        Update();
    }

    public string Sql => _request.Kind == TableActionKind.Rename
        ? _dialect.RenameTableSql(_request.Table, string.IsNullOrEmpty(Input.Text) ? "…" : Input.Text)
        : _dialect.DropTableSql(_request.Table);

    public bool CanRun
    {
        get
        {
            if (_working) return false;
            if (_request.Kind == TableActionKind.Rename)
            {
                var name = (Input.Text ?? "").Trim();
                return name.Length > 0 && name != _request.Table;
            }
            return Input.Text == _request.Table;
        }
    }

    private void Update()
    {
        SqlPreview.Text = Sql;
        RunButton.IsEnabled = CanRun;
    }

    private void Dismiss()
    {
        if (ReferenceEquals(_app.TableAction, _request)) _app.TableAction = null;
        if (IsVisible) Close();
    }

    private void Run()
    {
        if (!CanRun) return;
        _working = true;
        _workingBar.IsVisible = true;
        _error.IsVisible = false;
        Update();
        var newName = (Input.Text ?? "").Trim();
        _app.Fire(async () =>
        {
            try
            {
                await _app.PerformTableActionAsync(_request, newName);
                Dismiss();
            }
            catch (Exception e)
            {
                _error.Text = ErrorText.Describe(e).Message;
                _error.IsVisible = true;
            }
            finally
            {
                _working = false;
                _workingBar.IsVisible = false;
                Update();
            }
        });
    }
}
