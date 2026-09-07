using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// queued/running Jobがモデル操作から保護されるためのreservation lifecycleを検証する
/// </summary>
[TestFixture]
public sealed class TranscriptionModelUsageTrackerTests
{
    /// <summary>
    /// 最初のreservation取得から最後のreservation解放までモデルが使用中として扱われることを確認する
    /// </summary>
    [Test]
    public void Reservation_ProtectsModelUntilLastOwnerReleases()
    {
        var tracker = new TranscriptionModelUsageTracker();
        var key = new TranscriptionModelKey(
            new TranscriptionEngineId("whisper"),
            new TranscriptionModelId("small"));

        using var first = tracker.Acquire(key);
        var second = tracker.Acquire(key);

        Assert.That(tracker.IsInUse(key), Is.True);

        second.Dispose();
        Assert.That(tracker.IsInUse(key), Is.True);

        first.Dispose();
        Assert.That(tracker.IsInUse(key), Is.False);
    }

    /// <summary>
    /// reservationを複数回Disposeしても他Jobのreservation countを減らさないことを確認する
    /// </summary>
    [Test]
    public void Reservation_DisposeIsIdempotent()
    {
        var tracker = new TranscriptionModelUsageTracker();
        var key = new TranscriptionModelKey(
            new TranscriptionEngineId("reazonspeech"),
            new TranscriptionModelId("ja"));
        var reservation = tracker.Acquire(key);

        reservation.Dispose();
        reservation.Dispose();

        Assert.That(tracker.IsInUse(key), Is.False);
    }
}
