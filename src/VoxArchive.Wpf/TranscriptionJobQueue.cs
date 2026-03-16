using System.Threading.Channels;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

public sealed class TranscriptionJobQueue : IDisposable
{
    private readonly WhisperTranscriptionService _transcriptionService;
    private readonly Channel<TranscriptionJobRequest> _queue;
    private readonly CancellationTokenSource _cts;
    private readonly Task _workerTask;

    public TranscriptionJobQueue(WhisperTranscriptionService transcriptionService)
    {
        _transcriptionService = transcriptionService;
        _queue = Channel.CreateUnbounded<TranscriptionJobRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _cts = new CancellationTokenSource();
        _workerTask = Task.Run(WorkerLoopAsync);
    }

    public event EventHandler<TranscriptionJobCompletedEventArgs>? JobCompleted;

    public bool TryEnqueue(TranscriptionJobRequest request)
    {
        return _queue.Writer.TryWrite(request);
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_cts.Token))
            {
                while (_queue.Reader.TryRead(out var request))
                {
                    var result = await ProcessAsync(request, _cts.Token);
                    JobCompleted?.Invoke(this, new TranscriptionJobCompletedEventArgs(request, result));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // no-op
        }
        catch (Exception)
        {
        }
    }

    private async Task<TranscriptionJobResult> ProcessAsync(TranscriptionJobRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var priority = request.Trigger == TranscriptionTrigger.AutoAfterRecord
                ? request.Options.AutoTranscriptionPriority
                : request.Options.ManualTranscriptionPriority;

            if (priority == TranscriptionPriority.Low)
            {
                await Task.Delay(300, cancellationToken);
            }

            return await _transcriptionService.TranscribeAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new TranscriptionJobResult(
                Succeeded: false,
                Message: "文字起こし処理がキャンセルされました。",
                GeneratedFiles: Array.Empty<string>(),
                StartedAt: DateTimeOffset.Now,
                FinishedAt: DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            return new TranscriptionJobResult(
                Succeeded: false,
                Message: $"文字起こし実行中に例外が発生しました: {ex.Message}",
                GeneratedFiles: Array.Empty<string>(),
                StartedAt: DateTimeOffset.Now,
                FinishedAt: DateTimeOffset.Now);
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }

        try
        {
            _workerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore
        }

        _cts.Dispose();
    }
}

public sealed record TranscriptionJobCompletedEventArgs(
    TranscriptionJobRequest Request,
    TranscriptionJobResult Result);


