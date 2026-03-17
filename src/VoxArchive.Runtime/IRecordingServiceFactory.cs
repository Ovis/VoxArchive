using VoxArchive.Application.Abstractions;
using VoxArchive.Audio.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Encoding.Abstractions;

namespace VoxArchive.Runtime;

public interface IRecordingServiceFactory
{
    IRecordingService Create(
        IOutputCaptureController outputCaptureController,
        IOutputCaptureFailoverCoordinator failoverCoordinator,
        IMicCaptureService micCaptureService,
        IRingBuffer speakerBuffer,
        IRingBuffer micBuffer,
        IDriftCorrector driftCorrector,
        IFrameBuilder frameBuilder,
        IFfmpegFlacEncoder encoder);
}
