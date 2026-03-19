using VoxArchive.Application;
using VoxArchive.Application.Abstractions;
using VoxArchive.Audio.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Encoding.Abstractions;
using VoxArchive.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace VoxArchive.IntegrationTests;

public sealed class RecordingServiceIntegrationTests
{
    [Test]
    public async Task StartAndStop_TransitionsAndEmitsOutputPath()
    {
        var fixture = new RecordingFixture();
        var sut = fixture.CreateService();

        var options = fixture.CreateOptions();
        var states = new List<RecordingState>();
        sut.StateChanged += (_, s) => states.Add(s);

        var outputPath = await sut.StartAsync(options);
        await Task.Delay(80);
        await sut.StopAsync();

        Assert.That(outputPath, Does.Match(@"\d{17}(?:_\d+)?\.flac$"));
        Assert.That(states, Contains.Item(RecordingState.Starting));
        Assert.That(states, Contains.Item(RecordingState.Recording));
        Assert.That(states, Contains.Item(RecordingState.Stopping));
        Assert.That(states, Contains.Item(RecordingState.Stopped));
        Assert.That(fixture.Encoder.StartCalled, Is.True);
        Assert.That(fixture.Encoder.StopCalled, Is.True);
        Assert.That(fixture.Encoder.Writes, Is.GreaterThan(0));
    }

    [Test]
    public async Task PauseResume_ChangesState()
    {
        var fixture = new RecordingFixture();
        var sut = fixture.CreateService();

        var options = fixture.CreateOptions();

        await sut.StartAsync(options);
        await sut.PauseAsync();
        Assert.That(sut.CurrentState, Is.EqualTo(RecordingState.Paused));

        await sut.ResumeAsync();
        Assert.That(sut.CurrentState, Is.EqualTo(RecordingState.Recording));

        await sut.StopAsync();
    }
    [Test]
    public async Task Start_PreservesPreconfiguredMuteFlags()
    {
        var fixture = new RecordingFixture();
        var sut = fixture.CreateService();

        sut.SetSpeakerCaptureEnabled(false);
        sut.SetMicCaptureEnabled(false);

        var options = fixture.CreateOptions();

        await sut.StartAsync(options);
        Assert.That(sut.IsSpeakerCaptureEnabled, Is.False);
        Assert.That(sut.IsMicCaptureEnabled, Is.False);

        await Task.Delay(50);
        await sut.StopAsync();

        Assert.That(fixture.SpeakerBuffer.Count, Is.GreaterThan(0));
        Assert.That(fixture.MicBuffer.Count, Is.GreaterThan(0));

        var speaker = new float[fixture.SpeakerBuffer.Count];
        fixture.SpeakerBuffer.Read(speaker);
        Assert.That(speaker.All(x => x == 0f), Is.True);

        var mic = new float[fixture.MicBuffer.Count];
        fixture.MicBuffer.Read(mic);
        Assert.That(mic.All(x => x == 0f), Is.True);
    }

    [Test]
    public async Task OutputSourceChanged_IsForwarded()
    {
        var fixture = new RecordingFixture();
        var sut = fixture.CreateService();
        var options = fixture.CreateOptions() with
        {
            OutputCaptureMode = OutputCaptureMode.ProcessLoopback,
            TargetProcessId = 1234
        };

        var events = new List<OutputSourceChangedEvent>();
        sut.OutputSourceChanged += (_, e) => events.Add(e);

        await sut.StartAsync(options);
        fixture.OutputController.RaiseSourceChanged(new OutputSourceChangedEvent(
            OutputCaptureMode.ProcessLoopback,
            OutputCaptureMode.SpeakerLoopback,
            "TargetProcessExited",
            DateTimeOffset.UtcNow));

        await sut.StopAsync();

        Assert.That(events.Any(x => x.Reason == "TargetProcessExited"), Is.True);
    }

    [Test]
    public async Task Start_WithProcessModeWithoutPid_Throws()
    {
        var fixture = new RecordingFixture();
        var sut = fixture.CreateService();

        var options = fixture.CreateOptions() with
        {
            OutputCaptureMode = OutputCaptureMode.ProcessLoopback,
            TargetProcessId = null
        };

        Assert.ThrowsAsync<ArgumentException>(async () => await sut.StartAsync(options));
    }

    private sealed class RecordingFixture
    {
        public FakeOutputCaptureController OutputController { get; } = new();
        public FakeFailoverCoordinator FailoverCoordinator { get; } = new();
        public FakeMicCaptureService MicCaptureService { get; } = new();
        public FakeRingBuffer SpeakerBuffer { get; } = new();
        public FakeRingBuffer MicBuffer { get; } = new();
        public FakeDriftCorrector DriftCorrector { get; } = new();
        public FakeFrameBuilder FrameBuilder { get; } = new();
        public FakeEncoder Encoder { get; } = new();
        public FakeTelemetry Telemetry { get; } = new();

        public RecordingService CreateService()
        {
            var factory = new RecordingServiceFactory(NullLogger<RecordingService>.Instance, new[] { Telemetry });
            return (RecordingService)factory.Create(
                OutputController,
                FailoverCoordinator,
                MicCaptureService,
                SpeakerBuffer,
                MicBuffer,
                DriftCorrector,
                FrameBuilder,
                Encoder);
        }

        public RecordingOptions CreateOptions()
        {
            var dir = Path.Combine(Path.GetTempPath(), "VoxArchiveTests");
            Directory.CreateDirectory(dir);

            return new RecordingOptions
            {
                OutputDirectory = dir,
                SpeakerDeviceId = "speaker-1",
                MicDeviceId = "mic-1",
                FrameMilliseconds = 10,
                SampleRate = 48_000
            };
        }
    }

    private sealed class FakeOutputCaptureController : IOutputCaptureController
    {
        public OutputCaptureMode CurrentMode { get; private set; } = OutputCaptureMode.SpeakerLoopback;

        public event EventHandler<CaptureChunk>? ChunkCaptured;
        public event EventHandler<OutputSourceChangedEvent>? SourceChanged;

        public Task StartAsync(RecordingOptions options, CancellationToken cancellationToken = default)
        {
            CurrentMode = options.OutputCaptureMode;
            var samples = Enumerable.Repeat(0.1f, 480).ToArray();
            ChunkCaptured?.Invoke(this, new CaptureChunk(samples, options.SampleRate, DateTimeOffset.UtcNow));
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
            return Task.CompletedTask;
        }

        public void RaiseSourceChanged(OutputSourceChangedEvent ev)
        {
            SourceChanged?.Invoke(this, ev);
        }
    }

    private sealed class FakeFailoverCoordinator : IOutputCaptureFailoverCoordinator
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
                "Unavailable",
                DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMicCaptureService : IMicCaptureService
    {
        public event EventHandler<CaptureChunk>? ChunkCaptured;

        public Task StartAsync(string micDeviceId, int sampleRate, CancellationToken cancellationToken = default)
        {
            var samples = Enumerable.Repeat(0.15f, 480).ToArray();
            ChunkCaptured?.Invoke(this, new CaptureChunk(samples, sampleRate, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRingBuffer : IRingBuffer
    {
        private readonly Queue<float> _samples = new();

        public int Capacity => 1_000_000;
        public int Count => _samples.Count;

        public int Write(ReadOnlySpan<float> source)
        {
            foreach (var value in source)
            {
                _samples.Enqueue(value);
            }

            return source.Length;
        }

        public int Read(Span<float> destination)
        {
            var i = 0;
            while (i < destination.Length && _samples.Count > 0)
            {
                destination[i++] = _samples.Dequeue();
            }

            return i;
        }

        public int ReadWithZeroPadding(Span<float> destination)
        {
            var read = Read(destination);
            if (read < destination.Length)
            {
                destination.Slice(read).Clear();
            }

            return destination.Length;
        }

        public void Clear()
        {
            _samples.Clear();
        }
    }

    private sealed class FakeDriftCorrector : IDriftCorrector
    {
        public double CurrentPpm { get; private set; }

        public void Configure(double kp, double ki, double maxCorrectionPpm)
        {
        }

        public void Reset()
        {
            CurrentPpm = 0;
        }

        public double ComputeRatio(int micFillSamples, int targetFillSamples, TimeSpan deltaTime)
        {
            return 1.0;
        }
    }

    private sealed class FakeFrameBuilder : IFrameBuilder
    {
        public FrameBuildResult BuildFrame(int frameSamples, double micRatio)
        {
            var payload = new byte[Math.Max(4, frameSamples * 4)];
            return new FrameBuildResult(
                payload,
                frameSamples,
                frameSamples,
                frameSamples,
                0,
                0,
                (micRatio - 1) * 1_000_000,
                0.22,
                0.33);
        }
    }

    private sealed class FakeEncoder : IFfmpegFlacEncoder
    {
        public bool IsRunning { get; private set; }
        public bool StartCalled { get; private set; }
        public bool StopCalled { get; private set; }
        public int Writes { get; private set; }

        public Task StartAsync(FfmpegFlacEncoderOptions options, CancellationToken cancellationToken = default)
        {
            StartCalled = true;
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
            StopCalled = true;
            IsRunning = false;
            return Task.FromResult(new FfmpegStopResult(0, true, string.Empty));
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTelemetry : IRecordingTelemetrySink
    {
        public void OnStateChanged(RecordingState state)
        {
        }

        public void OnStatistics(RecordingStatistics statistics)
        {
        }

        public void OnError(string message)
        {
        }
    }
}



