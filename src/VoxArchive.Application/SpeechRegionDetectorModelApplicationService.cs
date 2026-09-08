using VoxArchive.Application.Abstractions;
using VoxArchive.Transcription;

namespace VoxArchive.Application;

/// <summary>
/// 共通発話検出モデル管理をPresentation向けDTOへ投影する
/// </summary>
/// <remarks>
/// ApplicationはSilero具象型を知らず、Transcription層の共通モデル管理契約だけに依存する。
/// これにより将来detector実装を差し替えてもWPFの操作契約を変更せずに済む。
/// モデル操作はApplication scopeで追跡し、SettingsWindowの寿命とアプリ終了処理を分離する。
/// </remarks>
public sealed class SpeechRegionDetectorModelApplicationService(
    ISpeechRegionDetectorModelManager modelManager) : ISpeechRegionDetectorModelApplicationService
{
    private readonly object _operationGate = new();
    private ActiveOperation? _activeOperation;

    /// <inheritdoc />
    public SpeechRegionDetectorModelStatusInfo Inspect()
        => ToStatus(modelManager.GetState());

    /// <inheritdoc />
    public Task<SpeechRegionDetectorModelStatusInfo> ReverifyAsync(CancellationToken cancellationToken = default)
    {
        var operation = BeginOperation("モデル再確認", canCancel: false, cancellationToken);
        operation.Task = Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ToStatus(modelManager.Recheck());
            }
            finally
            {
                EndOperation(operation);
            }
        }, cancellationToken);
        return (Task<SpeechRegionDetectorModelStatusInfo>)operation.Task;
    }

    /// <inheritdoc />
    public Task InstallAsync(
        bool force,
        IProgress<SpeechRegionDetectorModelTransferInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var operation = BeginOperation(force ? "モデル再取得" : "モデル取得", canCancel: true, cancellationToken);
        var adapter = progress is null
            ? null
            : new Progress<ManagedModelTransactionProgress>(x =>
            {
                if (x.IsValidating)
                {
                    // native validationは安全に中断できないため、終了時はcancel要求だけ記録して完了まで待つ。
                    operation.CanCancel = false;
                }

                progress.Report(new SpeechRegionDetectorModelTransferInfo(
                    x.BytesReceived,
                    x.TotalBytes,
                    x.CurrentFileName,
                    x.IsValidating));
            });

        operation.Task = RunInstallAsync(operation, force, adapter);
        return operation.Task;
    }

    /// <inheritdoc />
    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        var operation = BeginOperation("モデル削除", canCancel: false, cancellationToken);
        operation.Task = Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                modelManager.Delete();
            }
            finally
            {
                EndOperation(operation);
            }
        }, cancellationToken);
        return operation.Task;
    }

    /// <inheritdoc />
    public SpeechRegionDetectorModelOperationInfo? GetActiveOperation()
    {
        lock (_operationGate)
        {
            return _activeOperation is null
                ? null
                : new SpeechRegionDetectorModelOperationInfo(
                    _activeOperation.OperationName,
                    _activeOperation.CanCancel);
        }
    }

    /// <inheritdoc />
    public async Task CancelActiveOperationAndWaitAsync()
    {
        ActiveOperation? operation;
        lock (_operationGate)
        {
            operation = _activeOperation;
            if (operation is null)
            {
                return;
            }

            // download中だけcancelを通知する。validationへ入った後はnative処理を強制停止せず、その完了を待つ。
            if (operation.CanCancel && !operation.Cancellation.IsCancellationRequested)
            {
                operation.Cancellation.Cancel();
            }
        }

        try
        {
            await operation.Task;
        }
        catch (OperationCanceledException)
        {
            // 終了調停ではcancel成功を正常な終了条件として扱う。
        }
    }

    private async Task RunInstallAsync(
        ActiveOperation operation,
        bool force,
        IProgress<ManagedModelTransactionProgress>? progress)
    {
        try
        {
            await modelManager.InstallAsync(force, progress, operation.Cancellation.Token);
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private ActiveOperation BeginOperation(
        string operationName,
        bool canCancel,
        CancellationToken cancellationToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operation = new ActiveOperation(operationName, canCancel, cancellation);
        lock (_operationGate)
        {
            if (_activeOperation is not null)
            {
                cancellation.Dispose();
                throw new InvalidOperationException($"発話検出モデルの別操作を実行中です: {_activeOperation.OperationName}");
            }

            _activeOperation = operation;
        }

        return operation;
    }

    private void EndOperation(ActiveOperation operation)
    {
        lock (_operationGate)
        {
            if (ReferenceEquals(_activeOperation, operation))
            {
                _activeOperation = null;
            }
        }
        operation.Cancellation.Dispose();
    }

    private static SpeechRegionDetectorModelStatusInfo ToStatus(SpeechRegionDetectorModelState state)
        => new(
            state.ToString(),
            state == SpeechRegionDetectorModelState.Available);

    private sealed class ActiveOperation(
        string operationName,
        bool canCancel,
        CancellationTokenSource cancellation)
    {
        public string OperationName { get; } = operationName;
        public bool CanCancel { get; set; } = canCancel;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }
}
