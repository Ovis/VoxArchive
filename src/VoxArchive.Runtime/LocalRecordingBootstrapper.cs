using VoxArchive.Application;
using VoxArchive.Application.Abstractions;
using VoxArchive.Audio;
using VoxArchive.Audio.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Encoding;
using VoxArchive.Encoding.Abstractions;
using VoxArchive.Infrastructure;

namespace VoxArchive.Runtime;

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
        var options = await settingsService.LoadRecordingOptionsAsync(cancellationToken);

        var speakerCaptureService = new SpeakerCaptureService();
        var micCaptureService = new MicCaptureService();
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

        IRecordingService recordingService = new RecordingService(
            outputCaptureController,
            failoverCoordinator,
            micCaptureService,
            speakerBuffer,
            micBuffer,
            driftCorrector,
            frameBuilder,
            encoder);

        return new RecordingRuntimeContext(
            RecordingService: recordingService,
            SettingsService: settingsService,
            DefaultOptions: options);
    }
}
