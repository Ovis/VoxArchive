using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// Audio EditorのOriginal / Edited Previewを管理する。
/// </summary>
/// <remarks>
/// Edited PreviewとSolo確認は共通DSPを再生時に適用する。
/// Soloは監視状態だけから一時的なMuteを組み立て、編集状態そのものは変更しない。
/// </remarks>
public sealed class AudioEditorPreviewService : IDisposable
{
    private readonly IRecordingPlaybackService _playback;
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

    public Task PlayEditedAsync(
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
        cancellationToken.ThrowIfCancellationRequested();
        EnsureLoadedPreview(sourceFilePath, previewState, previewMode, soloChannel, isOriginal: false);

        _isEditedMode = true;
        ConfigureAndPlay(speed);
        return Task.CompletedTask;
    }

    public Task PlayOriginalAsync(
        string sourceFilePath,
        AudioEditState state,
        int? soloChannel,
        double speed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        if (soloChannel is null)
        {
            if (_isEditedMode || !_playback.IsLoaded || _renderedOriginal)
            {
                _playback.Load(sourceFilePath);
                _renderedState = null;
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
            EnsureLoadedPreview(
                sourceFilePath,
                originalState,
                AudioRenderChannelMode.MonoMixdown,
                soloChannel,
                isOriginal: true);
        }

        _isEditedMode = false;
        ConfigureAndPlay(speed);
        return Task.CompletedTask;
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
    }

    public void InvalidateMonitorPreview()
    {
        _renderedState = null;
    }

    private void EnsureLoadedPreview(
        string sourceFilePath,
        AudioEditState state,
        AudioRenderChannelMode channelMode,
        int? soloChannel,
        bool isOriginal)
    {
        var cacheValid = _renderedState is not null
            && _renderedState.Equals(state)
            && _renderedChannelMode == channelMode
            && _renderedSoloChannel == soloChannel
            && _renderedOriginal == isOriginal
            && _playback.IsLoaded
            && _isEditedMode != isOriginal;
        if (cacheValid) return;

        _playback.LoadEdited(sourceFilePath, state, channelMode);
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

    public void Dispose()
    {
        _playback.Dispose();
    }
}
