using VoxArchive.Application;
using VoxArchive.Application.Abstractions;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// 文字起こしQueueの逐次実行、優先度、重複排除、terminal outcome、model reservation解放を確認する
/// </summary>
public sealed class TranscriptionJobQueueTests
{
    [Test]
    public async Task TryEnqueueAsync_TwoJobs_RunSequentially_RejectDuplicate_AndReleaseReservations()
    {
        var root = CreateTempDirectory();
        try
        {
            var first = TranscriptionPipelineTestFixture.CreateWaveFile(root, "first.wav");
            var second = TranscriptionPipelineTestFixture.CreateWaveFile(root, "second.wav");
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = 0;
            var maxRunning = 0;
            var completionCount = 0;

            using var context = TranscriptionPipelineTestFixture.CreatePipeline(async (_, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref running);
                maxRunning = Math.Max(maxRunning, current);
                firstStarted.TrySetResult();
                try
                {
                    await gate.Task.WaitAsync(cancellationToken);
                    return new TranscriptionEngineResult([]);
                }
                finally
                {
                    Interlocked.Decrement(ref running);
                }
            });

            context.Queue.JobCompleted += (_, _) =>
            {
                if (Interlocked.Increment(ref completionCount) == 2)
                {
                    completed.TrySetResult();
                }
            };

            var options = TranscriptionPipelineTestFixture.CreateOptions();
            var firstResult = await context.Queue.TryEnqueueAsync(first, options, TranscriptionTrigger.Manual);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var duplicate = await context.Queue.TryEnqueueAsync(first, options, TranscriptionTrigger.Manual);
            var secondResult = await context.Queue.TryEnqueueAsync(second, options, TranscriptionTrigger.Manual);
            var modelKey = new TranscriptionModelKey(
                TranscriptionPipelineTestFixture.EngineId,
                TranscriptionPipelineTestFixture.ModelId);

            Assert.Multiple(() =>
            {
                Assert.That(firstResult.Enqueued, Is.True);
                Assert.That(duplicate.Enqueued, Is.False);
                Assert.That(duplicate.Message, Does.Contain("既に文字起こし待機中または実行中"));
                Assert.That(secondResult.Enqueued, Is.True);
                Assert.That(context.UsageTracker.IsInUse(modelKey), Is.True);
            });

            gate.TrySetResult();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.That(maxRunning, Is.EqualTo(1));
                Assert.That(context.Queue.GetStateSnapshot(), Is.Empty);
                Assert.That(context.UsageTracker.IsInUse(modelKey), Is.False);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TryEnqueueAsync_NormalRunsBeforeLow_AndSamePriorityUsesFifo()
    {
        var root = CreateTempDirectory();
        try
        {
            var blocker = TranscriptionPipelineTestFixture.CreateWaveFile(root, "blocker.wav");
            var lowFirst = TranscriptionPipelineTestFixture.CreateWaveFile(root, "low-first.wav");
            var normalFirst = TranscriptionPipelineTestFixture.CreateWaveFile(root, "normal-first.wav");
            var normalSecond = TranscriptionPipelineTestFixture.CreateWaveFile(root, "normal-second.wav");
            var blockerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completionOrder = new List<string>();
            var invocationCount = 0;
            var completionCount = 0;

            using var context = TranscriptionPipelineTestFixture.CreatePipeline(async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref invocationCount) == 1)
                {
                    blockerStarted.TrySetResult();
                    await releaseBlocker.Task.WaitAsync(cancellationToken);
                }

                return new TranscriptionEngineResult([]);
            });
            context.Queue.JobCompleted += (_, e) =>
            {
                lock (completionOrder)
                {
                    completionOrder.Add(Path.GetFileName(e.Job.AudioFilePath));
                }

                if (Interlocked.Increment(ref completionCount) == 4)
                {
                    completed.TrySetResult();
                }
            };

            var options = TranscriptionPipelineTestFixture.CreateOptions();
            await context.Queue.TryEnqueueAsync(blocker, options, TranscriptionTrigger.Manual);
            await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // blocker実行中にLow→Normal→Normalの順で積み、PriorityQueueがNormalを先にしつつFIFOを維持することを固定する。
            await context.Queue.TryEnqueueAsync(lowFirst, options, TranscriptionTrigger.AutoAfterRecord);
            await context.Queue.TryEnqueueAsync(normalFirst, options, TranscriptionTrigger.Manual);
            await context.Queue.TryEnqueueAsync(normalSecond, options, TranscriptionTrigger.Manual);
            releaseBlocker.TrySetResult();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(
                completionOrder,
                Is.EqualTo(new[] { "blocker.wav", "normal-first.wav", "normal-second.wav", "low-first.wav" }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task JobCompleted_EngineException_IsMappedToFailedAndClearsState()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = TranscriptionPipelineTestFixture.CreateWaveFile(root, "failure.wav");
            using var context = TranscriptionPipelineTestFixture.CreatePipeline((_, _) =>
                throw new InvalidOperationException("engine failed"));
            var completed = new TaskCompletionSource<TranscriptionJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Queue.JobCompleted += (_, e) => completed.TrySetResult(e.Result);

            var enqueue = await context.Queue.TryEnqueueAsync(
                source,
                TranscriptionPipelineTestFixture.CreateOptions(),
                TranscriptionTrigger.Manual);
            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.That(enqueue.Enqueued, Is.True);
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.Outcome, Is.EqualTo(TranscriptionJobOutcome.Failed));
                Assert.That(result.Message, Does.Contain("engine failed"));
                Assert.That(context.Queue.GetStateSnapshot(), Is.Empty);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Cancel_RunningJob_IsMappedToCancelledAndClearsState()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = TranscriptionPipelineTestFixture.CreateWaveFile(root, "cancel.wav");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var context = TranscriptionPipelineTestFixture.CreatePipeline(async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new TranscriptionEngineResult([]);
            });
            var completed = new TaskCompletionSource<TranscriptionJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Queue.JobCompleted += (_, e) => completed.TrySetResult(e.Result);

            var enqueue = await context.Queue.TryEnqueueAsync(
                source,
                TranscriptionPipelineTestFixture.CreateOptions(),
                TranscriptionTrigger.Manual);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cancelAccepted = context.Queue.Cancel(source);
            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.That(enqueue.Enqueued, Is.True);
                Assert.That(cancelAccepted, Is.True);
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.Outcome, Is.EqualTo(TranscriptionJobOutcome.Cancelled));
                Assert.That(context.Queue.GetStateSnapshot(), Is.Empty);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"voxarchive-queue-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
