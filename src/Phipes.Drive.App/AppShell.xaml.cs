using Phipes.Drive.App.Pages;

namespace Phipes.Drive.App;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        // Pushed on top of the tabs, so it gets a back button and hides the tab bar.
        Routing.RegisterRoute(nameof(ViewerPage), typeof(ViewerPage));
    }
}
