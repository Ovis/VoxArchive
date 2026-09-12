using System.IO;
using System.Text;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// Audio Editorセッション内で編集後ピーク解析結果を再利用する。
/// </summary>
/// <remarks>
/// 元ファイルのLength/LastWriteUtc、編集状態、出力モードをKeyへ含める。
/// Undo/Redoで既出状態へ戻った場合や、Export直前にUIで同一状態を解析済みの場合の全体再走査を避ける。
/// </remarks>
public sealed class AudioPeakAnalysisCache
{
    private const int Capacity = 16;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();

    public async Task<AudioPeakAssessment> GetOrAnalyzeAsync(
        string inputFilePath,
        AudioEditState state,
        AudioRenderChannelMode channelMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFilePath);
        ArgumentNullException.ThrowIfNull(state);

        var key = BuildKey(inputFilePath, state, channelMode);
        if (TryGet(key, out var cached)) return cached;

        var analysis = await AudioFileRenderService.AnalyzeAsync(inputFilePath, state, channelMode, cancellationToken);
        var assessment = AudioPeakAssessment.FromPeak(analysis.PeakAbsoluteSample);
        Add(key, assessment);
        return assessment;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _lru.Clear();
        }
    }

    private bool TryGet(string key, out AudioPeakAssessment assessment)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                assessment = default;
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            assessment = node.Value.Assessment;
            return true;
        }
    }

    private void Add(string key, AudioPeakAssessment assessment)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.Value = new Entry(key, assessment);
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<Entry>(new Entry(key, assessment));
            _lru.AddFirst(node);
            _entries[key] = node;

            while (_entries.Count > Capacity && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _entries.Remove(last.Value.Key);
            }
        }
    }

    private static string BuildKey(string inputFilePath, AudioEditState state, AudioRenderChannelMode channelMode)
    {
        var file = new FileInfo(inputFilePath);
        if (!file.Exists) throw new FileNotFoundException("元音声ファイルが見つかりません。", inputFilePath);

        var builder = new StringBuilder(256);
        builder.Append(Path.GetFullPath(inputFilePath).ToUpperInvariant())
            .Append('|').Append(file.Length)
            .Append('|').Append(file.LastWriteTimeUtc.Ticks)
            .Append('|').Append((int)channelMode)
            .Append('|').Append(state.SourceDuration.Ticks)
            .Append('|').Append(state.ChannelCount);

        foreach (var range in state.CutRanges)
        {
            builder.Append("|C:").Append(range.Start.Ticks).Append(':').Append(range.End.Ticks);
        }

        foreach (var channel in state.Channels)
        {
            builder.Append("|G:").Append(BitConverter.DoubleToInt64Bits(channel.GainDb))
                .Append(':').Append(channel.IsMuted ? '1' : '0');
        }

        return builder.ToString();
    }

    private readonly record struct Entry(string Key, AudioPeakAssessment Assessment);
}
