using Avalonia.Controls;

namespace GodDB.App.Views;

/// Redis 화면: 키 · 콘솔 · Pub/Sub · 스트림 · 서버 정보 (Redis 화면 포팅에서 바꾼다). DataContext = RedisWorkspace
public partial class RedisWorkspaceView : UserControl
{
    public RedisWorkspaceView() => InitializeComponent();
}
