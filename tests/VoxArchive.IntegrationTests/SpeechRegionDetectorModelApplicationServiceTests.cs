using VoxArchive.Application;
using VoxArchive.Transcription;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// 発話検出モデル操作をApplication scopeで追跡し、終了要求時に安全にcancel・完了待機できることを確認する
/// </summary>
public sealed class SpeechRegionDetectorModelApplicationServiceTests
{
    [Test]
    public async Task CancelActiveOperationAndWaitAsync_Download中はCancelして完了を待つ()
    {
        var manager = new BlockingModelManager();
        var service = new SpeechRegionDetectorModelApplicationService(manager);
        var installTask = service.InstallAsync(force: false);
        await manager.DownloadStarted.Task;

        var active = service.GetActiveOperation();
        var shutdownTask = service.CancelActiveOperationAndWaitAsync();
        await shutdownTask;

        Assert.Multiple(() =>
        {
            Assert.That(active, Is.Not.Null);
            Assert.That(active!.CanCancel, Is.True);
            Assert.That(manager.CancellationObserved, Is.True);
            Assert.That(service.GetActiveOperation(), Is.Null);
        });
        Assert.That(async () => await installTask, Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task CancelActiveOperationAndWaitAsync_Validation中は完了まで待ってCommit前Cancelを維持する()
    {
        var manager = new BlockingModelManager { EnterValidation = true };
        var service = new SpeechRegionDetectorModelApplicationService(manager);
        var installTask = service.InstallAsync(force: false);
        await manager.ValidationStarted.Task;

        var active = service.GetActiveOperation();
        var shutdownTask = service.CancelActiveOperationAndWaitAsync();
        await Task.Delay(20);

        Assert.Multiple(() =>
        {
            Assert.That(active, Is.Not.Null);
            Assert.That(active!.CanCancel, Is.False);
            Assert.That(shutdownTask.IsCompleted, Is.False, "native validation完了前に終了待機を抜けてはいけない");
        });

        manager.ValidationRelease.TrySetResult();
        await shutdownTask;

        Assert.Multiple(() =>
        {
            Assert.That(manager.CancellationObserved, Is.True, "validation後のcommit判定でcancelを観測する必要がある");
            Assert.That(service.GetActiveOperation(), Is.Null);
        });
        Assert.That(async () => await installTask, Throws.InstanceOf<OperationCanceledException>());
    }

    private sealed class BlockingModelManager : ISpeechRegionDetectorModelManager
    {
        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ValidationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ValidationRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool EnterValidation { get; init; }
        public bool CancellationObserved { get; private set; }

        public SpeechRegionDetectorModelState GetState() => SpeechRegionDetectorModelState.Missing;

        public SpeechRegionDetectorModelState Recheck() => SpeechRegionDetectorModelState.Available;

        public async Task InstallAsync(
            bool force,
            IProgress<ManagedModelTransactionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadStarted.TrySetResult();
            if (EnterValidation)
            {
                progress?.Report(new ManagedModelTransactionProgress(100, 100, null, IsValidating: true));
                ValidationStarted.TrySetResult();

                // native validationはCancellationTokenで強制停止できない実装を模擬する。
                await ValidationRelease.Task;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }
                return;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public void Delete()
        {
        }
    }
}
