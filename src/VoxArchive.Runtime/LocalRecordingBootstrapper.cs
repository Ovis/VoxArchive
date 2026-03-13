using System.Runtime.Versioning;
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

        var speakerCaptureService = NaudioRuntimeSupport.CreateSpeakerCaptureService();
        var micCaptureService = NaudioRuntimeSupport.CreateMicCaptureService();
        var processLoopbackCaptureService = new ProcessLoopbackCaptureService();

        var speakerSource = new SpeakerLoopbackCaptureSource(speakerCaptureService);
        var processSource = new ProcessLoopbackCaptureSource(processLoopbackCaptureService);

        IOutputCaptureController outputCaptureController = new OutputCaptureController(speakerSource, processSource);
        IOutputCaptureFailoverCoordinator failoverCoordinator = new OutputCaptureFailoverCoordinator(outputCaptureController);

        var sampleRate = options.SampleRate > 0 ? options.SampleRate : 48_000;
        var bufferCapacity = sampleRate * 5;

        IRingBuffer speakerBuffer = new FloatRingBuffer(bufferCapacity);
        IRingBuffer micBuffer = new FloatRingBuffer(bufferCapacity);
        IDriftCorrector driftCorrector = new PiDriftCorrector(options.Kp, options.Ki, options.MaxCorrectionPpm);
        IVariableRateResampler resampler = new LinearVariableRateResampler();
        IFrameBuilder frameBuilder = new FrameBuilder(speakerBuffer, micBuffer, resampler);
        IFfmpegFlacEncoder encoder = new FfmpegFlacEncoder();

        var baseDir = Path.GetDirectoryName(_settingsPath) ?? ".";
        var logPath = Path.Combine(baseDir, "recording.log");
        var csvPath = Path.Combine(baseDir, "recording-metrics.csv");
        var jsonlPath = Path.Combine(baseDir, "recording-metrics.jsonl");

        IRecordingTelemetrySink telemetrySink = new CompositeRecordingTelemetrySink(
            new FileRecordingTelemetrySink(logPath),
            new CsvRecordingTelemetrySink(csvPath),
            new JsonlRecordingTelemetrySink(jsonlPath));

        IRecordingService recordingService = new RecordingService(
            outputCaptureController,
            failoverCoordinator,
            micCaptureService,
            speakerBuffer,
            micBuffer,
            driftCorrector,
            frameBuilder,
            encoder,
            telemetrySink);

        return new RecordingRuntimeContext(
            RecordingService: recordingService,
            SettingsService: settingsService,
            DeviceService: deviceService,
            ProcessCatalogService: processCatalogService,
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
