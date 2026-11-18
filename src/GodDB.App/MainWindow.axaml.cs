using Avalonia.Controls;
using GodDB.Core;

namespace GodDB.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"GodDB {AppInfo.Version}";
    }
}
