using System.ComponentModel;
using GodDB.App.ViewModels;
using GodDB.Core.Schema;

namespace GodDB.App.Tests;

/// 비교가 끝나면 결과를 먼저 넣고 Running 을 끈다 — 그때 목록(HasItems)도 다시 알려야 화면에 목록이 나온다
/// (화면 확인에서 "다름 1 · 누락 10" 만 나오고 목록이 비어 있던 문제)
public class CompareResultListTests
{
    [Fact]
    public Task HasItemsNotifiedWhenRunningEnds() => UiThread.Run(() =>
    {
        var vm = new CompareViewModel(new TestEnv().NewApp()) { Running = true };
        vm.Report = new SchemaDiffReport([new SchemaDiffItem(SchemaDiffKind.MissingTable, "t", null, "없음", "CREATE TABLE t (id int);")], 0);
        Assert.False(vm.HasItems);   // 아직 읽는 중
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.Running = false;
        Assert.True(vm.HasItems);
        Assert.Contains(nameof(CompareViewModel.HasItems), changed);
        Assert.Contains(nameof(CompareViewModel.NoDifferences), changed);
    });
}
