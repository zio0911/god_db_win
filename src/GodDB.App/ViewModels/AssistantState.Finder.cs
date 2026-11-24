using CommunityToolkit.Mvvm.ComponentModel;

namespace GodDB.App.ViewModels;

// AI 데이터 찾기 상태 (Mac 1.4.0 AssistantState 의 finderMode · finderCards · prodSampleAllowed)
public sealed partial class AssistantState
{
    /// 🔎 데이터 위치 찾기: 다음 입력을 데이터 찾기로 보낸다
    [ObservableProperty] private bool _finderMode;

    private readonly Dictionary<Guid, DataFinderCard> _finderCards = [];

    /// 운영 접속에서 [이 접속은 항상] 으로 샘플 조회를 허락한 접속 (앱을 켜 둔 동안)
    public HashSet<Guid> ProdSampleAllowed { get; } = [];

    /// 데이터 찾기 답 카드가 바뀌었을 때 (답 메시지 id). 화면이 그 카드를 다시 그린다.
    public event Action<Guid>? FinderCardChanged;

    /// 답 메시지 id → 카드
    public DataFinderCard? FinderCard(Guid messageId) => _finderCards.GetValueOrDefault(messageId);

    public void SetFinderCard(Guid messageId, DataFinderCard card)
    {
        _finderCards[messageId] = card;
        AppState.RunOnUi(() => FinderCardChanged?.Invoke(messageId));
    }
}
