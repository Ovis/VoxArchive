using System.Text.Json;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Model Managerがdownload共有と文字起こし・モデル管理のglobal排他を正しく調停することを確認する
/// </summary>
public sealed class TranscriptionModelManagerTests
{
    private static readonly TranscriptionEngineId EngineId = new("test-model-engine");
    private static readonly TranscriptionModelId ModelId = new("model-a");
    private static readonly TranscriptionModelKey ModelKey = new(EngineId, ModelId);

    [Test]
    public async Task InstallAsync_SameModelConcurrentRequests_ShareSingleOwnerDownload()
    {
        var provider = new ControlledModelProvider();
        var (manager, _) = CreateManager(provider);

        var owner = manager.InstallAsync(ModelKey, force: false);
        var waiter = manager.InstallAsync(ModelKey, force: false);

        Assert.That(provider.InstallCallCount, Is.EqualTo(1));
        Assert.That(manager.GetActiveDownload()?.WaiterCount, Is.EqualTo(1));

        provider.CompleteInstall();
        var results = await Task.WhenAll(owner, waiter);

        Assert.Multiple(() =>
        {
            Assert.That(provider.InstallCallCount, Is.EqualTo(1));
            Assert.That(results[0], Is.EqualTo(results[1]));
            Assert.That(manager.GetActiveDownload(), Is.Null);
        });
    }

    [Test]
    public void ReservedModel_CannotBeDeletedOrReverified()
    {
        var provider = new ControlledModelProvider(isReady: true);
        var (manager, usageTracker) = CreateManager(provider);
        using var reservation = usageTracker.Acquire(ModelKey);

        Assert.Multiple(() =>
        {
            Assert.That(
                async () => await manager.DeleteAsync(ModelKey),
                Throws.TypeOf<InvalidOperationException>());
            Assert.That(
                () => manager.Reverify(ModelKey),
                Throws.TypeOf<InvalidOperationException>());
        });
    }

    [Test]
    public void AnyTranscriptionReservation_BlocksAllModelManagementOperations()
    {
        var provider = new ControlledModelProvider(isReady: true);
        var (manager, usageTracker) = CreateManager(provider);
        var otherKey = new TranscriptionModelKey(EngineId, new TranscriptionModelId("different-model"));
        using var reservation = usageTracker.Acquire(otherKey);

        Assert.Multiple(() =>
        {
            Assert.That(
                async () => await manager.InstallAsync(ModelKey, force: true),
                Throws.TypeOf<InvalidOperationException>());
            Assert.That(
                async () => await manager.DeleteAsync(ModelKey),
                Throws.TypeOf<InvalidOperationException>());
            Assert.That(
                () => manager.Reverify(ModelKey),
                Throws.TypeOf<InvalidOperationException>());
        });
    }

    [Test]
    public async Task ActiveDownload_BlocksAdmissionStyleDirectReservation()
    {
        var provider = new ControlledModelProvider();
        var (manager, usageTracker) = CreateManager(provider);
        var download = manager.InstallAsync(ModelKey, force: false);

        // 現行AdmissionはUsageTrackerを直接利用するため、その経路でもblockされることを確認する。
        Assert.That(
            () => usageTracker.Acquire(ModelKey),
            Throws.TypeOf<InvalidOperationException>());

        provider.CompleteInstall();
        await download;

        using var reservation = usageTracker.Acquire(ModelKey);
        Assert.That(manager.IsInUse(ModelKey), Is.True);
    }

    [Test]
    public void ReadinessUsesLightweightCheck_ExplicitReverifyUsesHashInspection()
    {
        var provider = new ControlledModelProvider(isReady: true);
        var (manager, _) = CreateManager(provider);

        var ready = manager.IsReady(ModelKey);

        Assert.Multiple(() =>
        {
            Assert.That(ready, Is.True);
            Assert.That(provider.IsReadyCallCount, Is.EqualTo(1));
            Assert.That(provider.HashInspectionCallCount, Is.Zero);
        });

        var inspection = manager.Reverify(ModelKey);

        Assert.Multiple(() =>
        {
            Assert.That(inspection.Level, Is.EqualTo(TranscriptionModelInspectionLevel.Hash));
            Assert.That(provider.HashInspectionCallCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ActiveDelete_IsExposedAndShutdownWaitCompletesOnlyAfterDeleteFinishes()
    {
        var provider = new ControlledModelProvider(isReady: true, blockDelete: true);
        var (manager, _) = CreateManager(provider);

        var delete = manager.DeleteAsync(ModelKey);
        await provider.DeleteStarted;

        var active = manager.GetActiveExclusiveOperation();
        var shutdownWait = manager.WaitForActiveExclusiveOperationAsync();

        Assert.Multiple(() =>
        {
            Assert.That(active, Is.Not.Null);
            Assert.That(active!.Key, Is.EqualTo(ModelKey));
            Assert.That(active.OperationName, Is.EqualTo("削除"));
            Assert.That(shutdownWait.IsCompleted, Is.False);
        });

        provider.CompleteDelete();
        await delete;
        await shutdownWait;

        Assert.That(manager.GetActiveExclusiveOperation(), Is.Null);
    }

    private static (TranscriptionModelManager Manager, TranscriptionModelUsageTracker UsageTracker) CreateManager(
        ControlledModelProvider provider)
    {
        var registration = new TranscriptionEngineRegistration(
            new FakeEngine(),
            new FakeSettingsProvider(),
            provider,
            new FakeModelRequirementResolver());
        var registry = new TranscriptionEngineRegistry([registration]);
        var usageTracker = new TranscriptionModelUsageTracker();
        return (new TranscriptionModelManager(registry, usageTracker), usageTracker);
    }

    private sealed record FakeOptions : ITranscriptionEngineOptions;

    private sealed class FakeEngine : ITranscriptionEngine
    {
        public TranscriptionEngineId Id => EngineId;
        public TranscriptionAudioRequirements AudioRequirements { get; } = new(16_000, 1, TranscriptionSampleFormat.Pcm16);

        public Task<TranscriptionEngineResult> TranscribeAsync(
            TranscriptionEngineRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TranscriptionEngineResult([]));
    }

    private sealed class FakeSettingsProvider : ITranscriptionEngineSettingsProvider
    {
        public ITranscriptionEngineOptions Deserialize(JsonElement settings, int schemaVersion) => new FakeOptions();
        public JsonElement Serialize(ITranscriptionEngineOptions options) => JsonSerializer.SerializeToElement(new { });
        public IReadOnlyList<TranscriptionValidationError> Validate(ITranscriptionEngineOptions options) => [];
    }

    private sealed class FakeModelRequirementResolver : ITranscriptionModelRequirementResolver
    {
        public TranscriptionModelId ResolveRequiredModel(ITranscriptionEngineOptions options) => ModelId;
        public ITranscriptionEngineOptions SelectModel(ITranscriptionEngineOptions options, TranscriptionModelId modelId) => options;
        public ITranscriptionEngineOptions BindInstallation(ITranscriptionEngineOptions options, TranscriptionModelInstallation installation) => options;
    }

    private sealed class ControlledModelProvider(bool isReady = false, bool blockDelete = false) : ITranscriptionModelProvider
    {
        private readonly TaskCompletionSource<TranscriptionModelInstallation> _installation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _deleteStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _deleteCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int InstallCallCount { get; private set; }
        public int IsReadyCallCount { get; private set; }
        public int HashInspectionCallCount { get; private set; }
        public TranscriptionEngineId EngineId => TranscriptionModelManagerTests.EngineId;
        public Task DeleteStarted => _deleteStarted.Task;

        public IReadOnlyList<TranscriptionModelDescriptor> GetAvailableModels()
            => [new(ModelId, "Model A")];

        public bool IsReady(TranscriptionModelId modelId)
        {
            IsReadyCallCount++;
            return isReady;
        }

        public TranscriptionModelInspection Inspect(
            TranscriptionModelId modelId,
            TranscriptionModelInspectionLevel level)
        {
            if (level == TranscriptionModelInspectionLevel.Hash)
            {
                HashInspectionCallCount++;
            }
            return new(
                isReady ? TranscriptionModelPackageState.Installed : TranscriptionModelPackageState.Missing,
                level);
        }

        public Task<TranscriptionModelInstallation> InstallAsync(
            TranscriptionModelId modelId,
            bool force,
            IProgress<TranscriptionModelTransferProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            InstallCallCount++;
            return _installation.Task.WaitAsync(cancellationToken);
        }

        public async Task DeleteAsync(TranscriptionModelId modelId, CancellationToken cancellationToken = default)
        {
            _deleteStarted.TrySetResult();
            if (!blockDelete)
            {
                return;
            }

            // 削除途中を終了処理が強制停止しないことを検証するため、テスト側から解放されるまで処理を保持する。
            await _deleteCompletion.Task.WaitAsync(cancellationToken);
        }

        public TranscriptionModelInstallation GetInstallation(TranscriptionModelId modelId)
            => new(EngineId, modelId, ["model-a.bin"]);

        public void CompleteInstall() => _installation.TrySetResult(GetInstallation(ModelId));
        public void CompleteDelete() => _deleteCompletion.TrySetResult();
    }
}
