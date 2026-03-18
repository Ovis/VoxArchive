using System.IO;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoxArchive.Application.Abstractions;
using VoxArchive.Audio;
using VoxArchive.Audio.Abstractions;
using VoxArchive.Infrastructure;
using VoxArchive.Runtime;
using ZLogger;

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

        var logsDir = Path.Combine(appData, "logs");
        Directory.CreateDirectory(logsDir);

        var settingsPath = Path.Combine(appData, "settings.json");

        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureLogging(builder =>
                {
                    builder.ClearProviders();
                    builder.SetMinimumLevel(LogLevel.Information);
                    builder.AddZLoggerRollingFile(options =>
                    {
                        options.FilePathSelector = (timestamp, sequenceNumber) =>
                            Path.Combine(logsDir, $"app-{timestamp.ToLocalTime():yyyyMMdd}-{sequenceNumber:000}.log");
                        options.RollingSizeKB = 1024;
                    });
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton<ISettingsService>(_ => new JsonSettingsService(settingsPath));
                    services.AddSingleton<IDeviceService, WasapiDeviceService>();
                    services.AddSingleton<IProcessCatalogService, ProcessCatalogService>();
                    services.AddSingleton<ISpeakerCaptureService>(_ => NAudioRuntimeSupport.CreateSpeakerCaptureService());
                    services.AddSingleton<IMicCaptureService>(_ => NAudioRuntimeSupport.CreateMicCaptureService());
                    services.AddSingleton<IProcessLoopbackCaptureService, ProcessLoopbackCaptureService>();
                    services.AddSingleton<IRecordingServiceFactory, RecordingServiceFactory>();
                    services.AddSingleton<LocalRecordingBootstrapper>();

                    services.AddSingleton(new RecordingCatalogService(Path.Combine(appData, "library.json")));
                    services.AddSingleton<WhisperModelStore>();
                    services.AddSingleton<WhisperTranscriptionService>();
                    services.AddSingleton<TranscriptionJobQueue>();
                    services.AddTransient<IRecordingPlaybackService, RecordingPlaybackService>();
                    services.AddTransient<MainWindow>();
                })
                .Build();

            await _host.StartAsync();

            var bootstrapper = _host.Services.GetRequiredService<LocalRecordingBootstrapper>();
            var context = await bootstrapper.InitializeAsync();

            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("Application startup completed.");

            var window = _host.Services.GetRequiredService<MainWindow>();
            window.DataContext = ActivatorUtilities.CreateInstance<MainViewModel>(_host.Services, context);
            window.Show();
        }
        catch (Exception ex)
        {
            if (_host is not null)
            {
                try
                {
                    var logger = _host.Services.GetService<ILogger<App>>();
                    logger?.LogCritical(ex, "Application startup failed.");
                }
                catch
                {
                    Debug.WriteLine($"[App] Failed to log startup exception: {ex}");
                }
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
                Debug.WriteLine("[App] Host stop threw an exception during shutdown.");
            }

            _host.Dispose();
            _host = null;
        }

        base.OnExit(e);
    }
}

