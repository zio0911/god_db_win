using Avalonia.Controls;
using GodDB.App.ViewModels;

namespace GodDB.App.Views;

public partial class SplashView : UserControl
{
    public SplashView()
    {
        InitializeComponent();
        VersionText.Text = AppState.Version;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not StartupState st) return;
        void Update() => CurrentText.Text = st.Current.Length == 0 ? Tr("준비 중…") : st.Current + "…";
        st.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(StartupState.Current)) Update(); };
        Update();
    }
}
