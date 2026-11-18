using Avalonia.Controls;

namespace GodDB.App.Views;

/// 사이드바 아래 Redis 키 검색 칸 (Redis 화면 포팅에서 바꾼다). DataContext = RedisWorkspace (없으면 null)
public partial class RedisKeySearchBar : UserControl
{
    public RedisKeySearchBar() => InitializeComponent();
}
