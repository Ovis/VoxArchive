using Microsoft.Extensions.Logging;
using VoxArchive.Application.Abstractions;
using VoxArchive.Audio.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Encoding.Abstractions;
using System.Buffers;

namespace VoxArchive.Application;

public sealed class RecordingService : IRecordingService
{
    private readonly RecordingStateMachine _stateMachine = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IOutputCaptureController _outputCaptureController;
    private readonly IOutputCaptureFailoverCoordinator _failoverCoordinator;
    private readonly IMicCaptureService _micCaptureService;
    private readonly IRingBuffer _speakerBuffer;
    private readonly IRingBuffer _micBuffer;
    private readonly IDriftCorrector _driftCorrector;
    private readonly IFrameBuilder _frameBuilder;
    private readonly IFfmpegFlacEncoder _encoder;
    private readonly ILogger<RecordingService> _logger;
    private readonly IRecordingTelemetrySink? _telemetrySink;

    private CancellationTokenSource? _processingCts;
    private Task? _processingTask;
    private RecordingOptions? _activeOptions;
    private string? _outputPath;
    private DateTimeOffset _startedAt;
    private TimeSpan _accumulatedPausedDuration;
    private DateTimeOffset? _pauseStartedAt;
    private bool _isPaused;
    private long _underflowCount;
    private long _overflowCount;
    private double _lastSpeakerLevel;
    private double _lastMicLevel;
    private volatile bool _speakerCaptureEnabled = true;
    private volatile bool _micCaptureEnabled = true;
    private DateTimeOffset _lastStatisticsLogAt = DateTimeOffset.MinValue;

    public RecordingService(
        IOutputCaptureController outputCaptureController,
        IOutputCaptureFailoverCoordinator failoverCoordinator,
        IMicCaptureService micCaptureService,
        IRingBuffer speakerBuffer,
        IRingBuffer micBuffer,
        IDriftCorrector driftCorrector,
        IFrameBuilder frameBuilder,
        IFfmpegFlacEncoder encoder,
        ILogger<RecordingService> logger,
        IRecordingTelemetrySink? telemetrySink = null
        )
    {
        _outputCaptureController = outputCaptureController;
        _failoverCoordinator = failoverCoordinator;
        _micCaptureService = micCaptureService;
        _speakerBuffer = speakerBuffer;
        _micBuffer = micBuffer;
        _driftCorrector = driftCorrector;
        _frameBuilder = frameBuilder;
        _encoder = encoder;
        _telemetrySink = telemetrySink;
        _logger = logger;

        _outputCaptureController.ChunkCaptured += OnSpeakerChunkCaptured;
        _outputCaptureController.SourceChanged += (_, e) => OutputSourceChanged?.Invoke(this, e);
        _failoverCoordinator.SourceChanged += (_, e) => OutputSourceChanged?.Invoke(this, e);
        _micCaptureService.ChunkCaptured += OnMicChunkCaptured;
    }

    public RecordingState CurrentState => _stateMachine.CurrentState;
    public bool IsSpeakerCaptureEnabled => _speakerCaptureEnabled;
    public bool IsMicCaptureEnabled => _micCaptureEnabled;

    public event EventHandler<RecordingState>? StateChanged;
    public event EventHandler<RecordingStatistics>? StatisticsUpdated;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<OutputSourceChangedEvent>? OutputSourceChanged;

    public void SetSpeakerCaptureEnabled(bool enabled)
    {
        _speakerCaptureEnabled = enabled;
    }

    public void SetMicCaptureEnabled(bool enabled)
    {
        _micCaptureEnabled = enabled;
    }

    public async Task<string> StartAsync(RecordingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ValidateOptions(options);
            TransitionTo(RecordingState.Starting);

            Directory.CreateDirectory(options.OutputDirectory);
            var resolvedMode = await _failoverCoordinator.ResolveStartupModeAsync(options, cancellationToken);
            var effectiveOptions = options with { OutputCaptureMode = resolvedMode };

            _activeOptions = effectiveOptions;
            _outputPath = BuildOutputFilePath(effectiveOptions.OutputDirectory);
            _startedAt = DateTimeOffset.UtcNow;
            _accumulatedPausedDuration = TimeSpan.Zero;
            _pauseStartedAt = null;
            _isPaused = false;
            _underflowCount = 0;
            _overflowCount = 0;
            _speakerBuffer.Clear();
            _micBuffer.Clear();
            _driftCorrector.Configure(effectiveOptions.Kp, effectiveOptions.Ki, effectiveOptions.MaxCorrectionPpm);
            _driftCorrector.Reset();
            ApplyChannelAlignmentPrefill(effectiveOptions);

            await _encoder.StartAsync(new FfmpegFlacEncoderOptions(
                OutputFilePath: _outputPath,
                SampleRate: effectiveOptions.SampleRate,
                Channels: effectiveOptions.ChannelCount,
                CompressionLevel: effectiveOptions.FlacCompressionLevel), cancellationToken);

            await StartCaptureAsync(effectiveOptions, cancellationToken);

            var processingCts = new CancellationTokenSource();
            _processingCts = processingCts;
            _processingTask = Task.Run(() => ProcessingLoopAsync(processingCts.Token));

            TransitionTo(RecordingState.Recording);
            RaiseStatistics();
            return _outputPath;
        }
        catch (Exception ex)
        {
            await TryStopInternalsAsync(CancellationToken.None);
            SafeTransitionToError(ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            TransitionTo(RecordingState.Pausing);
            _pauseStartedAt ??= DateTimeOffset.UtcNow;
            _isPaused = true;
            await StopCaptureAsync(cancellationToken);
            TransitionTo(RecordingState.Paused);
        }
        catch (Exception ex)
        {
            SafeTransitionToError(ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_activeOptions is null)
            {
                throw new InvalidOperationException("Active recording options are not available.");
            }

            await StartCaptureAsync(_activeOptions, cancellationToken);
            AccumulatePauseDuration();
            _isPaused = false;
            TransitionTo(RecordingState.Recording);
        }
        catch (Exception ex)
        {
            SafeTransitionToError(ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            TransitionTo(RecordingState.Stopping);
            await TryStopInternalsAsync(cancellationToken);
            AccumulatePauseDuration();
            TransitionTo(RecordingState.Stopped);
            RaiseStatistics();
        }
        catch (Exception ex)
        {
            SafeTransitionToError(ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartCaptureAsync(RecordingOptions options, CancellationToken cancellationToken)
    {
        await _outputCaptureController.StartAsync(options, cancellationToken);
        await _micCaptureService.StartAsync(options.MicDeviceId, options.SampleRate, cancellationToken);
    }

    private async Task StopCaptureAsync(CancellationToken cancellationToken)
    {
        await _outputCaptureController.StopAsync(cancellationToken);
        await _micCaptureService.StopAsync(cancellationToken);
    }

    private async Task TryStopInternalsAsync(CancellationToken cancellationToken)
    {
        _isPaused = true;

        if (_processingCts is not null)
        {
            await _processingCts.CancelAsync();
            _processingCts.Dispose();
            _processingCts = null;
        }

        if (_processingTask is not null)
        {
            try
            {
                await _processingTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 停止要求で処理ループ待機が中断された場合は正常系として扱う。
            }

            _processingTask = null;
        }

        await StopCaptureAsync(cancellationToken);
        var stopResult = await _encoder.StopAsync(cancellationToken);
        if (!stopResult.IsSuccess)
        {
            throw new InvalidOperationException($"ffmpeg exited with code {stopResult.ExitCode}. {stopResult.StandardError}");
        }
    }

    private async Task ProcessingLoopAsync(CancellationToken cancellationToken)
    {
        if (_activeOptions is null)
        {
            return;
        }

        var frameSamples = (_activeOptions.SampleRate * _activeOptions.FrameMilliseconds) / 1000;
        var effectiveTargetBufferMs = Math.Clamp(_activeOptions.TargetBufferMilliseconds, 40, 120);
        var targetFillSamples = (_activeOptions.SampleRate * effectiveTargetBufferMs) / 1000;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_activeOptions.FrameMilliseconds));

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (_isPaused)
            {
                continue;
            }

            var ratio = _driftCorrector.ComputeRatio(_micBuffer.Count, targetFillSamples, TimeSpan.FromMilliseconds(_activeOptions.FrameMilliseconds));
            var frame = _frameBuilder.BuildFrame(frameSamples, ratio);
            await _encoder.WriteAsync(frame.InterleavedPcm16, cancellationToken);

            _lastSpeakerLevel = frame.SpeakerLevel;
            _lastMicLevel = frame.MicLevel;
            _underflowCount += frame.UnderflowSamples;
            _overflowCount += frame.OverflowSamples;
            RaiseStatistics();
        }
    }

    private void OnSpeakerChunkCaptured(object? sender, CaptureChunk chunk)
    {
        if (!_speakerCaptureEnabled)
        {
            WriteSilence(_speakerBuffer, chunk.Samples.Length);
            return;
        }

        WriteChunkWithRateNormalization(_speakerBuffer, chunk);
    }

    private void OnMicChunkCaptured(object? sender, CaptureChunk chunk)
    {
        if (!_micCaptureEnabled)
        {
            WriteSilence(_micBuffer, chunk.Samples.Length);
            return;
        }

        WriteChunkWithRateNormalization(_micBuffer, chunk);
    }

    private static void WriteSilence(IRingBuffer buffer, int samples)
    {
        if (samples <= 0)
        {
            return;
        }

        var rented = ArrayPool<float>.Shared.Rent(Math.Min(samples, 4096));
        Array.Clear(rented, 0, rented.Length);
        try
        {
            var remaining = samples;
            while (remaining > 0)
            {
                var len = Math.Min(remaining, rented.Length);
                WriteSamples(buffer, rented.AsSpan(0, len));
                remaining -= len;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    private void WriteChunkWithRateNormalization(IRingBuffer buffer, CaptureChunk chunk)
    {
        var targetRate = _activeOptions?.SampleRate ?? chunk.SampleRate;
        if (chunk.SampleRate <= 0 || targetRate <= 0 || chunk.SampleRate == targetRate)
        {
            WriteSamples(buffer, chunk.Samples.Span);
            return;
        }

        var converted = ResampleLinear(chunk.Samples.Span, chunk.SampleRate, targetRate);
        WriteSamples(buffer, converted);
    }

    private static float[] ResampleLinear(ReadOnlySpan<float> source, int sourceRate, int targetRate)
    {
        if (source.IsEmpty)
        {
            return Array.Empty<float>();
        }

        if (sourceRate <= 0 || targetRate <= 0 || sourceRate == targetRate)
        {
            return source.ToArray();
        }

        var outputLength = Math.Max(1, (int)Math.Round(source.Length * (double)targetRate / sourceRate));
        var output = new float[outputLength];

        if (source.Length == 1 || outputLength == 1)
        {
            output.AsSpan().Fill(source[0]);
            return output;
        }

        var step = (double)(source.Length - 1) / (outputLength - 1);
        for (var i = 0; i < outputLength; i++)
        {
            var srcPos = i * step;
            var idx = (int)srcPos;
            var frac = (float)(srcPos - idx);

            var s0 = source[idx];
            var s1 = source[Math.Min(idx + 1, source.Length - 1)];
            output[i] = s0 + ((s1 - s0) * frac);
        }

        return output;
    }


    private void ApplyChannelAlignmentPrefill(RecordingOptions options)
    {
        var alignmentMs = Math.Clamp(options.ChannelAlignmentMilliseconds, -1000, 1000);
        if (alignmentMs == 0)
        {
            return;
        }

        var samples = (int)Math.Round(options.SampleRate * (Math.Abs(alignmentMs) / 1000d));
        if (samples <= 0)
        {
            return;
        }

        // +ms: speaker を遅延, -ms: mic を遅延
        var targetBuffer = alignmentMs > 0 ? _speakerBuffer : _micBuffer;
        var chunk = new float[Math.Min(samples, 4096)];
        var remaining = samples;
        while (remaining > 0)
        {
            var len = Math.Min(remaining, chunk.Length);
            WriteSamples(targetBuffer, chunk.AsSpan(0, len));
            remaining -= len;
        }
    }
    private static void WriteSamples(IRingBuffer buffer, ReadOnlySpan<float> samples)
    {
        var offset = 0;
        while (offset < samples.Length)
        {
            var written = buffer.Write(samples.Slice(offset));
            if (written <= 0)
            {
                break;
            }

            offset += written;
        }
    }

    private void TransitionTo(RecordingState next)
    {
        _stateMachine.Transition(next);
        if (IsTelemetryEnabled())
        {
            _telemetrySink?.OnStateChanged(next);
            _logger?.LogInformation("state={State}", next);
        }
        StateChanged?.Invoke(this, next);
    }

    private void SafeTransitionToError(string message)
    {
        if (_stateMachine.TryTransition(RecordingState.Error, out _))
        {
            if (IsTelemetryEnabled())
            {
                _telemetrySink?.OnStateChanged(RecordingState.Error);
                _logger?.LogWarning("state={State}", RecordingState.Error);
            }
            StateChanged?.Invoke(this, RecordingState.Error);
        }

        if (IsTelemetryEnabled())
        {
            _telemetrySink?.OnError(message);
            _logger?.LogWarning("error={Message}", message);
        }
        ErrorOccurred?.Invoke(this, message);
    }

    private void RaiseStatistics()
    {
        var startedAt = _startedAt == default ? DateTimeOffset.UtcNow : _startedAt;
        var elapsed = DateTimeOffset.UtcNow - startedAt - GetCurrentPausedDuration();
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var statistics = new RecordingStatistics
        {
            ElapsedTime = elapsed,
            OutputFilePath = _outputPath,
            SpeakerBufferMilliseconds = SamplesToMs(_speakerBuffer.Count),
            MicBufferMilliseconds = SamplesToMs(_micBuffer.Count),
            DriftCorrectionPpm = _driftCorrector.CurrentPpm,
            UnderflowCount = _underflowCount,
            OverflowCount = _overflowCount,
            SpeakerLevel = _lastSpeakerLevel,
            MicLevel = _lastMicLevel
        };

        if (IsTelemetryEnabled())
        {
            _telemetrySink?.OnStatistics(statistics);

            var now = DateTimeOffset.UtcNow;
            if ((now - _lastStatisticsLogAt).TotalSeconds >= 1)
            {
                _lastStatisticsLogAt = now;
                _logger?.LogInformation(
                    "stats elapsed={Elapsed} spkBufMs={SpeakerBufferMs:F0} micBufMs={MicBufferMs:F0} ppm={DriftPpm:F1} uf={Underflow} of={Overflow} spkLv={SpeakerLevel:F3} micLv={MicLevel:F3}",
                    statistics.ElapsedTime,
                    statistics.SpeakerBufferMilliseconds,
                    statistics.MicBufferMilliseconds,
                    statistics.DriftCorrectionPpm,
                    statistics.UnderflowCount,
                    statistics.OverflowCount,
                    statistics.SpeakerLevel,
                    statistics.MicLevel);
            }
        }
        StatisticsUpdated?.Invoke(this, statistics);
    }

    private bool IsTelemetryEnabled()
    {
        return _activeOptions?.RecordingMetricsLogEnabled == true;
    }


    private void AccumulatePauseDuration()
    {
        if (_pauseStartedAt is not DateTimeOffset pauseStartedAt)
        {
            return;
        }

        var pauseDuration = DateTimeOffset.UtcNow - pauseStartedAt;
        if (pauseDuration > TimeSpan.Zero)
        {
            _accumulatedPausedDuration += pauseDuration;
        }

        _pauseStartedAt = null;
    }

    private TimeSpan GetCurrentPausedDuration()
    {
        if (_pauseStartedAt is DateTimeOffset pauseStartedAt)
        {
            var inProgressPause = DateTimeOffset.UtcNow - pauseStartedAt;
            if (inProgressPause > TimeSpan.Zero)
            {
                return _accumulatedPausedDuration + inProgressPause;
            }
        }

        return _accumulatedPausedDuration;
    }
    private static void ValidateOptions(RecordingOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            throw new ArgumentException("OutputDirectory is required.", nameof(options));
        }

        if (options.SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.SampleRate));
        }

        if (options.FrameMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.FrameMilliseconds));
        }

        if (options.OutputCaptureMode == OutputCaptureMode.ProcessLoopback && options.TargetProcessId is null)
        {
            throw new ArgumentException("TargetProcessId is required for process loopback mode.", nameof(options));
        }
    }

    private static string BuildOutputFilePath(string directory)
    {
        var now = DateTime.Now;
        var baseName = now.ToString("yyyyMMddHHmmssfff");
        var path = Path.Combine(directory, baseName + ".flac");
        if (!File.Exists(path))
        {
            return path;
        }

        var index = 1;
        while (true)
        {
            var candidate = Path.Combine(directory, $"{baseName}_{index}.flac");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            index++;
        }
    }

    private double SamplesToMs(int samples)
    {
        if (_activeOptions is null || _activeOptions.SampleRate <= 0)
        {
            return 0;
        }

        return (samples * 1000d) / _activeOptions.SampleRate;
    }
}

