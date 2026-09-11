namespace VoxArchive.Wpf;

/// <summary>
/// Audio Editorの書き出しをアプリ全体で1件に制限する。
/// </summary>
public sealed class AudioExportCoordinator
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private int _isExporting;

    public bool IsExporting => Volatile.Read(ref _isExporting) != 0;

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        Interlocked.Exchange(ref _isExporting, 1);
        return new Lease(this);
    }

    private void Exit()
    {
        Interlocked.Exchange(ref _isExporting, 0);
        _semaphore.Release();
    }

    private sealed class Lease(AudioExportCoordinator owner) : IDisposable
    {
        private AudioExportCoordinator? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Exit();
        }
    }
}
