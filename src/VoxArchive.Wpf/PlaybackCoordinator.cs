namespace VoxArchive.Wpf;

/// <summary>
/// アプリ内の再生排他を管理する。
/// </summary>
/// <remarks>
/// LibraryやAudio Editorが個別に再生サービスを持っていても、実際に音を出すのは常に1つだけとする。
/// 別の再生が開始された場合は以前の再生を一時停止し、再生位置は保持する。
/// </remarks>
public sealed class PlaybackCoordinator
{
    private readonly object _gate = new();
    private RecordingPlaybackService? _active;

    internal void Activate(RecordingPlaybackService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        RecordingPlaybackService? previous;
        lock (_gate)
        {
            if (ReferenceEquals(_active, service))
            {
                return;
            }

            previous = _active;
            _active = service;
        }

        previous?.PauseForArbitration();
    }

    internal void Release(RecordingPlaybackService service)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_active, service))
            {
                _active = null;
            }
        }
    }
}
