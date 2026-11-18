using Avalonia.Controls;

namespace GodDB.App.Views;

/// 사이드바 Redis 영역: DB 선택 · 키 트리 (Redis 화면 포팅에서 바꾼다). DataContext = RedisWorkspace (없으면 null)
public partial class RedisSidebarSection : UserControl
{
    public RedisSidebarSection() => InitializeComponent();
}
