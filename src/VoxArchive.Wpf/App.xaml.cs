using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoxArchive.Runtime;

namespace VoxArchive.Wpf;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoxArchive");
        Directory.CreateDirectory(appData);

        var settingsPath = Path.Combine(appData, "settings.json");
        var appErrorLogPath = Path.Combine(appData, "app-errors.log");

        try
        {
            var bootstrapper = new LocalRecordingBootstrapper(settingsPath);
            var context = await bootstrapper.InitializeAsync();

            _host = Host.CreateDefaultBuilder()
                .ConfigureLogging(builder =>
                {
                    builder.ClearProviders();
                    builder.SetMinimumLevel(LogLevel.Information);
                    builder.AddProvider(new AppFileLoggerProvider(appErrorLogPath));
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton(context);
                    services.AddSingleton(new RecordingCatalogService(Path.Combine(appData, "library.db")));
                    services.AddSingleton<WhisperModelStore>();
                    services.AddSingleton<WhisperTranscriptionService>();
                    services.AddSingleton<TranscriptionJobQueue>();
                    services.AddSingleton<MainViewModel>();
                    services.AddTransient<MainWindow>();
                })
                .Build();

            await _host.StartAsync();

            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("Application startup completed.");

            var window = _host.Services.GetRequiredService<MainWindow>();
            window.DataContext = _host.Services.GetRequiredService<MainViewModel>();
            window.Show();
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(
                    appErrorLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [Fatal] [App] 起動失敗: {ex}" + Environment.NewLine);
            }
            catch
            {
            }

            Shutdown(-1);
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                _host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            }
            catch
            {
            }

            _host.Dispose();
            _host = null;
        }

        base.OnExit(e);
    }
}
