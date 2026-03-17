using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using VoxArchive.Application;
using VoxArchive.Application.Abstractions;
using VoxArchive.Audio;
using VoxArchive.Audio.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Encoding;
using VoxArchive.Encoding.Abstractions;
using VoxArchive.Infrastructure;

namespace VoxArchive.Runtime;

[SupportedOSPlatform("windows")]
public sealed class LocalRecordingBootstrapper
{
    private readonly string _settingsPath;

    public LocalRecordingBootstrapper(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public async Task<RecordingRuntimeContext> InitializeAsync(CancellationToken cancellationToken = default)
    {
        ISettingsService settingsService = new JsonSettingsService(_settingsPath);
        IDeviceService deviceService = new WasapiDeviceService();
        IProcessCatalogService processCatalogService = new ProcessCatalogService();

        var loaded = await settingsService.LoadRecordingOptionsAsync(cancellationToken);
        var defaultSpeaker = await deviceService.GetDefaultSpeakerDeviceAsync(cancellationToken);
        var defaultMic = await deviceService.GetDefaultMicrophoneDeviceAsync(cancellationToken);
        var options = ApplyDefaults(loaded, defaultSpeaker, defaultMic);

        var services = new ServiceCollection();
        services.AddSingleton<ISettingsService>(settingsService);
        services.AddSingleton<IDeviceService>(deviceService);
        services.AddSingleton<IProcessCatalogService>(processCatalogService);
        services.AddSingleton(options);

        services.AddSingleton<ISpeakerCaptureService>(_ => NaudioRuntimeSupport.CreateSpeakerCaptureService());
        services.AddSingleton<IMicCaptureService>(_ => NaudioRuntimeSupport.CreateMicCaptureService());
        services.AddSingleton<IProcessLoopbackCaptureService, ProcessLoopbackCaptureService>();

        services.AddSingleton<IOutputCaptureController>(sp =>
        {
            var speakerSource = new SpeakerLoopbackCaptureSource(sp.GetRequiredService<ISpeakerCaptureService>());
            var processSource = new ProcessLoopbackCaptureSource(sp.GetRequiredService<IProcessLoopbackCaptureService>());
            return new OutputCaptureController(speakerSource, processSource);
        });

        services.AddSingleton<IOutputCaptureFailoverCoordinator, OutputCaptureFailoverCoordinator>();

        services.AddSingleton<IRecordingService>(sp =>
        {
            var recordingOptions = sp.GetRequiredService<RecordingOptions>();
            var sampleRate = recordingOptions.SampleRate > 0 ? recordingOptions.SampleRate : 48_000;
            var bufferCapacity = sampleRate * 5;

            IRingBuffer speakerBuffer = new FloatRingBuffer(bufferCapacity);
            IRingBuffer micBuffer = new FloatRingBuffer(bufferCapacity);
            IDriftCorrector driftCorrector = new PiDriftCorrector(recordingOptions.Kp, recordingOptions.Ki, recordingOptions.MaxCorrectionPpm);
            IVariableRateResampler resampler = new LinearVariableRateResampler();
            IFrameBuilder frameBuilder = new FrameBuilder(speakerBuffer, micBuffer, resampler);
            IFfmpegFlacEncoder encoder = new FfmpegFlacEncoder();

            var baseDir = Path.GetDirectoryName(_settingsPath) ?? ".";
            var logPath = Path.Combine(baseDir, "recording.log");

            IRecordingTelemetrySink telemetrySink = new FileRecordingTelemetrySink(logPath);
            return new RecordingService(
                sp.GetRequiredService<IOutputCaptureController>(),
                sp.GetRequiredService<IOutputCaptureFailoverCoordinator>(),
                sp.GetRequiredService<IMicCaptureService>(),
                speakerBuffer,
                micBuffer,
                driftCorrector,
                frameBuilder,
                encoder,
                telemetrySink);
        });

        var provider = services.BuildServiceProvider();

        return new RecordingRuntimeContext(
            RecordingService: provider.GetRequiredService<IRecordingService>(),
            SettingsService: provider.GetRequiredService<ISettingsService>(),
            DeviceService: provider.GetRequiredService<IDeviceService>(),
            ProcessCatalogService: provider.GetRequiredService<IProcessCatalogService>(),
            DefaultOptions: options);
    }

    private static RecordingOptions ApplyDefaults(RecordingOptions loaded, AudioDeviceInfo? defaultSpeaker, AudioDeviceInfo? defaultMic)
    {
        var outputDirectory = string.IsNullOrWhiteSpace(loaded.OutputDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VoxArchive")
            : loaded.OutputDirectory;

        return loaded with
        {
            OutputDirectory = outputDirectory,
            SpeakerDeviceId = string.IsNullOrWhiteSpace(loaded.SpeakerDeviceId)
                ? (defaultSpeaker?.DeviceId ?? string.Empty)
                : loaded.SpeakerDeviceId,
            MicDeviceId = string.IsNullOrWhiteSpace(loaded.MicDeviceId)
                ? (defaultMic?.DeviceId ?? string.Empty)
                : loaded.MicDeviceId
        };
    }
}
