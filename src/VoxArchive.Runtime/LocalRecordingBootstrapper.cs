using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using VoxArchive.Application;
using VoxArchive.Application.Abstractions;
using VoxArchive.Audio;
using VoxArchive.Audio.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Encoding;
using VoxArchive.Encoding.Abstractions;

namespace VoxArchive.Runtime;

[SupportedOSPlatform("windows")]
public sealed class LocalRecordingBootstrapper
{
    private readonly ISettingsService _settingsService;
    private readonly IDeviceService _deviceService;
    private readonly IProcessCatalogService _processCatalogService;
    private readonly ISpeakerCaptureService _speakerCaptureService;
    private readonly IMicCaptureService _micCaptureService;
    private readonly IProcessLoopbackCaptureService _processLoopbackCaptureService;
    private readonly ILogger<RecordingService> _recordingLogger;

    public LocalRecordingBootstrapper(
        ISettingsService settingsService,
        IDeviceService deviceService,
        IProcessCatalogService processCatalogService,
        ISpeakerCaptureService speakerCaptureService,
        IMicCaptureService micCaptureService,
        IProcessLoopbackCaptureService processLoopbackCaptureService,
        ILogger<RecordingService> recordingLogger)
    {
        _settingsService = settingsService;
        _deviceService = deviceService;
        _processCatalogService = processCatalogService;
        _speakerCaptureService = speakerCaptureService;
        _micCaptureService = micCaptureService;
        _processLoopbackCaptureService = processLoopbackCaptureService;
        _recordingLogger = recordingLogger;
    }

    public async Task<RecordingRuntimeContext> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await _settingsService.LoadRecordingOptionsAsync(cancellationToken);
        var defaultSpeaker = await _deviceService.GetDefaultSpeakerDeviceAsync(cancellationToken);
        var defaultMic = await _deviceService.GetDefaultMicrophoneDeviceAsync(cancellationToken);
        var options = ApplyDefaults(loaded, defaultSpeaker, defaultMic);

        var sampleRate = options.SampleRate > 0 ? options.SampleRate : 48_000;
        var bufferCapacity = sampleRate * 5;

        IRingBuffer speakerBuffer = new FloatRingBuffer(bufferCapacity);
        IRingBuffer micBuffer = new FloatRingBuffer(bufferCapacity);
        IDriftCorrector driftCorrector = new PiDriftCorrector(options.Kp, options.Ki, options.MaxCorrectionPpm);
        IVariableRateResampler resampler = new LinearVariableRateResampler();
        IFrameBuilder frameBuilder = new FrameBuilder(speakerBuffer, micBuffer, resampler);
        IFfmpegFlacEncoder encoder = new FfmpegFlacEncoder();

        var speakerSource = new SpeakerLoopbackCaptureSource(_speakerCaptureService);
        var processSource = new ProcessLoopbackCaptureSource(_processLoopbackCaptureService);
        IOutputCaptureController outputCaptureController = new OutputCaptureController(speakerSource, processSource);
        IOutputCaptureFailoverCoordinator failoverCoordinator = new OutputCaptureFailoverCoordinator(outputCaptureController);

        IRecordingService recordingService = new RecordingService(
            outputCaptureController,
            failoverCoordinator,
            _micCaptureService,
            speakerBuffer,
            micBuffer,
            driftCorrector,
            frameBuilder,
            encoder,
            telemetrySink: null,
            logger: _recordingLogger);

        return new RecordingRuntimeContext(
            RecordingService: recordingService,
            SettingsService: _settingsService,
            DeviceService: _deviceService,
            ProcessCatalogService: _processCatalogService,
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

