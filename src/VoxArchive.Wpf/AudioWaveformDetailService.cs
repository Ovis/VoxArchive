using System.IO;
using NAudio.Wave;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// Zoom中のViewportだけを高解像度で再解析し、編集セッション内にキャッシュする。
/// </summary>
/// <remarks>
/// 初期粗波形は常に即時Fallbackとして利用し、高解像度生成中も編集操作を止めない。
/// Viewport移動時は古い生成をCancelし、完了済み結果だけをメモリへ保持する。
/// </remarks>
public sealed class AudioWaveformDetailService : IDisposable
{
    private const int MaxCacheEntries = 24;
    private readonly string _filePath;
    private readonly Dictionary<CacheKey, AudioWaveformDetailResult> _cache = new();
    private readonly LinkedList<CacheKey> _lru = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _activeRequest;

    public AudioWaveformDetailService(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    public async Task<AudioWaveformDetailResult?> GetAsync(
        AudioWaveformViewport viewport,
        int requestedBuckets,
        CancellationToken cancellationToken = default)
    {
        requestedBuckets = Math.Clamp(requestedBuckets, 256, 12_000);
        var key = CacheKey.Create(viewport, requestedBuckets);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                Touch(key);
                return cached;
            }

            _activeRequest?.Cancel();
            _activeRequest?.Dispose();
            _activeRequest = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        CancellationToken token;
        lock (_gate) token = _activeRequest!.Token;

        try
        {
            var result = await Task.Run(() => AnalyzeRegion(viewport, requestedBuckets, token), token);
            lock (_gate)
            {
                _cache[key] = result;
                Touch(key);
                TrimCache();
            }
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
    }

    private AudioWaveformDetailResult AnalyzeRegion(AudioWaveformViewport viewport, int bucketCount, CancellationToken cancellationToken)
    {
        using var reader = new AudioFileReader(_filePath);
        var channels = reader.WaveFormat.Channels;
        if (channels is < 1 or > 2) throw new NotSupportedException("Audio EditorはMonoまたはStereo音声のみを扱います。");

        var sampleRate = reader.WaveFormat.SampleRate;
        var startFrame = AudioRenderPlan.TimeToFrameIndex(viewport.Start, sampleRate);
        var endFrame = AudioRenderPlan.TimeToFrameIndex(viewport.End, sampleRate);
        var frameCount = Math.Max(1L, endFrame - startFrame);
        bucketCount = (int)Math.Min(bucketCount, frameCount);

        var mins = new float[channels][];
        var maxs = new float[channels][];
        for (var channel = 0; channel < channels; channel++)
        {
            mins[channel] = Enumerable.Repeat(float.PositiveInfinity, bucketCount).ToArray();
            maxs[channel] = Enumerable.Repeat(float.NegativeInfinity, bucketCount).ToArray();
        }

        reader.Position = Math.Min(reader.Length, checked(startFrame * reader.WaveFormat.BlockAlign));
        var buffer = new float[4096 * channels];
        long relativeFrame = 0;
        while (relativeFrame < frameCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wantedFrames = (int)Math.Min(4096L, frameCount - relativeFrame);
            var readSamples = reader.Read(buffer, 0, wantedFrames * channels);
            if (readSamples <= 0) break;
            var readFrames = readSamples / channels;

            for (var frame = 0; frame < readFrames; frame++, relativeFrame++)
            {
                var bucket = (int)Math.Min(bucketCount - 1, relativeFrame * bucketCount / frameCount);
                for (var channel = 0; channel < channels; channel++)
                {
                    var sample = buffer[(frame * channels) + channel];
                    if (sample < mins[channel][bucket]) mins[channel][bucket] = sample;
                    if (sample > maxs[channel][bucket]) maxs[channel][bucket] = sample;
                }
            }
        }

        for (var channel = 0; channel < channels; channel++)
        {
            for (var bucket = 0; bucket < bucketCount; bucket++)
            {
                if (float.IsPositiveInfinity(mins[channel][bucket])) mins[channel][bucket] = 0f;
                if (float.IsNegativeInfinity(maxs[channel][bucket])) maxs[channel][bucket] = 0f;
            }
        }

        return new AudioWaveformDetailResult(
            viewport,
            Enumerable.Range(0, channels)
                .Select(channel => new AudioWaveformEnvelope(mins[channel], maxs[channel]))
                .ToArray());
    }

    private void Touch(CacheKey key)
    {
        var node = _lru.Find(key);
        if (node is not null) _lru.Remove(node);
        _lru.AddFirst(key);
    }

    private void TrimCache()
    {
        while (_lru.Count > MaxCacheEntries)
        {
            var key = _lru.Last!.Value;
            _lru.RemoveLast();
            _cache.Remove(key);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _activeRequest?.Cancel();
            _activeRequest?.Dispose();
            _activeRequest = null;
            _cache.Clear();
            _lru.Clear();
        }
    }

    private readonly record struct CacheKey(long Start10Ms, long End10Ms, int Buckets)
    {
        public static CacheKey Create(AudioWaveformViewport viewport, int buckets)
            => new(viewport.Start.Ticks / TimeSpan.TicksPerMillisecond / 10,
                viewport.End.Ticks / TimeSpan.TicksPerMillisecond / 10,
                buckets);
    }
}

public sealed record AudioWaveformDetailResult(
    AudioWaveformViewport Viewport,
    IReadOnlyList<AudioWaveformEnvelope> Envelopes);
