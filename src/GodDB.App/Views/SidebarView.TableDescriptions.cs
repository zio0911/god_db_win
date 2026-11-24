using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

// 사이드바 머리 줄의 테이블 설명 (AI 참고) 말풍선 아이콘 · DB 머리 우클릭 메뉴 (Mac 1.4.3 #178 · #185)
public partial class SidebarView
{
    private static readonly IBrush DescriptionsPurple = new SolidColorBrush(Color.FromRgb(0xAF, 0x52, 0xDE));
    private AppState? _descriptionsApp;

    private void AttachTableDescriptions(AppState app)
    {
        if (ReferenceEquals(_descriptionsApp, app)) return;
        _descriptionsApp = app;
        DescriptionsButton.Click += (_, _) => app.OpenTableDescriptions();
        var item = new MenuItem { Header = Tr("테이블 설명 (AI 참고)…") };
        item.Click += (_, _) => app.OpenTableDescriptions();
        SchemaHeader.ContextMenu = new ContextMenu { ItemsSource = new[] { item } };
        app.TableDescriptionsChanged += UpdateDescriptionsIcon;
        app.PropertyChanged += OnAppChangedForDescriptions;
        app.ConnectionChanged += _ => UpdateDescriptionsIcon();
        UpdateDescriptionsIcon();
    }

    private void OnAppChangedForDescriptions(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.SelectedProfileId) or nameof(AppState.State) or nameof(AppState.CurrentDatabase))
            UpdateDescriptionsIcon();
    }

    /// 설명이 없으면 흐린 빈 말풍선, 있으면 보라색 채운 말풍선 + 도움말에 테이블 수
    private void UpdateDescriptionsIcon()
    {
        if (_descriptionsApp is not { } app) return;
        AppState.RunOnUi(() =>
        {
            var has = app.CurrentDescriptions != null;
            DescriptionsIcon.Filled = has;
            DescriptionsIcon.Foreground = has ? DescriptionsPurple : Brushes.Gray;
            DescriptionsIcon.Opacity = has ? 1 : 0.7;
            ToolTip.SetTip(DescriptionsButton, app.TableDescriptionsTip);
            Avalonia.Automation.AutomationProperties.SetName(DescriptionsButton, Tr("테이블 설명 (AI 참고)"));
        });
    }

    /// 말풍선이 채워져 있는지 (테스트용)
    public bool DescriptionsIconFilled => DescriptionsIcon.Filled;
}
