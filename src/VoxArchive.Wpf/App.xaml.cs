using System.IO;
using VoxArchive.Runtime;

namespace VoxArchive.Wpf;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoxArchive");
        var settingsPath = Path.Combine(appData, "settings.json");

        var bootstrapper = new LocalRecordingBootstrapper(settingsPath);
        var context = await bootstrapper.InitializeAsync();

        var vm = new MainViewModel(context);
        var window = new MainWindow
        {
            DataContext = vm
        };

        window.Show();
    }
}


