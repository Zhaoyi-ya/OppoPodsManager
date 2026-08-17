using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OppoPodsManager.UI.Views;

public partial class AboutView : PageView
{
    public AboutView()
    {
        InitializeComponent();
        VersionText.Text = AppInfo.VersionLabel + " 内测版";
    }

    private void AboutBack_Click(object? s, RoutedEventArgs e) => Host?.RequestNavigate("settings");

    private void OpenUrl_Click(object? s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is string url)
            Host?.OpenUrl(url);
    }

    public override void ApplySnapshot(BusinessSnapshot snapshot) { }
}
