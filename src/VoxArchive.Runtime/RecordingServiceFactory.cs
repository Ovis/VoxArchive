using Microsoft.Extensions.Logging;
using VoxArchive.Application;
using VoxArchive.Application.Abstractions;
using VoxArchive.Audio.Abstractions;
using VoxArchive.Encoding.Abstractions;

namespace VoxArchive.Runtime;

public sealed class RecordingServiceFactory(
    ILogger<RecordingService> logger,
    IEnumerable<IRecordingTelemetrySink> telemetrySinks)
    : IRecordingServiceFactory
{
    private readonly IRecordingTelemetrySink? _telemetrySink = telemetrySinks.FirstOrDefault();

    public IRecordingService Create(
        IOutputCaptureController outputCaptureController,
        IOutputCaptureFailoverCoordinator failoverCoordinator,
        IMicCaptureService micCaptureService,
        IRingBuffer speakerBuffer,
        IRingBuffer micBuffer,
        IDriftCorrector driftCorrector,
        IFrameBuilder frameBuilder,
        IFfmpegFlacEncoder encoder)
    {
        return new RecordingService(
            outputCaptureController,
            failoverCoordinator,
            micCaptureService,
            speakerBuffer,
            micBuffer,
            driftCorrector,
            frameBuilder,
            encoder,
            _telemetrySink,
            logger);
    }
}
