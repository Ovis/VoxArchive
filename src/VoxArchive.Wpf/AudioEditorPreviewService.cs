using System.IO;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// Audio EditorのOriginal / Edited Previewを管理する。
/// </summary>
/// <remarks>
/// Edited Previewは共通Rendering Pipelineで一時WAVを生成して再生する。
/// これによりCut/Crossfade/Gain/Muteの順序を最終書き出しと一致させる。
/// </remarks>
public sealed class AudioEditorPreviewService : IDisposable
{
    private readonly IRecordingPlaybackService _playback;
    private string? _editedPreviewPath;
    private AudioEditState? _renderedState;
    private bool _isEditedMode = true;

    public AudioEditorPreviewService(IRecordingPlaybackService playback)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
    }

    public bool IsPlaying => _playback.IsPlaying;
    public TimeSpan Position => _playback.Position;
    public TimeSpan Duration => _playback.Duration;
    public bool IsEditedMode => _isEditedMode;

    public async Task PlayEditedAsync(string sourceFilePath, AudioEditState state, double speed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.HasOutputAudio)
        {
            throw new InvalidOperationException("編集後音声が空、または全チャンネルが無音のため再生できません。");
        }

        if (_renderedState is null || !_renderedState.Equals(state) || string.IsNullOrWhiteSpace(_editedPreviewPath) || !File.Exists(_editedPreviewPath))
        {
            _playback.Unload();
            DeleteEditedPreview();
            var path = Path.Combine(Path.GetTempPath(), $"voxarchive-editor-preview-{Guid.NewGuid():N}.wav");
            await AudioFileRenderService.RenderWaveAsync(
                sourceFilePath,
                path,
                state,
                AudioRenderChannelMode.Stereo,
                masterGainDb: 0d,
                cancellationToken);
            _editedPreviewPath = path;
            _renderedState = state;
        }

        if (!_isEditedMode || !_playback.IsLoaded)
        {
            _playback.Load(_editedPreviewPath!);
        }

        _isEditedMode = true;
        _playback.SetPlaybackSpeed(speed);
        _playback.SetGains(0d, 0d);
        _playback.SetMixToMono(false);
        _playback.Play();
    }

    public void PlayOriginal(string sourceFilePath, double speed)
    {
        if (_isEditedMode || !_playback.IsLoaded)
        {
            _playback.Load(sourceFilePath);
        }

        _isEditedMode = false;
        _playback.SetPlaybackSpeed(speed);
        _playback.SetGains(0d, 0d);
        _playback.SetMixToMono(false);
        _playback.Play();
    }

    public void Pause() => _playback.Pause();

    public void Stop() => _playback.Stop();

    public void Seek(TimeSpan position) => _playback.Seek(position);

    public void SetPlaybackSpeed(double speed) => _playback.SetPlaybackSpeed(speed);

    public void InvalidateEditedPreview()
    {
        _renderedState = null;
        if (_isEditedMode)
        {
            _playback.Unload();
        }
        DeleteEditedPreview();
    }

    private void DeleteEditedPreview()
    {
        if (string.IsNullOrWhiteSpace(_editedPreviewPath)) return;
        try
        {
            if (File.Exists(_editedPreviewPath)) File.Delete(_editedPreviewPath);
        }
        catch
        {
            // Preview一時ファイルの削除失敗はEditorの終了を妨げない。
        }
        _editedPreviewPath = null;
    }

    public void Dispose()
    {
        _playback.Dispose();
        DeleteEditedPreview();
    }
}
