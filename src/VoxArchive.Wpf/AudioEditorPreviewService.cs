using System.IO;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// Audio EditorのOriginal / Edited Previewを管理する。
/// </summary>
/// <remarks>
/// Edited PreviewとSolo確認は共通Rendering Pipelineで一時WAVを生成して再生する。
/// Soloは監視状態だけから一時的なMuteを組み立て、編集状態そのものは変更しない。
/// </remarks>
public sealed class AudioEditorPreviewService : IDisposable
{
    private readonly IRecordingPlaybackService _playback;
    private string? _renderedPreviewPath;
    private AudioEditState? _renderedState;
    private AudioRenderChannelMode _renderedChannelMode;
    private int? _renderedSoloChannel;
    private bool _renderedOriginal;
    private bool _isEditedMode = true;

    public AudioEditorPreviewService(IRecordingPlaybackService playback)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
    }

    public bool IsPlaying => _playback.IsPlaying;
    public bool IsLoaded => _playback.IsLoaded;
    public TimeSpan Position => _playback.Position;
    public TimeSpan Duration => _playback.Duration;
    public bool IsEditedMode => _isEditedMode;

    public async Task PlayEditedAsync(
        string sourceFilePath,
        AudioEditState state,
        AudioRenderChannelMode channelMode,
        int? soloChannel,
        double speed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.HasOutputAudio && soloChannel is null)
        {
            throw new InvalidOperationException("編集後音声が空、または全チャンネルが無音のため再生できません。");
        }

        var previewState = ApplySolo(state, soloChannel);
        var previewMode = soloChannel.HasValue ? AudioRenderChannelMode.MonoMixdown : channelMode;
        await EnsureRenderedPreviewAsync(sourceFilePath, previewState, previewMode, soloChannel, isOriginal: false, cancellationToken);

        if (!_isEditedMode || !_playback.IsLoaded)
        {
            _playback.Load(_renderedPreviewPath!);
        }

        _isEditedMode = true;
        ConfigureAndPlay(speed);
    }

    public async Task PlayOriginalAsync(
        string sourceFilePath,
        AudioEditState state,
        int? soloChannel,
        double speed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (soloChannel is null)
        {
            if (_isEditedMode || !_playback.IsLoaded || _renderedOriginal)
            {
                _playback.Load(sourceFilePath);
            }
        }
        else
        {
            var originalState = new AudioEditState(
                state.SourceDuration,
                state.ChannelCount,
                channelStates: Enumerable.Range(0, state.ChannelCount)
                    .Select(index => new AudioChannelEditState(0d, index != soloChannel.Value))
                    .ToArray());
            await EnsureRenderedPreviewAsync(
                sourceFilePath,
                originalState,
                AudioRenderChannelMode.MonoMixdown,
                soloChannel,
                isOriginal: true,
                cancellationToken);
            _playback.Load(_renderedPreviewPath!);
        }

        _isEditedMode = false;
        ConfigureAndPlay(speed);
    }

    public void Pause() => _playback.Pause();

    public void Stop() => _playback.Stop();

    public void Seek(TimeSpan position) => _playback.Seek(position);

    public void SetPlaybackSpeed(double speed) => _playback.SetPlaybackSpeed(speed);

    public void InvalidateEditedPreview()
    {
        _renderedState = null;
        if (_isEditedMode && _playback.IsLoaded)
        {
            _playback.Unload();
        }
        DeleteRenderedPreview();
    }

    public void InvalidateMonitorPreview()
    {
        _renderedState = null;
        DeleteRenderedPreview();
    }

    private async Task EnsureRenderedPreviewAsync(
        string sourceFilePath,
        AudioEditState state,
        AudioRenderChannelMode channelMode,
        int? soloChannel,
        bool isOriginal,
        CancellationToken cancellationToken)
    {
        var cacheValid = _renderedState is not null
            && _renderedState.Equals(state)
            && _renderedChannelMode == channelMode
            && _renderedSoloChannel == soloChannel
            && _renderedOriginal == isOriginal
            && !string.IsNullOrWhiteSpace(_renderedPreviewPath)
            && File.Exists(_renderedPreviewPath);
        if (cacheValid) return;

        _playback.Unload();
        DeleteRenderedPreview();
        var path = Path.Combine(Path.GetTempPath(), $"voxarchive-editor-preview-{Guid.NewGuid():N}.wav");
        await AudioFileRenderService.RenderWaveAsync(
            sourceFilePath,
            path,
            state,
            channelMode,
            masterGainDb: 0d,
            cancellationToken: cancellationToken,
            autoAttenuate: false);
        _renderedPreviewPath = path;
        _renderedState = state;
        _renderedChannelMode = channelMode;
        _renderedSoloChannel = soloChannel;
        _renderedOriginal = isOriginal;
    }

    private void ConfigureAndPlay(double speed)
    {
        _playback.SetPlaybackSpeed(speed);
        _playback.SetGains(0d, 0d);
        _playback.SetMixToMono(false);
        _playback.Play();
    }

    private static AudioEditState ApplySolo(AudioEditState state, int? soloChannel)
    {
        if (!soloChannel.HasValue) return state;
        if (soloChannel < 0 || soloChannel >= state.ChannelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(soloChannel));
        }

        var result = state;
        for (var i = 0; i < state.ChannelCount; i++)
        {
            if (i == soloChannel.Value) continue;
            var channel = result.Channels[i];
            result = result.WithChannelState(i, new AudioChannelEditState(channel.GainDb, true));
        }
        return result;
    }

    private void DeleteRenderedPreview()
    {
        if (string.IsNullOrWhiteSpace(_renderedPreviewPath)) return;
        try
        {
            if (File.Exists(_renderedPreviewPath)) File.Delete(_renderedPreviewPath);
        }
        catch
        {
            // Preview一時ファイルの削除失敗はEditorの終了を妨げない。
        }
        _renderedPreviewPath = null;
    }

    public void Dispose()
    {
        _playback.Dispose();
        DeleteRenderedPreview();
    }
}
