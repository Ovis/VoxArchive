namespace VoxArchive.Wpf;

/// <summary>
/// Audio Editorの書き出しをアプリ全体で1件に制限する。
/// </summary>
/// <remarks>
/// 仕様上Queueは持たない。既に書き出し中なら待機せず失敗を返し、UI側で利用者へ通知する。
/// </remarks>
public sealed class AudioExportCoordinator
{
    private int _isExporting;

    public event EventHandler? ExportStateChanged;

    public bool IsExporting => Volatile.Read(ref _isExporting) != 0;

    public bool TryEnter(out IDisposable? lease)
    {
        if (Interlocked.CompareExchange(ref _isExporting, 1, 0) != 0)
        {
            lease = null;
            return false;
        }

        lease = new Lease(this);
        ExportStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Exit()
    {
        if (Interlocked.Exchange(ref _isExporting, 0) != 0)
        {
            ExportStateChanged?.Invoke(this, EventArgs.Empty);
        }
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
