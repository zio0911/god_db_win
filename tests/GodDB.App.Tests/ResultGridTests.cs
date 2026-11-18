using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using GodDB.App.Controls;
using GodDB.App.ViewModels;
using GodDB.Core;
using GodDB.Core.Results;
using GodDB.Core.Schema;

namespace GodDB.App.Tests;

/// 결과 표 (가상화 · 이어 붙이기 · 편집 상태 표시 · 복사)
public class ResultGridTests
{
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static QueryResult Result(int rows, int columns = 3) =>
        new(Enumerable.Range(0, columns).Select(c => $"c{c}"),
            Enumerable.Range(0, rows).Select(r => (IReadOnlyList<string?>)Enumerable.Range(0, columns).Select(c => $"{r}-{c}").ToArray()), 0.01);

    private static (Window, ResultGrid) Host()
    {
        var grid = new ResultGrid();
        var w = new Window { Width = 800, Height = 600, Content = grid };
        w.Show();
        Pump();
        return (w, grid);
    }

    [Fact]
    public Task Append_200kRows_FinishesQuickly() => UiThread.Run(() =>
    {
        var (w, grid) = Host();
        var r = Result(1_000);
        r.AppendMode = true;
        r.HasMore = true;
        r.PageSize = 1_000;
        grid.Show(r, new PendingEdits(), "");
        Pump();
        var sw = Stopwatch.StartNew();
        // AppState.LoadMoreAsync 와 같이 1,000행씩 붙이고 RowsAppended 를 알린다
        while (r.Rows.Count < AppState.MaxRowsInMemory)
        {
            var start = r.Rows.Count;
            r.Rows.AddRange(Enumerable.Range(start, 1_000).Select(i => new QueryResult.Row(i, [$"{i}-0", $"{i}-1", $"{i}-2"])));
            grid.Append(start, 1_000);
        }
        Pump();
        sw.Stop();
        Assert.Equal(AppState.MaxRowsInMemory, grid.Rows.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        // 한 번에 200,000행을 붙여도
        var r2 = Result(0);
        r2.AppendMode = true;
        grid.Show(r2, new PendingEdits(), "");
        Pump();
        sw.Restart();
        r2.Rows.AddRange(Enumerable.Range(0, 200_000).Select(i => new QueryResult.Row(i, [$"{i}", "b", "c"])));
        grid.Append(0, 200_000);
        Pump();
        Assert.Equal(200_000, grid.Rows.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"single append took {sw.Elapsed}");
        w.Close();
    });

    [Fact]
    public Task Append_KeepsScrollAndSelection() => UiThread.Run(() =>
    {
        var (w, grid) = Host();
        var r = Result(1_000, 1);
        r.AppendMode = true;
        grid.Show(r, new PendingEdits(), "");
        Pump();
        grid.Select([990]);
        Pump();
        int FirstVisible() => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(grid).OfType<DataGridRow>()
            .Where(x => x.IsVisible && x.DataContext is GridRow).Min(x => ((GridRow)x.DataContext!).Id);
        var before = FirstVisible();
        Assert.True(before > 900);
        r.Rows.AddRange(Enumerable.Range(1_000, 1_000).Select(i => new QueryResult.Row(i, [$"{i}"])));
        grid.Append(1_000, 1_000);
        Pump();
        Assert.Equal(2_000, grid.Rows.Count);
        Assert.InRange(FirstVisible(), before - 2, before + 2);
        Assert.Equal([990], grid.SelectedIds);
        Assert.Single(grid.Grid.SelectedItems);
        w.Close();
    });

    [Fact]
    public Task ReachingEnd_AsksForMore_AndFilterHidesRows() => UiThread.Run(() =>
    {
        var (w, grid) = Host();
        var r = Result(100);
        r.AppendMode = true;
        var asked = 0;
        grid.OnReachEnd = () => asked++;
        grid.Show(r, new PendingEdits(), "");
        Pump();
        Assert.Equal(0, asked);
        grid.Select([99]);
        Pump();
        Assert.True(asked > 0);
        grid.Show(r, new PendingEdits(), " 55-1 ");
        Assert.Equal(["55-1"], grid.Rows.Select(x => x.Values[1]));
        w.Close();
    });
}
