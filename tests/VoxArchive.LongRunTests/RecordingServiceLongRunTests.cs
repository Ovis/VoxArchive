using VoxArchive.Application;
using VoxArchive.Application.Abstractions;
using VoxArchive.Audio;
using VoxArchive.Audio.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Encoding.Abstractions;
using VoxArchive.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace VoxArchive.LongRunTests;

public sealed class RecordingServiceLongRunTests
{
    [Test, CancelAfter(30000)]
    public async Task ContinuousRun_ForTenSeconds_DoesNotFailAndProducesFrames()
    {
        using var fixture = new LongRunFixture();
        var sut = fixture.CreateService();

        var errors = new List<string>();
        sut.ErrorOccurred += (_, e) => errors.Add(e);

        var options = fixture.CreateOptions();

        await sut.StartAsync(options);
        await Task.Delay(TimeSpan.FromSeconds(10));
        await sut.StopAsync();

        Assert.That(errors, Is.Empty);
        Assert.That(sut.CurrentState, Is.EqualTo(RecordingState.Stopped));
        Assert.That(fixture.Encoder.Writes, Is.GreaterThanOrEqualTo(500), $"Expected many frame writes, actual={fixture.Encoder.Writes}");
    }

    private sealed class LongRunFixture : IDisposable
    {
        private readonly SyntheticOutputCaptureController _outputController = new();
        private readonly SyntheticFailoverCoordinator _failoverCoordinator = new();
        private readonly SyntheticMicCaptureService _micCaptureService = new();
        private readonly IRingBuffer _speakerBuffer = new FloatRingBuffer(48000 * 5);
        private readonly IRingBuffer _micBuffer = new FloatRingBuffer(48000 * 5);
        private readonly IDriftCorrector _driftCorrector = new PiDriftCorrector(2e-8, 1e-12, 300);
        private readonly IVariableRateResampler _resampler = new LinearVariableRateResampler();
        private readonly IRecordingTelemetrySink _telemetry = new NoOpTelemetry();

        public CountingEncoder Encoder { get; } = new();

        public RecordingService CreateService()
        {
            var frameBuilder = new FrameBuilder(_speakerBuffer, _micBuffer, _resampler);
            var factory = new RecordingServiceFactory(NullLogger<RecordingService>.Instance, new[] { _telemetry });
            return (RecordingService)factory.Create(
                _outputController,
                _failoverCoordinator,
                _micCaptureService,
                _speakerBuffer,
                _micBuffer,
                _driftCorrector,
                frameBuilder,
                Encoder);
        }

        public RecordingOptions CreateOptions()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), "VoxArchiveLongRun");
            Directory.CreateDirectory(outputDir);

            return new RecordingOptions
            {
                OutputDirectory = outputDir,
                SpeakerDeviceId = "synthetic-speaker",
                MicDeviceId = "synthetic-mic",
                SampleRate = 48_000,
                FrameMilliseconds = 10,
                TargetBufferMilliseconds = 200,
                OutputCaptureMode = OutputCaptureMode.SpeakerLoopback
            };
        }

        public void Dispose()
        {
            _outputController.Dispose();
            _micCaptureService.Dispose();
        }
    }

    private sealed class SyntheticOutputCaptureController : IOutputCaptureController, IDisposable
    {
        private Timer? _timer;
        private int _sampleRate;

        public OutputCaptureMode CurrentMode { get; private set; } = OutputCaptureMode.SpeakerLoopback;
        public event EventHandler<CaptureChunk>? ChunkCaptured;
        public event EventHandler<OutputSourceChangedEvent>? SourceChanged;

        public Task StartAsync(RecordingOptions options, CancellationToken cancellationToken = default)
        {
            CurrentMode = options.OutputCaptureMode;
            _sampleRate = options.SampleRate;
            _timer?.Dispose();
            _timer = new Timer(_ => PushChunk(), null, 0, 10);
            return Task.CompletedTask;
        }

        public Task SwitchToSpeakerLoopbackAsync(string reason, CancellationToken cancellationToken = default)
        {
            var prev = CurrentMode;
            CurrentMode = OutputCaptureMode.SpeakerLoopback;
            SourceChanged?.Invoke(this, new OutputSourceChangedEvent(prev, CurrentMode, reason, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _timer?.Dispose();
            _timer = null;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }

        private void PushChunk()
        {
            var samples = GenerateTone(480, 440, _sampleRate, 0.18f);
            ChunkCaptured?.Invoke(this, new CaptureChunk(samples, _sampleRate, DateTimeOffset.UtcNow));
        }
    }

    private sealed class SyntheticMicCaptureService : IMicCaptureService, IDisposable
    {
        private Timer? _timer;
        private int _sampleRate;

        public event EventHandler<CaptureChunk>? ChunkCaptured;

        public Task StartAsync(string micDeviceId, int sampleRate, CancellationToken cancellationToken = default)
        {
            _sampleRate = sampleRate;
            _timer?.Dispose();
            _timer = new Timer(_ => PushChunk(), null, 0, 10);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _timer?.Dispose();
            _timer = null;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }

        private void PushChunk()
        {
            var samples = GenerateTone(480, 620, _sampleRate, 0.16f);
            ChunkCaptured?.Invoke(this, new CaptureChunk(samples, _sampleRate, DateTimeOffset.UtcNow));
        }
    }

    private sealed class SyntheticFailoverCoordinator : IOutputCaptureFailoverCoordinator
    {
        public event EventHandler<OutputSourceChangedEvent>? SourceChanged;

        public Task<OutputCaptureMode> ResolveStartupModeAsync(RecordingOptions options, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(options.OutputCaptureMode);
        }

        public Task HandleProcessCaptureUnavailableAsync(CancellationToken cancellationToken = default)
        {
            SourceChanged?.Invoke(this, new OutputSourceChangedEvent(
                OutputCaptureMode.ProcessLoopback,
                OutputCaptureMode.SpeakerLoopback,
                "SyntheticUnavailable",
                DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }
    }

    private sealed class CountingEncoder : IFfmpegFlacEncoder
    {
        public bool IsRunning { get; private set; }
        public int Writes { get; private set; }

        public Task StartAsync(FfmpegFlacEncoderOptions options, CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task WriteAsync(ReadOnlyMemory<byte> pcm16StereoFrame, CancellationToken cancellationToken = default)
        {
            if (IsRunning)
            {
                Writes++;
            }

            return Task.CompletedTask;
        }

        public Task<FfmpegStopResult> StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.FromResult(new FfmpegStopResult(0, true, string.Empty));
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpTelemetry : IRecordingTelemetrySink
    {
        public void OnError(string message)
        {
        }

        public void OnStateChanged(RecordingState state)
        {
        }

        public void OnStatistics(RecordingStatistics statistics)
        {
        }
    }

    private static float[] GenerateTone(int sampleCount, double frequency, int sampleRate, float gain)
    {
        var arr = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)sampleRate;
            arr[i] = (float)(Math.Sin(2 * Math.PI * frequency * t) * gain);
        }

        return arr;
    }
}




